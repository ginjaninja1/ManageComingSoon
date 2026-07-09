// ManageComingSoon - Emby Library Service

namespace ManageComingSoon.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using ManageComingSoon.Model;
    using MediaBrowser.Common.Net;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Persistence;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;

    public class EmbyLibraryService
    {
        // Tag text is read from config on every use so a change on the
        // Configuration tab takes effect for the next pipeline run without
        // a server restart.
        private string ActiveTagText
        {
            get
            {
                var cfg = ManageComingSoonPlugin.Instance?.Configuration;
                if (cfg == null || string.IsNullOrEmpty(cfg.ComingSoonTagText))
                    return "Coming Soon";
                return cfg.ComingSoonTagText;
            }
        }

        private const string StubResourceName = "ManageComingSoon.comingsoon.mp4";
        private const string BootstrapStubFileName = "_placeholder_.mp4";

        // ---- Ingest-poll presets ----
        // The right timeout depends on what Emby actually has to DO to satisfy the
        // probe, not on which refresh mode triggered it. A folder with no new
        // media in it (an empty bootstrap folder, or confirming a folder/row has
        // disappeared) is a DB/index check - normally near-instant, occasionally a
        // few seconds on a slow disk. A folder containing a real, previously-
        // unseen video file needs Emby to actually open and probe that file (and,
        // for Normal mode, run a full fresh metadata/image identification pass
        // with no provider ids to go on) - that needs real headroom.
        private const int NoNewContentFirstWaitSeconds = 2;
        private const int NoNewContentSecondWaitSeconds = 7;
        private const int NewContentFirstWaitSeconds = 20;
        private const int NewContentSecondWaitSeconds = 60;

        // Add Movie [Method B] only - deliberately more aggressive than the
        // shared NewContent* pair above (which Make Live's ConfirmTargetState
        // stage also uses and which stays at 20s/60s). Tune this array only;
        // do not point AddPipeline at NewContentFirstWaitSeconds/
        // NewContentSecondWaitSeconds or Make Live's timing changes too.
        // Three passes (rather than two) so a fast-ingesting server can
        // confirm quickly without giving up the extra headroom a slower
        // server might need on the final pass.
        // Pass 3 bumped 30s -> 60s after a real-world batch add showed one
        // item (out of five, otherwise-identical stub files) get picked up
        // by Emby as a genuine "date modified change" instead of the cheap
        // new-stub path the rest took - that triggers a real ffprobe
        // (-show_data) which can run well past 30s. The other four movies
        // in that same batch all confirmed at ~2s, so passes 1/2 stay as-is;
        // only the worst-case tail needed more room.
        private const int AddPipelineIngestPass1Seconds = 2;
        private const int AddPipelineIngestPass2Seconds = 7;
        private const int AddPipelineIngestPass3Seconds = 60;
        private static readonly int[] AddPipelineIngestPassSeconds =
        {
            AddPipelineIngestPass1Seconds,
            AddPipelineIngestPass2Seconds,
            AddPipelineIngestPass3Seconds,
        };

        private readonly IServerApplicationHost appHost;
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly ILogger logger;

        public EmbyLibraryService(
            IServerApplicationHost appHost,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILibraryMonitor libraryMonitor,
            ILogger logger)
        {
            this.appHost = appHost;
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.libraryMonitor = libraryMonitor;
            this.logger = logger;
        }

        // -----------------------------------------------------------------------
        // Steps 1-3: Create placeholder, register pending path, scan
        // Step 4 (tagging) is handled by ComingSoonEntryPoint.OnItemAdded
        // which fires AFTER Emby has fully committed the item's metadata.
        // -----------------------------------------------------------------------

        public async Task<string> AddComingSoonAsync(
            TmdbMovieResult movie,
            string libraryPath,
            string customStubPath,
            CancellationToken token)
        {
            string safeName = BuildComingSoonFolderName(movie.Title, movie.ReleaseYear);
            string folderPath = Path.Combine(libraryPath, safeName);

            // --- Step 1: Suppress LibraryMonitor BEFORE creating the folder ---
            // This prevents the monitor from resetting its debounce timer on every
            // filesystem write, which would delay ingest by 60-90 seconds.
            this.logger.Info("[Step 1] Suppressing LibraryMonitor for {0}", folderPath);
            this.libraryMonitor.ReportFileSystemChangeBeginning(folderPath);

            this.logger.Info("[Step 1] Creating placeholder at {0}", folderPath);
            try
            {
                this.fileSystem.CreateDirectory(folderPath);
                string stubFile = Path.Combine(folderPath, safeName + GetStubExtension(customStubPath));
                await WriteStubAsync(stubFile, customStubPath).ConfigureAwait(false);
                this.logger.Info("[Step 1] Folder and stub created successfully");
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[Step 1] Failed to create placeholder", ex);
                this.libraryMonitor.ReportFileSystemChangeComplete(folderPath, false);
                throw;
            }

            // --- Step 2: Register path so IServerEntryPoint tags it on ItemAdded ---
            this.logger.Info("[Step 2] Registering pending path for tagging: {0}", folderPath);
            ComingSoonEntryPoint.RegisterPendingPath(folderPath);

            // --- Step 3: Release suppression with refreshPath=true ---
            // This calls ReportFileSystemChanged(folderPath) immediately, creating a
            // FileRefresher with a short debounce timer (~1-3s) rather than the full
            // 60-90s LibraryMonitor debounce that would have fired from the write events.
            this.logger.Info("[Step 3] Releasing LibraryMonitor suppression with immediate refresh for {0}", folderPath);
            this.libraryMonitor.ReportFileSystemChangeComplete(folderPath, true);
            this.logger.Info("[Step 3] ReportFileSystemChangeComplete called – FileRefresher should start shortly");

            // Tag is applied by ComingSoonEntryPoint.OnItemAdded when ItemAdded fires.
            this.logger.Info("Placeholder created. Awaiting FileRefresher ingest and ItemAdded.");

            return folderPath;
        }

        // -----------------------------------------------------------------------
        // Add Coming Soon - new pipeline (parallel, NOT replacing AddComingSoonAsync)
        // -----------------------------------------------------------------------
        // AddComingSoonAsync above is kept fully intact and working - this is a
        // separate alternative being proven out alongside it, not a replacement
        // (deliberately different from how Make Live's old MakeLiveAsync was
        // fully retired once its replacement was proven - both Add methods stay
        // available side by side for now, by explicit choice).
        //
        // Two gaps in AddComingSoonAsync this addresses:
        //   1. It relies ENTIRELY on LibraryMonitor's own debounced FileRefresher
        //      to notice the new file - no explicit REST refresh trigger, unlike
        //      the proven CallRefreshEndpointAsync mechanism Make Live uses.
        //   2. It is pure fire-and-forget: once the folder/stub are written, it
        //      returns immediately and the ENTIRE completion signal is
        //      ComingSoonEntryPoint.OnItemAdded eventually firing - no poll, no
        //      timeout, no failure path. If that event never fires, the UI sits
        //      in "Awaiting Emby ingest and tagging..." forever.
        //
        // NOTE: this does NOT remove the dependency on
        // ComingSoonEntryPoint.OnItemAdded for the actual tag WRITE - that
        // remains the only code path that applies the Coming Soon tag (via
        // RegisterPendingPath below, unchanged). What changes is that this
        // pipeline actively POLLS for evidence the tag was applied, with a real
        // timeout, instead of the caller passively waiting forever.
        // -----------------------------------------------------------------------

        public enum AddComingSoonStage
        {
            ReadinessCheck = 0,
            WriteFiles = 1,
            RefreshTargetLibrary = 2,
            ConfirmIngestedAndTagged = 3,
            Complete = 4
        }

        public class AddComingSoonResult
        {
            public bool Success { get; set; }
            public AddComingSoonStage? FailedAtStage { get; set; }
            public string FailureReason { get; set; }
            public string FolderPath { get; set; }
            public Guid? FinalItemId { get; set; }
        }

        private static readonly double[] AddComingSoonStagePercent =
        {
            /* ReadinessCheck           */  5,
            /* WriteFiles               */ 30,
            /* RefreshTargetLibrary     */ 40,
            /* ConfirmIngestedAndTagged */ 95, // up to ~69s: 2s/7s/60s passes
            /* Complete                 */ 100
        };

        public async Task<AddComingSoonResult> AddComingSoonPipelineAsync(
            TmdbMovieResult movie,
            string targetPath,
            string customStubPath,
            string apiKey,
            IProgress<double> progress,
            Action<string> onStatus,
            CancellationToken token)
        {
            string safeName = BuildComingSoonFolderName(movie.Title, movie.ReleaseYear);
            string folderPath = Path.Combine(targetPath, safeName);

            this.logger.Info("[AddPipeline] ===== START ===== folder={0}", folderPath);

            try
            {
                ReportAdd(progress, AddComingSoonStage.ReadinessCheck);
                // Stage 1: the destination-conflict gate already ran in the page
                // view before this was ever called - checkpoint only, same
                // pattern as Make Live's Stage 1.

                try
                {
                    this.fileSystem.CreateDirectory(folderPath);
                    string stubFile = Path.Combine(folderPath, safeName + GetStubExtension(customStubPath));
                    await WriteStubAsync(stubFile, customStubPath).ConfigureAwait(false);
                    this.logger.Info("[AddPipeline] Folder and stub created: {0}", folderPath);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("[AddPipeline] Failed to create placeholder", ex);
                    return FailAdd(AddComingSoonStage.WriteFiles, "Failed to create folder/stub: " + ex.Message, folderPath);
                }

                // LibraryMonitor suppression - NOT used here, superseded by the
                // explicit REST refresh below. Commented out rather than deleted
                // (per the original AddComingSoonAsync pattern), in case it's
                // ever needed again:
                //
                // this.libraryMonitor.ReportFileSystemChangeBeginning(folderPath);
                // ... create folder/stub ...
                // this.libraryMonitor.ReportFileSystemChangeComplete(folderPath, true);

                // Still required: this is the ONLY mechanism that makes
                // ComingSoonEntryPoint.OnItemAdded apply the Coming Soon tag -
                // this pipeline polls for evidence of that below, it does not
                // apply the tag itself.
                ComingSoonEntryPoint.RegisterPendingPath(folderPath);
                this.logger.Info("[AddPipeline] Registered pending path for tagging: {0}", folderPath);

                ReportAdd(progress, AddComingSoonStage.WriteFiles);

                var targetLibrary = FindCollectionFolder(targetPath);
                if (targetLibrary == null)
                {
                    this.logger.Warn("[AddPipeline] Could not locate target CollectionFolder for {0}", targetPath);
                    return FailAdd(AddComingSoonStage.RefreshTargetLibrary, "Could not locate the target library.", folderPath);
                }

                if (string.IsNullOrEmpty(apiKey))
                {
                    this.logger.Warn("[AddPipeline] No Emby API key configured - cannot call the refresh endpoint.");
                    return FailAdd(AddComingSoonStage.RefreshTargetLibrary, "No Emby API key configured.", folderPath);
                }
                string baseUrl = await this.appHost.GetLocalApiUrl(token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(baseUrl))
                {
                    this.logger.Warn("[AddPipeline] GetLocalApiUrl returned empty.");
                    return FailAdd(AddComingSoonStage.RefreshTargetLibrary, "Could not resolve the local Emby API URL.", folderPath);
                }
                var httpClient = this.appHost.Resolve<IHttpClient>();

                try
                {
                    // ValidationOnly, not Default: this call only needs to make
                    // Emby register the new path and apply the tag (Step 4, via
                    // ComingSoonEntryPoint.OnItemAdded) - that registration +
                    // tag IS success for this plugin. Default mode also runs a
                    // full metadata/image identification pass against TMDB (and
                    // whatever other providers are configured), which this call
                    // does not need and must not be exposed to: if a provider is
                    // slow or offline, a Default-mode refresh hangs on it and
                    // this whole pipeline times out and errors the row even
                    // though the add itself would otherwise have succeeded.
                    // ValidationOnly never touches a provider, so a provider
                    // outage can no longer block or fail an add. (Make Live's
                    // EstablishTargetIds stage uses the same mode for the same
                    // reason - see Stage_EstablishTargetIdsAsync above.)
                    await CallRefreshEndpointAsync(httpClient, baseUrl, targetLibrary.InternalId,
                        "Library (Coming Soon target)", apiKey, MetadataRefreshMode.ValidationOnly,
                        "AddPipeline/RefreshTargetLibrary", token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("[AddPipeline] ValidationOnly refresh failed", ex);
                    return FailAdd(AddComingSoonStage.RefreshTargetLibrary, "Library refresh call failed: " + ex.Message, folderPath);
                }
                ReportAdd(progress, AddComingSoonStage.RefreshTargetLibrary);

                BaseItem taggedMovie = null;
                bool confirmed = await WaitForConditionAsync(
                    () =>
                    {
                        taggedMovie = FindMovieInFolder(folderPath);
                        return taggedMovie != null;
                    },
                    token, "AddPipeline/ConfirmIngestedAndTagged",
                    AddPipelineIngestPassSeconds,
                    onStatus).ConfigureAwait(false);

                if (!confirmed)
                    return FailAdd(AddComingSoonStage.ConfirmIngestedAndTagged,
                        "Item was not ingested and tagged within the timeout.", folderPath);

                LogItemState("AddPipeline - confirmed ingested and tagged", "Movie", taggedMovie);
                ReportAdd(progress, AddComingSoonStage.ConfirmIngestedAndTagged);

                this.logger.Info("[AddPipeline] ===== COMPLETE ===== '{0}'", movie.Title);
                ReportAdd(progress, AddComingSoonStage.Complete);

                // Nice-to-have, not a requirement: now that the item is safely
                // in the library and tagged (the actual success condition for
                // this plugin), ask Emby to also fetch full metadata/images.
                // Fired detached with its own CancellationToken.None - if a
                // provider is offline this can take as long as it likes, or
                // simply fail, without it ever being able to touch this
                // pipeline's result, the row's state, or AddMovieTracker
                // history. Best-effort only; errors are logged and swallowed.
                FireAndForgetFullRefresh(httpClient, baseUrl, targetLibrary.InternalId, apiKey, movie.Title);

                return new AddComingSoonResult
                {
                    Success = true,
                    FolderPath = folderPath,
                    FinalItemId = taggedMovie.Id
                };
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[AddPipeline] Unhandled exception", ex);
                return FailAdd(AddComingSoonStage.Complete, "Unhandled exception - see server log: " + ex.Message, folderPath);
            }
        }

        private static void ReportAdd(IProgress<double> progress, AddComingSoonStage stage)
        {
            if (progress != null)
                progress.Report(AddComingSoonStagePercent[(int)stage]);
        }

        private AddComingSoonResult FailAdd(AddComingSoonStage stage, string reason, string folderPath)
        {
            this.logger.Warn("[AddPipeline] FAILED at stage {0}: {1}", stage, reason);
            return new AddComingSoonResult
            {
                Success = false,
                FailedAtStage = stage,
                FailureReason = reason,
                FolderPath = folderPath
            };
        }

        // -----------------------------------------------------------------------
        // Add Coming Soon - BATCH ("Add All"). One library refresh for the whole
        // batch instead of one per movie.
        //
        // Looping AddComingSoonPipelineAsync per movie - which is what "Add All"
        // used to do - means N folders written produces N separate REST /Refresh
        // calls, each carrying its own timeout exposure to whatever provider
        // state Emby is in. That's what clogs Emby when adding several movies at
        // once, especially with a provider slow or offline: this batches the
        // write step for every movie first, then issues exactly ONE
        // ValidationOnly refresh against the target library covering all of
        // them, then confirms each folder's tag independently. The per-folder
        // confirmation polls are local libraryManager lookups, not server
        // round-trips, so running them concurrently (Task.WhenAll) costs Emby
        // nothing extra - it just stops one slow folder holding up everyone
        // else's confirmation.
        // -----------------------------------------------------------------------

        public class AddComingSoonBatchRequest
        {
            public string EntryId { get; set; }
            public TmdbMovieResult Movie { get; set; }
        }

        public class AddComingSoonBatchItemResult
        {
            public string EntryId { get; set; }
            public bool Success { get; set; }
            public AddComingSoonStage? FailedAtStage { get; set; }
            public string FailureReason { get; set; }
            public string FolderPath { get; set; }
            public Guid? FinalItemId { get; set; }
        }

        public async Task<List<AddComingSoonBatchItemResult>> AddComingSoonBatchAsync(
            List<AddComingSoonBatchRequest> requests,
            string targetPath,
            string customStubPath,
            string apiKey,
            Action<string, string> onItemStatus,
            CancellationToken token)
        {
            var results = new Dictionary<string, AddComingSoonBatchItemResult>();
            var folderPaths = new Dictionary<string, string>(); // EntryId -> folderPath, write succeeded

            this.logger.Info("[AddBatch] ===== START ===== {0} movie(s), target={1}", requests.Count, targetPath);

            // ---- Stage 1: write every folder/stub. One movie's write failing
            // is recorded as that movie's own failure only - it does not abort
            // the rest of the batch. ----
            foreach (var req in requests)
            {
                string safeName = BuildComingSoonFolderName(req.Movie.Title, req.Movie.ReleaseYear);
                string folderPath = Path.Combine(targetPath, safeName);

                try
                {
                    this.fileSystem.CreateDirectory(folderPath);
                    string stubFile = Path.Combine(folderPath, safeName + GetStubExtension(customStubPath));
                    await WriteStubAsync(stubFile, customStubPath).ConfigureAwait(false);
                    this.logger.Info("[AddBatch] Folder and stub created for '{0}': {1}", req.Movie.Title, folderPath);

                    ComingSoonEntryPoint.RegisterPendingPath(folderPath);
                    folderPaths[req.EntryId] = folderPath;

                    if (onItemStatus != null)
                        onItemStatus(req.EntryId, "Writing folder and stub file...");
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("[AddBatch] Failed to create placeholder for '{0}'", ex, req.Movie.Title);
                    results[req.EntryId] = FailBatchItem(
                        req.EntryId, AddComingSoonStage.WriteFiles,
                        "Failed to create folder/stub: " + ex.Message, folderPath);
                }
            }

            if (folderPaths.Count == 0)
            {
                this.logger.Warn("[AddBatch] No folders were written successfully - skipping library refresh entirely.");
                return requests.Select(r => results[r.EntryId]).ToList();
            }

            // ---- Stage 2: ONE refresh for the whole batch ----
            var targetLibrary = FindCollectionFolder(targetPath);
            if (targetLibrary == null)
            {
                this.logger.Warn("[AddBatch] Could not locate target CollectionFolder for {0}", targetPath);
                return FailRemainingBatch(requests, results, folderPaths,
                    AddComingSoonStage.RefreshTargetLibrary, "Could not locate the target library.");
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                this.logger.Warn("[AddBatch] No Emby API key configured - cannot call the refresh endpoint.");
                return FailRemainingBatch(requests, results, folderPaths,
                    AddComingSoonStage.RefreshTargetLibrary, "No Emby API key configured.");
            }

            string baseUrl = await this.appHost.GetLocalApiUrl(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(baseUrl))
            {
                this.logger.Warn("[AddBatch] GetLocalApiUrl returned empty.");
                return FailRemainingBatch(requests, results, folderPaths,
                    AddComingSoonStage.RefreshTargetLibrary, "Could not resolve the local Emby API URL.");
            }

            var httpClient = this.appHost.Resolve<IHttpClient>();

            try
            {
                // ValidationOnly, same reasoning as AddComingSoonPipelineAsync:
                // this refresh only needs to register the new paths so the tag
                // can be applied - it must not be exposed to a slow/offline
                // provider, and one call here covers every folder written above.
                await CallRefreshEndpointAsync(httpClient, baseUrl, targetLibrary.InternalId,
                    "Library (Coming Soon target, batch)", apiKey, MetadataRefreshMode.ValidationOnly,
                    "AddBatch/RefreshTargetLibrary", token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[AddBatch] ValidationOnly refresh failed", ex);
                return FailRemainingBatch(requests, results, folderPaths,
                    AddComingSoonStage.RefreshTargetLibrary, "Library refresh call failed: " + ex.Message);
            }

            // ---- Stage 3: confirm ingest+tag per movie, concurrently ----
            // IMPORTANT: each task below must NOT write into the shared `results`
            // dictionary directly - Dictionary<TKey,TValue> is not thread-safe for
            // concurrent writes, and running N of these via Task.WhenAll means N
            // threads could be inserting at once. That used to corrupt the
            // dictionary's internal buckets intermittently (more likely with
            // larger/faster batches), occasionally leaving a slot holding
            // default(TValue) - i.e. a null AddComingSoonBatchItemResult - which
            // then NullReferenceException'd on `r.Success` below at the
            // Count(r => r.Success) call. Each task instead RETURNS its result;
            // they're merged into `results` single-threaded after WhenAll.
            var pollResults = await Task.WhenAll(folderPaths.Select(async kvp =>
            {
                string entryId = kvp.Key;
                string folderPath = kvp.Value;

                BaseItem taggedMovie = null;
                bool confirmed = await WaitForConditionAsync(
                    () =>
                    {
                        taggedMovie = FindMovieInFolder(folderPath);
                        return taggedMovie != null;
                    },
                    token, "AddBatch/ConfirmIngestedAndTagged",
                    AddPipelineIngestPassSeconds,
                    msg => { if (onItemStatus != null) onItemStatus(entryId, msg); }).ConfigureAwait(false);

                if (!confirmed)
                {
                    return FailBatchItem(entryId, AddComingSoonStage.ConfirmIngestedAndTagged,
                        "Item was not ingested and tagged within the timeout.", folderPath);
                }

                LogItemState("AddBatch - confirmed ingested and tagged", "Movie", taggedMovie);
                return new AddComingSoonBatchItemResult
                {
                    EntryId = entryId,
                    Success = true,
                    FolderPath = folderPath,
                    FinalItemId = taggedMovie.Id,
                };
            })).ConfigureAwait(false);

            foreach (var r in pollResults)
                results[r.EntryId] = r;

            int successCount = results.Values.Count(r => r.Success);
            this.logger.Info("[AddBatch] ===== COMPLETE ===== {0}/{1} succeeded", successCount, requests.Count);

            // Best-effort, detached, whole-library follow-up - same reasoning as
            // AddComingSoonPipelineAsync's FireAndForgetFullRefresh. One call
            // covers every movie in the batch.
            if (successCount > 0)
                FireAndForgetFullRefresh(httpClient, baseUrl, targetLibrary.InternalId, apiKey,
                    string.Format("{0} movie(s) (batch)", successCount));

            return requests.Select(r => results[r.EntryId]).ToList();
        }

        private List<AddComingSoonBatchItemResult> FailRemainingBatch(
            List<AddComingSoonBatchRequest> requests,
            Dictionary<string, AddComingSoonBatchItemResult> results,
            Dictionary<string, string> folderPaths,
            AddComingSoonStage stage, string reason)
        {
            foreach (var req in requests)
            {
                if (results.ContainsKey(req.EntryId)) continue; // already failed at WriteFiles
                string folderPath;
                folderPaths.TryGetValue(req.EntryId, out folderPath);
                results[req.EntryId] = FailBatchItem(req.EntryId, stage, reason, folderPath);
            }
            return requests.Select(r => results[r.EntryId]).ToList();
        }

        private AddComingSoonBatchItemResult FailBatchItem(
            string entryId, AddComingSoonStage stage, string reason, string folderPath)
        {
            this.logger.Warn("[AddBatch] FAILED at stage {0} for entry {1}: {2}", stage, entryId, reason);
            return new AddComingSoonBatchItemResult
            {
                EntryId = entryId,
                Success = false,
                FailedAtStage = stage,
                FailureReason = reason,
                FolderPath = folderPath,
            };
        }

        // -----------------------------------------------------------------------
        // Make Live - unified pipeline
        // -----------------------------------------------------------------------
        // Replaces the old MakeLiveAsync / TestA_SafeMode / TestB_AdventurousMode
        // trio. Normal and Advanced modes now walk the exact same nine stages and
        // only diverge inside Stage 6 (LibraryUpdateAndRefresh) - that's the only
        // place either mode touches Path/parent on a live object. When no move is
        // configured (targetPath is null/empty), Stages 3, 4, 5 and 8 report
        // complete immediately: the progress bar still advances through all nine
        // checkpoints every run, it just doesn't wait on the ones that don't apply.
        //
        // LibraryMonitor suppression (ReportFileSystemChangeBeginning/Complete) -
        // used by AddComingSoonAsync above - has deliberately been left OUT of
        // this pipeline for now. Revisit once testing shows whether un-suppressed
        // move/delete events cause problems (e.g. a debounced auto-refresh racing
        // one of the explicit REST refreshes below).
        // -----------------------------------------------------------------------

        public enum MakeLiveMode
        {
            /// <summary>
            /// Tag removal only. Never touches Path or parent on the live Movie
            /// row - Emby is left to discover the moved file as a new item via a
            /// plain Default-mode refresh. This is the default / production mode:
            /// "normal" because it does not aggressively manipulate Emby's library
            /// state. Creating folders and asking for a rescan doesn't count as
            /// aggressive - mutating an existing live item's Path/parent does.
            /// </summary>
            Normal,

            /// <summary>
            /// Reparents the existing Movie row onto the bootstrapped destination
            /// Folder (Path + parent updated) before refreshing, attempting to
            /// preserve the item's identity (watch state, played position, etc.)
            /// across the move.
            /// </summary>
            Advanced
        }

        public enum MakeLiveStage
        {
            ReadinessCheck = 0,
            CaptureState = 1,
            CreateTargetFolder = 2,
            EstablishTargetIds = 3,
            MoveFiles = 4,
            LibraryUpdateAndRefresh = 5,
            ConfirmTargetState = 6,
            RefreshSourceOrphans = 7,
            SteadyStateCheck = 8,
            Complete = 9
        }

        // Fixed checkpoints so the bar advances the same way regardless of mode
        // or whether a move is configured. Stages 3/4/5/8 simply resolve fast
        // (a few ms) instead of waiting, when there's no move.
        private static readonly double[] MakeLiveStagePercent =
        {
            /* ReadinessCheck          */  5,
            /* CaptureState            */ 12,
            /* CreateTargetFolder      */ 20,
            /* EstablishTargetIds      */ 40, // includes the up-to-9s no-new-content ingest poll
            /* MoveFiles               */ 55,
            /* LibraryUpdateAndRefresh */ 65,
            /* ConfirmTargetState      */ 85, // includes the up-to-80s new-content ingest poll - the long pole
            /* RefreshSourceOrphans    */ 95, // includes the up-to-9s no-new-content ingest poll
            /* SteadyStateCheck        */ 98,
            /* Complete                */ 100
        };

        public class MakeLiveResult
        {
            public bool Success { get; set; }
            public MakeLiveStage? FailedAtStage { get; set; }
            public string FailureReason { get; set; }
            public Guid? FinalItemId { get; set; }

            /// <summary>
            /// True when the failure condition (e.g. insufficient disk space)
            /// affects every remaining item in the batch. MakeLiveTask uses this
            /// to revert unstarted queued items back to Pending rather than leaving
            /// them stuck in the queue, and to stop processing further items.
            /// </summary>
            public bool IsHardStop { get; set; }

            /// <summary>
            /// Only meaningful when Mode == Advanced and a move was requested.
            /// True = the original Movie row (same InternalId/Id) was confirmed,
            /// by a FRESH post-Stage-8 lookup, to still be the item at the new
            /// path - watch state/userdata genuinely carried over.
            /// False = Advanced mode ran, the move succeeded (Success=true), but
            /// the live item at the new path is a NEW row - identity was lost,
            /// most likely during Stage 8's source-library cleanup refresh
            /// silently removing the reparented row rather than recognizing it.
            /// Null = Normal mode, or no move was requested - identity
            /// preservation was never attempted, so the question doesn't apply.
            /// </summary>
            public bool? IdentityPreserved { get; set; }

            /// <summary>
            /// True = more than one item was found with Path == the expected final
            /// path - a duplicate row exists, regardless of whether the original
            /// row is among them. This is checked via a real list query (see
            /// FindAllByPath), NOT libraryManager.FindByPath, because FindByPath
            /// silently picks the newest-DateCreated match and hides the rest -
            /// it can report a single clean result even when this is true.
            /// Meaningful whenever a move was requested, in EITHER mode.
            /// </summary>
            public bool? DuplicateDetected { get; set; }
        }

        // Carries everything captured/derived as the pipeline progresses, so the
        // stage methods below can stay short instead of passing a dozen params.
        private class MoveContext
        {
            public string SourceFolderPath;
            public string TargetLibraryRootPath;  // as configured; null when no move
            public string TargetMovieFolderPath;  // root + movie folder name
            public string MovieNewVideoPath;

            public bool MoveRequested;
            public bool DeleteStub;
            public MakeLiveMode Mode;

            public BaseItem Movie;
            public BaseItem FinalItem;
            public long MovieOriginalInternalId;
            public Guid MovieOriginalId;
            public string MovieOriginalVideoPath;

            public Folder SourceFolder;
            public long? SourceFolderInternalId;

            public CollectionFolder SourceLibrary;
            public CollectionFolder TargetLibrary;
            public Folder DestinationFolder; // bootstrapped real Folder row, once known

            public string ApiKey;
            public string BaseUrl;
            public IHttpClient HttpClient;

            public string CustomStubPath;   // configured stub video, or null = use embedded resource
            public string LogContext;       // e.g. " 'Inception (2024)' [2/7]" - prefixed onto every log line below; empty when not set

            /// <summary>
            /// Optional callback set from MakeLivePipelineAsync at call time.
            /// Called by each stage with a short human-readable description of what
            /// the pipeline is doing — drives the secondary-text line of the
            /// in-flight tracker row in the Make Live UI.
            /// </summary>
            public Action<string> OnStageMessage;
        }

        public async Task<MakeLiveResult> MakeLivePipelineAsync(
            string folderPath,
            string targetPath,
            bool deleteStub,
            MakeLiveMode mode,
            string apiKey,
            IProgress<double> progress,
            CancellationToken token,
            string customStubPath = null,
            string logContext = null,
            Action<string> onStageMessage = null)
        {
            var ctx = new MoveContext
            {
                SourceFolderPath = folderPath,
                TargetLibraryRootPath = string.IsNullOrEmpty(targetPath) ? null : targetPath,
                MoveRequested = !string.IsNullOrEmpty(targetPath),
                DeleteStub = deleteStub,
                Mode = mode,
                ApiKey = apiKey,
                CustomStubPath = customStubPath,
                LogContext = string.IsNullOrEmpty(logContext) ? string.Empty : " " + logContext,
                OnStageMessage = onStageMessage,
            };

            this.logger.Info("[Pipeline/Start]{0} ===== START ===== folder={1} move={2}",
                ctx.LogContext, folderPath, ctx.MoveRequested);

            try
            {
                Report(progress, MakeLiveStage.ReadinessCheck);
                // Stage 1: the MigrationAnalyzer gate already ran in the page view
                // before this was ever called. Kept as an explicit stage/checkpoint
                // so heavier disk checks have an obvious home later without
                // renumbering everything downstream of it.

                ctx.OnStageMessage?.Invoke("Reading library state\u2026");
                if (!Stage_CaptureState(ctx))
                    return Fail(ctx, MakeLiveStage.CaptureState, "Could not resolve the Movie item for this folder.");
                ctx.LogContext = " '" + ctx.Movie.Name + "'" +
                    (string.IsNullOrEmpty(logContext) ? string.Empty : " " + logContext);
                Report(progress, MakeLiveStage.CaptureState);

                if (ctx.MoveRequested)
                {
                    ctx.OnStageMessage?.Invoke("Creating destination folder\u2026");
                    if (!await Stage_CreateTargetFolderAsync(ctx, token).ConfigureAwait(false))
                        return Fail(ctx, MakeLiveStage.CreateTargetFolder, "Could not create the destination folder.");
                }
                Report(progress, MakeLiveStage.CreateTargetFolder);

                if (ctx.MoveRequested)
                {
                    ctx.OnStageMessage?.Invoke("Registering destination folder\u2026");
                    if (!await Stage_EstablishTargetIdsAsync(ctx, token).ConfigureAwait(false))
                        return Fail(ctx, MakeLiveStage.EstablishTargetIds, "Destination folder was not ingested by Emby in time.");
                }
                Report(progress, MakeLiveStage.EstablishTargetIds);

                // Mode C (Isolation): suppress LibraryMonitor around the physical
                // move on both sides. Promoted from the former diagnostic Test C -
                // confirmed to be what stops the source-side identity loss, by
                // ruling out LibraryMonitor's own independent debounced refresh
                // racing the explicit REST refresh calls below.
                bool suppressMonitor = ctx.MoveRequested && ctx.Mode == MakeLiveMode.Advanced;
                if (ctx.MoveRequested)
                {
                    // Disk space check before any files move — hard stop if insufficient.
                    // Skipped for same-drive moves (rename, no space consumed).
                    if (!CheckDiskSpaceForMove(ctx))
                        return Fail(ctx, MakeLiveStage.MoveFiles,
                            "Insufficient disk space on destination drive.", isHardStop: true);

                    ctx.OnStageMessage?.Invoke("Moving files\u2026");

                    if (suppressMonitor)
                    {
                        this.logger.Info("[Pipeline/MoveFiles]{0} Suppressing LibraryMonitor for source and destination during move.", ctx.LogContext);
                        this.libraryMonitor.ReportFileSystemChangeBeginning(ctx.SourceFolderPath);
                        this.libraryMonitor.ReportFileSystemChangeBeginning(ctx.TargetMovieFolderPath);
                    }
                    bool moved;
                    try
                    {
                        moved = Stage_MoveFiles(ctx);
                    }
                    finally
                    {
                        if (suppressMonitor)
                        {
                            this.libraryMonitor.ReportFileSystemChangeComplete(ctx.SourceFolderPath, false);
                            this.libraryMonitor.ReportFileSystemChangeComplete(ctx.TargetMovieFolderPath, false);
                        }
                    }
                    if (!moved)
                        return Fail(ctx, MakeLiveStage.MoveFiles, "Failed to move files to the destination folder.");

                    // Stage_DeletePlaceholderStub is retained as a safety net but is a
                    // documented no-op: Stage 3 no longer writes _placeholder_.mp4
                    // (Probe 2 removed — empty folder is always sufficient).
                    Stage_DeletePlaceholderStub(ctx);
                }
                Report(progress, MakeLiveStage.MoveFiles);

                ctx.OnStageMessage?.Invoke("Removing Coming Soon tag\u2026");
                if (!await Stage_LibraryUpdateAndRefreshAsync(ctx, token).ConfigureAwait(false))
                    return Fail(ctx, MakeLiveStage.LibraryUpdateAndRefresh, "Library refresh call failed - check the configured API key.");
                Report(progress, MakeLiveStage.LibraryUpdateAndRefresh);

                // Stage message for ConfirmTargetState is driven by the WaitForConditionAsync
                // onPass callback inside Stage_ConfirmTargetStateAsync, so each check pass
                // updates the secondary line with the specific wait time.
                if (!await Stage_ConfirmTargetStateAsync(ctx, token).ConfigureAwait(false))
                    return Fail(ctx, MakeLiveStage.ConfirmTargetState, "Item did not reach the expected state after refresh.");
                Report(progress, MakeLiveStage.ConfirmTargetState);

                // Mode C (Isolation) skips Stage 8 entirely - the former diagnostic
                // Test C confirmed this REST refresh on the SOURCE library was the
                // other half of the identity-loss trigger. Mode A still runs it
                // (it never reparents, so there's no identity to lose, and it's
                // the only thing that clears the stale source-library row).
                if (ctx.MoveRequested && ctx.Mode == MakeLiveMode.Normal)
                {
                    ctx.OnStageMessage?.Invoke("Cleaning up source library\u2026");
                    if (!await Stage_RefreshSourceOrphansAsync(ctx, token).ConfigureAwait(false))
                    {
                        // Non-fatal: the movie itself is live and correct - only the old
                        // database row is left behind. Logged loudly, but a stray orphan
                        // row doesn't justify failing an otherwise-successful move.
                        this.logger.Warn("[Pipeline/RefreshSourceOrphans]{0} Source orphan (InternalId={1}) was not cleaned up " +
                            "within the timeout - a manual library scan will clear it.", ctx.LogContext, ctx.SourceFolderInternalId);
                    }
                }
                Report(progress, MakeLiveStage.RefreshSourceOrphans);

                ctx.OnStageMessage?.Invoke("Verifying final state\u2026");
                var steadyState = await Stage_CheckSteadyStateAsync(ctx, token).ConfigureAwait(false);
                Report(progress, MakeLiveStage.SteadyStateCheck);

                // Stage_CleanupStub is retained as a safety net but is a documented
                // no-op: Stage 3 no longer writes _placeholder_.mp4.
                if (ctx.DeleteStub)
                    Stage_CleanupStub(ctx);

                this.logger.Info("[Pipeline/Complete]{0} ===== COMPLETE =====", ctx.LogContext);
                Report(progress, MakeLiveStage.Complete);

                return new MakeLiveResult
                {
                    Success = true,
                    FinalItemId = steadyState.FinalId,
                    IdentityPreserved = ctx.MoveRequested && mode == MakeLiveMode.Advanced
                        ? (bool?)steadyState.IdentityPreserved
                        : null,
                    DuplicateDetected = ctx.MoveRequested ? (bool?)steadyState.DuplicateDetected : null
                };
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[Pipeline/Complete]{0} Unhandled exception", ex, ctx.LogContext);
                return Fail(ctx, MakeLiveStage.Complete, "Unhandled exception - see server log: " + ex.Message);
            }
        }

        // ---- Stage 9: Clean up the bootstrap/placeholder stub ----
        // DOCUMENTED NO-OP: Stage 3 no longer writes _placeholder_.mp4 (Probe 2
        // removed — empty folder is always sufficient). File.Exists returns false
        // and the method returns. Retained as a permanent safety net in case the
        // design ever changes.
        private void Stage_CleanupStub(MoveContext ctx)
        {
            string finalFolder = ctx.MoveRequested ? ctx.TargetMovieFolderPath : ctx.SourceFolderPath;
            if (string.IsNullOrEmpty(finalFolder)) return;

            string stubPath = Path.Combine(finalFolder, BootstrapStubFileName);
            try
            {
                if (File.Exists(stubPath))
                {
                    File.Delete(stubPath);
                    this.logger.Info("[Pipeline/CleanupStub]{0} Removed placeholder stub: {1}", ctx.LogContext, stubPath);
                }
            }
            catch (Exception ex)
            {
                this.logger.Warn("[Pipeline/CleanupStub]{0} Failed to remove placeholder stub '{1}': {2}",
                    ctx.LogContext, stubPath, ex.Message);
            }
        }

        // ---- Stage 2: Capture current state ----
        private bool Stage_CaptureState(MoveContext ctx)
        {
            var movie = FindMovieInFolder(ctx.SourceFolderPath);
            LogItemState("Pipeline/CaptureState - captured initial state", "Movie", movie);
            if (movie == null) return false;

            ctx.Movie = movie;
            ctx.MovieOriginalInternalId = movie.InternalId;
            ctx.MovieOriginalId = movie.Id;
            ctx.MovieOriginalVideoPath = movie.Path;

            ctx.SourceFolder = movie.GetParent() as Folder;
            ctx.SourceFolderInternalId = ctx.SourceFolder != null ? ctx.SourceFolder.InternalId : (long?)null;
            LogItemState("Pipeline/CaptureState - source Folder", "Movie Folder (source)", ctx.SourceFolder);

            ctx.SourceLibrary = FindCollectionFolder(ctx.SourceFolderPath);
            if (ctx.SourceLibrary == null)
                this.logger.Warn("[Pipeline/CaptureState]{0} Could not locate source CollectionFolder for {1} - " +
                    "the refresh in Stage 6/8 will not be able to run.", ctx.LogContext, ctx.SourceFolderPath);

            if (ctx.MoveRequested)
            {
                string folderName = Path.GetFileName(ctx.SourceFolderPath);
                ctx.TargetMovieFolderPath = Path.Combine(ctx.TargetLibraryRootPath, folderName);
                string movieFileName = Path.GetFileName(movie.Path);
                ctx.MovieNewVideoPath = Path.Combine(ctx.TargetMovieFolderPath, movieFileName);

                ctx.TargetLibrary = FindCollectionFolder(ctx.TargetLibraryRootPath);
                if (ctx.TargetLibrary == null)
                {
                    this.logger.Warn("[Pipeline/CaptureState]{0} Could not locate target CollectionFolder for {1}",
                        ctx.LogContext, ctx.TargetLibraryRootPath);
                    return false;
                }
            }

            return true;
        }

        // ---- Stage 3: Create target library folder (move only) ----
        // An empty folder is always sufficient for Emby to register a Folder item.
        // Probe 2 (writing a placeholder stub as a fallback) has been removed:
        // if an empty folder ever fails to register, something deeper is wrong
        // and returning false here surfaces that failure clearly rather than
        // masking it behind a stub workaround.
        //
        // Stage_DeletePlaceholderStub and Stage_CleanupStub are retained as safety
        // nets (they check File.Exists before doing anything) but are now no-ops
        // in normal operation.
        private async Task<bool> Stage_CreateTargetFolderAsync(MoveContext ctx, CancellationToken token)
        {
            if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false))
                return false;

            try
            {
                bool isNew = !Directory.Exists(ctx.TargetMovieFolderPath);
                if (isNew)
                {
                    Directory.CreateDirectory(ctx.TargetMovieFolderPath);
                    this.logger.Info("[Pipeline/CreateTargetFolder]{0} Created destination folder: {1}",
                        ctx.LogContext, ctx.TargetMovieFolderPath);
                }
                else
                {
                    var existing = Directory.GetFileSystemEntries(ctx.TargetMovieFolderPath);
                    if (existing.Length > 0)
                        this.logger.Warn("[Pipeline/CreateTargetFolder]{0} Destination folder already exists and is non-empty " +
                            "({1} entr{2}) — possible retry of a previous attempt. Leaving existing contents alone: {3}",
                            ctx.LogContext, existing.Length, existing.Length == 1 ? "y" : "ies", ctx.TargetMovieFolderPath);
                    else
                        this.logger.Info("[Pipeline/CreateTargetFolder]{0} Destination folder already exists and is empty: {1}",
                            ctx.LogContext, ctx.TargetMovieFolderPath);
                }

                await CallRefreshEndpointAsync(ctx.HttpClient, ctx.BaseUrl, ctx.TargetLibrary.InternalId,
                    "Library (target)", ctx.ApiKey, MetadataRefreshMode.ValidationOnly,
                    "Pipeline/CreateTargetFolder", token, ctx.LogContext).ConfigureAwait(false);

                bool folderRegistered = await WaitForConditionAsync(
                    () =>
                    {
                        ctx.DestinationFolder = this.libraryManager.FindByPath(ctx.TargetMovieFolderPath, true) as Folder;
                        return ctx.DestinationFolder != null;
                    },
                    token, "Pipeline/CreateTargetFolder/Poll",
                    NoNewContentFirstWaitSeconds, NoNewContentSecondWaitSeconds, null, ctx.LogContext)
                    .ConfigureAwait(false);

                if (folderRegistered)
                    LogItemState("Pipeline/CreateTargetFolder - empty folder registered by Emby",
                        "Movie Folder (target, real)", ctx.DestinationFolder);
                else
                    this.logger.Warn("[Pipeline/CreateTargetFolder]{0} Empty folder was not registered by Emby — " +
                        "unexpected. Check Emby server health.", ctx.LogContext);

                return folderRegistered;
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[Pipeline/CreateTargetFolder]{0} Failed: {1}",
                    ex, ctx.LogContext, ctx.TargetMovieFolderPath);
                return false;
            }
        }

        // ---- Stage 4: Establish target ids (move only) ----
        // ctx.DestinationFolder is now established in Stage 3 (CreateTargetFolder),
        // which owns both the directory creation and the Emby ingest probe.
        // Stage 4 is retained as an explicit progress checkpoint; it verifies
        // Stage 3 did its job and fails loudly if somehow it did not.
        private Task<bool> Stage_EstablishTargetIdsAsync(MoveContext ctx, CancellationToken token)
        {
            if (ctx.DestinationFolder != null)
            {
                LogItemState("Pipeline/EstablishTargetIds - destination Folder confirmed (established in Stage 3)",
                    "Movie Folder (target, real)", ctx.DestinationFolder);
                return Task.FromResult(true);
            }

            // Should never reach here: Stage 3 returning true guarantees DestinationFolder is set.
            this.logger.Warn("[Pipeline/EstablishTargetIds]{0} DestinationFolder is null — Stage 3 should have established it.",
                ctx.LogContext);
            return Task.FromResult(false);
        }

        // ---- Stage 5: Move files (move only) ----
        // Moves everything in the source folder — video file, subtitles, extras,
        // subfolders. No stub-skipping needed: Stage 3 no longer writes a bootstrap
        // stub so there is nothing left behind to skip.
        //
        // Source folder delete: non-recursive. Since all contents were moved above,
        // the folder should be empty. If it is not (unexpected item appeared after
        // the move loop) we log a warning and skip the delete rather than
        // force-deleting with Directory.Delete(path, true).
        //
        // Disk space is checked by the caller (MakeLivePipelineAsync) before this
        // method is called, so no further check is needed here.
        private bool Stage_MoveFiles(MoveContext ctx)
        {
            try
            {
                if (!Directory.Exists(ctx.TargetMovieFolderPath))
                    Directory.CreateDirectory(ctx.TargetMovieFolderPath);

                foreach (var entryPath in Directory.GetFileSystemEntries(ctx.SourceFolderPath))
                {
                    string entryName = Path.GetFileName(entryPath);
                    string destPath = Path.Combine(ctx.TargetMovieFolderPath, entryName);

                    if (Directory.Exists(entryPath))
                        Directory.Move(entryPath, destPath);
                    else
                        File.Move(entryPath, destPath);

                    this.logger.Info("[Pipeline/MoveFiles]{0} Moved {1} -> {2}", ctx.LogContext, entryPath, destPath);
                }

                if (Directory.Exists(ctx.SourceFolderPath))
                {
                    var remaining = Directory.GetFileSystemEntries(ctx.SourceFolderPath);
                    if (remaining.Length > 0)
                    {
                        // Should not happen: all contents were moved above. Do not
                        // force-delete — log a warning and leave the folder for
                        // Stage 8's orphan cleanup to remove the Emby row. The
                        // physical folder will need manual review.
                        this.logger.Warn(
                            "[Pipeline/MoveFiles]{0} Source folder still has {1} item(s) after move — " +
                            "skipping delete to avoid data loss. Manual review required: {2}",
                            ctx.LogContext, remaining.Length, ctx.SourceFolderPath);
                    }
                    else
                    {
                        // Non-recursive: will throw (rather than silently delete) if
                        // somehow the folder is non-empty — extra guard against data loss.
                        Directory.Delete(ctx.SourceFolderPath);
                        this.logger.Info("[Pipeline/MoveFiles]{0} Deleted (now empty) source folder: {1}",
                            ctx.LogContext, ctx.SourceFolderPath);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[Pipeline/MoveFiles]{0} Failed to move files from {1} to {2}",
                    ex, ctx.LogContext, ctx.SourceFolderPath, ctx.TargetMovieFolderPath);
                return false;
            }
        }

        // ---- Between Stage 5 and Stage 6: delete the placeholder stub ----
        // DOCUMENTED NO-OP: Stage 3 no longer writes _placeholder_.mp4 (Probe 2
        // removed — empty folder is always sufficient). File.Exists returns false
        // and the method logs that and returns. Retained as a permanent safety net
        // in case the design ever changes. Stage_CleanupStub at end-of-pipeline
        // is a second safety net.
        private void Stage_DeletePlaceholderStub(MoveContext ctx)
        {
            string stubPath = Path.Combine(ctx.TargetMovieFolderPath, BootstrapStubFileName);
            try
            {
                if (File.Exists(stubPath))
                {
                    File.Delete(stubPath);
                    this.logger.Info("[Pipeline/DeletePlaceholderStub]{0} Deleted placeholder stub: {1}",
                        ctx.LogContext, stubPath);
                }
                else
                {
                    this.logger.Info("[Pipeline/DeletePlaceholderStub]{0} No placeholder stub present (expected — " +
                        "Stage 3 no longer writes one): {1}", ctx.LogContext, stubPath);
                }
            }
            catch (Exception ex)
            {
                this.logger.Warn("[Pipeline/DeletePlaceholderStub]{0} Could not delete placeholder stub '{1}': {2}",
                    ctx.LogContext, stubPath, ex.Message);
            }
        }

        // ---- Stage 6: Library update + target refresh - the ONLY branch point ----
        private async Task<bool> Stage_LibraryUpdateAndRefreshAsync(MoveContext ctx, CancellationToken token)
        {
            string activeTag = ActiveTagText;
            var tags = (ctx.Movie.Tags ?? new string[0])
                .Where(t => !string.Equals(t, activeTag, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ctx.Movie.Tags = tags;

            if (!ctx.MoveRequested)
            {
                // No folder boundary crossed, so there's nothing for Advanced mode
                // to do differently here - tag removal + in-place update is the
                // whole story, identical for both modes.
                this.libraryManager.UpdateItem(ctx.Movie, ctx.Movie.GetParent(), ItemUpdateType.MetadataEdit, null);
                LogItemState("Pipeline/LibraryUpdateAndRefresh - tag removed (no move)", "Movie", ctx.Movie);

                if (ctx.SourceLibrary == null) return false;
                if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false)) return false;

                try
                {
                    await CallRefreshEndpointAsync(ctx.HttpClient, ctx.BaseUrl, ctx.SourceLibrary.InternalId,
                        "Library", ctx.ApiKey, MetadataRefreshMode.Default,
                        "Pipeline/LibraryUpdateAndRefresh/Trigger", token, ctx.LogContext).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("[Pipeline/LibraryUpdateAndRefresh]{0} Default refresh failed", ex, ctx.LogContext);
                    return false;
                }

                return true;
            }

            if (ctx.Mode == MakeLiveMode.Advanced)
            {
                // Reparent the existing live Movie row onto the real, bootstrapped
                // destination Folder, attempting to preserve its identity.
                //
                // IMPORTANT: Path/ParentId are written via IItemRepository.SaveItem
                // FIRST, not via UpdateItem alone. Evidence (Test B/C runs) showed
                // UpdateItem-only reparenting produces a DUPLICATE row: Emby's item
                // Guids are derived as MD5(TypeName + Path) (confirmed from Emby's
                // own LibraryManager source), so when the recursive folder-validate
                // scan later encounters the real file under the real parent, it
                // independently re-derives a Guid from the NEW path, doesn't find a
                // row already saved under THAT id, and creates a fresh one -
                // regardless of what UpdateItem did to the original row's Path
                // field. SaveItem persists the row keyed by its EXISTING
                // InternalId/Guid, with no re-derivation involved, which is the
                // actual fix being tested here. ParentId (a real, settable `long`
                // property on BaseItem - confirmed via decompile, NOT the same as
                // the `parent` argument UpdateItem takes) is set explicitly since
                // that's the real relational link, separate from Path.
                //
                // UpdateItem is still called afterward, with the same already-
                // correct item/parent, in case it does additional in-memory
                // cache/event work beyond persistence - Emby is closed-source past
                // this SDK version, so that can't be confirmed either way. If an
                // un-refreshed in-memory cache were a real problem, Stage 7's own
                // same-process poll would simply time out rather than silently
                // succeed, so this isn't something that needs verifying blind.
                ctx.Movie.Path = ctx.MovieNewVideoPath;
                ctx.Movie.ParentId = ctx.DestinationFolder.InternalId;

                this.itemRepository.SaveItem(ctx.Movie, token);
                LogItemState("Pipeline/LibraryUpdateAndRefresh - tag+path+ParentId SaveItem (persisted directly)", "Movie", ctx.Movie);

                this.libraryManager.UpdateItem(ctx.Movie, ctx.DestinationFolder, ItemUpdateType.MetadataEdit, null);
                LogItemState("Pipeline/LibraryUpdateAndRefresh - UpdateItem (cache/event refresh)", "Movie", ctx.Movie);

                this.logger.Info("[Pipeline/LibraryUpdateAndRefresh]{0} Reparented onto target Folder InternalId={1} ('{2}').",
                    ctx.LogContext, ctx.DestinationFolder.InternalId, ctx.DestinationFolder.Path);
            }
            else
            {
                // Normal mode: tag removal only. Path/parent are deliberately left
                // untouched - no aggressive manipulation of a live object. Emby is
                // simply asked to look at the (now populated) target library again
                // and discover the moved file as a new item.
                this.libraryManager.UpdateItem(ctx.Movie, ctx.Movie.GetParent(), ItemUpdateType.MetadataEdit, null);
                LogItemState("Pipeline/LibraryUpdateAndRefresh - tag removed, path/parent untouched", "Movie", ctx.Movie);
            }

            if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false))
                return false;

            try
            {
                await CallRefreshEndpointAsync(ctx.HttpClient, ctx.BaseUrl, ctx.TargetLibrary.InternalId,
                    "Library (target)", ctx.ApiKey, MetadataRefreshMode.Default,
                    "Pipeline/LibraryUpdateAndRefresh/Trigger", token, ctx.LogContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[Pipeline/LibraryUpdateAndRefresh]{0} Default refresh failed for target library", ex, ctx.LogContext);
                return false;
            }

            return true;
        }

        // ---- Stage 7: Confirm the library achieved the expected state ----
        // This is the long-pole stage: Emby must probe a video file it hasn't
        // seen before (up to 80s on the slow preset). ctx.OnStageMessage is
        // threaded into WaitForConditionAsync's onPass callback so the secondary
        // text of the in-flight row updates each pass:
        //   "Confirming movie is live — 1st check (20s)…"
        //   "Confirming movie is live — 2nd check (60s)…"
        private async Task<bool> Stage_ConfirmTargetStateAsync(MoveContext ctx, CancellationToken token)
        {
            string expectedPath = ctx.MoveRequested ? ctx.MovieNewVideoPath : ctx.MovieOriginalVideoPath;

            Action<string> onPass = null;
            if (ctx.OnStageMessage != null)
            {
                onPass = passLabel =>
                    ctx.OnStageMessage(string.Format(
                        "Confirming movie is live \u2014 {0}\u2026", passLabel));
            }

            // Always the slow preset here: by Stage 6 the real movie file is what's
            // being scanned - whether or not a move happened, and whether or not
            // identity was preserved, Emby still has to probe a file it hasn't
            // seen before (and, in Normal mode, run a full metadata/image
            // identification pass with no provider ids to reuse).
            bool confirmed = await WaitForConditionAsync(
                () =>
                {
                    ctx.FinalItem = this.libraryManager.FindByPath(expectedPath, false);
                    if (ctx.FinalItem == null) return false;

                    if (ctx.MoveRequested && ctx.Mode == MakeLiveMode.Advanced)
                    {
                        // Advanced mode's whole point is identity preservation -
                        // confirm it's the SAME row, not a freshly minted one.
                        return ctx.FinalItem.InternalId == ctx.MovieOriginalInternalId;
                    }

                    // Normal mode (and the no-move case): any resolvable item at
                    // the expected path with real metadata counts as success.
                    return true;
                },
                token, "Pipeline/ConfirmTargetState/Poll",
                NewContentFirstWaitSeconds, NewContentSecondWaitSeconds, onPass, ctx.LogContext).ConfigureAwait(false);

            LogItemState(
                confirmed
                    ? "Pipeline/ConfirmTargetState - confirmed target state"
                    : "Pipeline/ConfirmTargetState - target state NOT confirmed (last lookup)",
                "Movie", ctx.FinalItem);

            return confirmed;
        }

        // ---- Stage 8: Refresh source to clear orphans (Mode A / move only, non-fatal) ----
        private async Task<bool> Stage_RefreshSourceOrphansAsync(MoveContext ctx, CancellationToken token)
        {
            if (!ctx.SourceFolderInternalId.HasValue) return true; // nothing to clean up

            if (ctx.SourceLibrary == null)
            {
                this.logger.Warn("[Pipeline/RefreshSourceOrphans]{0} Could not locate source CollectionFolder for {1} - " +
                    "cannot trigger cleanup refresh.", ctx.LogContext, ctx.SourceFolderPath);
                return false;
            }

            if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false))
                return false;

            try
            {
                await CallRefreshEndpointAsync(ctx.HttpClient, ctx.BaseUrl, ctx.SourceLibrary.InternalId,
                    "Library (source)", ctx.ApiKey, MetadataRefreshMode.Default,
                    "Pipeline/RefreshSourceOrphans/Trigger", token, ctx.LogContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[Pipeline/RefreshSourceOrphans]{0} Source cleanup refresh failed", ex, ctx.LogContext);
                return false;
            }

            // Default-mode refresh, but fast preset: there's no new content here,
            // just an old row that should disappear once Emby notices the folder
            // is gone. (Default mode is used so Emby actually re-evaluates the
            // library, not because anything needs identifying.)
            return await WaitForConditionAsync(
                () => this.libraryManager.GetItemById(ctx.SourceFolderInternalId.Value) == null,
                token, "Pipeline/RefreshSourceOrphans/Poll",
                NoNewContentFirstWaitSeconds, NoNewContentSecondWaitSeconds, null, ctx.LogContext).ConfigureAwait(false);
        }

        private class SteadyStateResult
        {
            public Guid FinalId;
            public bool IdentityPreserved;
            public bool DuplicateDetected;
        }

        // ---- Stage 9: Check steady state ----
        // Uses FindAllByPath (a real list query), NOT libraryManager.FindByPath -
        // see FindAllByPath's doc comment. A single-result lookup (FindByPath, or
        // GetItemById on its own) can report "found, looks fine" even while a
        // second row exists at the same path; this is the only check in the
        // pipeline that can actually SEE a duplicate, by counting how many rows
        // really exist there. Also deliberately re-queries fresh rather than
        // reusing ctx.FinalItem from Stage 7 - Stage 8 (or, in Test C, nothing at
        // all) may have changed the picture since then.
        private async Task<SteadyStateResult> Stage_CheckSteadyStateAsync(MoveContext ctx, CancellationToken token)
        {
            string expectedPath = ctx.MoveRequested ? ctx.MovieNewVideoPath : ctx.MovieOriginalVideoPath;

            var allAtPath = FindAllByPath(expectedPath, false);

            this.logger.Info("[Pipeline/SteadyStateCheck]{0} {1} item(s) found at expected path '{2}':",
                ctx.LogContext, allAtPath.Length, expectedPath);
            foreach (var candidate in allAtPath)
                LogItemState("Pipeline/SteadyStateCheck - FindAllByPath candidate", "Movie (by path)", candidate);
            if (allAtPath.Length == 0)
                this.logger.Info("[Pipeline/SteadyStateCheck]{0} (none)", ctx.LogContext);

            bool duplicateDetected = allAtPath.Length > 1;
            bool originalAmongResults = allAtPath.Any(i => i.InternalId == ctx.MovieOriginalInternalId);
            bool identityPreserved = allAtPath.Length == 1 && originalAmongResults;

            bool orphanCleared = !ctx.SourceFolderInternalId.HasValue ||
                this.libraryManager.GetItemById(ctx.SourceFolderInternalId.Value) == null;

            // Ground truth for "what's actually live right now": if there's
            // exactly one candidate that's clearly it. With a duplicate, there's
            // no single correct answer - report the original if it's one of the
            // duplicates, otherwise just the first found, since this field is only
            // used for the tracker/UI to point at SOMETHING, not as a judgment.
            Guid finalId = allAtPath.Length > 0
                ? (allAtPath.FirstOrDefault(i => i.InternalId == ctx.MovieOriginalInternalId) ?? allAtPath[0]).Id
                : ctx.MovieOriginalId;

            if (ctx.MoveRequested && duplicateDetected)
            {
                this.logger.Warn("[Pipeline/SteadyStateCheck]{0} DUPLICATE DETECTED - {1} separate items all have " +
                    "Path='{2}' (InternalIds: {3}). The original row (InternalId={4}) is {5} among them. This is " +
                    "almost certainly caused by Stage 6's reparent (UpdateItem with a new Path/parent) not fully " +
                    "registering the original row as a recognized child under the new parent at the relational " +
                    "level that the recursive folder-validate scan checks - so the scan creates a fresh row for " +
                    "the same file. The move itself succeeded, but watch state/userdata on the original row " +
                    "may not have carried over.",
                    ctx.LogContext, allAtPath.Length, expectedPath,
                    string.Join(", ", allAtPath.Select(i => i.InternalId.ToString())),
                    ctx.MovieOriginalInternalId,
                    originalAmongResults ? "present" : "NOT present");
            }
            else if (ctx.MoveRequested && !identityPreserved && allAtPath.Length == 1)
            {
                this.logger.Warn("[Pipeline/SteadyStateCheck]{0} Identity NOT preserved: item at '{1}' has " +
                    "InternalId={2}, expected {3}. The move succeeded, but watch state/userdata on the original row was NOT carried over.",
                    ctx.LogContext, expectedPath, allAtPath.Length > 0 ? allAtPath[0].InternalId.ToString() : "none",
                    ctx.MovieOriginalInternalId);
            }

            this.logger.Info("[Pipeline/SteadyStateCheck]{0} identity preserved={1}, " +
                "duplicate detected={2}, source orphan cleared={3}.",
                ctx.LogContext, identityPreserved, duplicateDetected, orphanCleared);

            return new SteadyStateResult
            {
                FinalId = finalId,
                IdentityPreserved = identityPreserved,
                DuplicateDetected = duplicateDetected
            };
        }

        // ---- Disk space check (cross-drive moves only) ----
        // Called at the start of the MoveFiles phase, before any files are touched.
        // Same-drive moves are renames (no space consumed) and are skipped.
        // Returns false and logs a warning if space is insufficient; the caller
        // (MakeLivePipelineAsync) treats false as a hard stop affecting all
        // remaining queued items.
        private bool CheckDiskSpaceForMove(MoveContext ctx)
        {
            string srcRoot = Path.GetPathRoot(ctx.SourceFolderPath);
            string dstRoot = Path.GetPathRoot(ctx.TargetMovieFolderPath);

            if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                return true; // same drive — move is a rename, no space needed

            long required;
            try
            {
                required = Directory
                    .GetFiles(ctx.SourceFolderPath, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
            }
            catch (Exception ex)
            {
                this.logger.Warn("[Pipeline/DiskSpaceCheck]{0} Could not measure source folder size: {1}",
                    ctx.LogContext, ex.Message);
                return false;
            }

            long available;
            try
            {
                available = new System.IO.DriveInfo(dstRoot).AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                this.logger.Warn("[Pipeline/DiskSpaceCheck]{0} Could not read free space on '{1}': {2}",
                    ctx.LogContext, dstRoot, ex.Message);
                return false;
            }

            if (available < required)
            {
                this.logger.Warn(
                    "[Pipeline/DiskSpaceCheck]{0} Insufficient space: need {1:N0} bytes, " +
                    "{2:N0} available on '{3}'.",
                    ctx.LogContext, required, available, dstRoot);
                return false;
            }

            this.logger.Info(
                "[Pipeline/DiskSpaceCheck]{0} Space OK: need {1:N0} bytes, {2:N0} available on '{3}'.",
                ctx.LogContext, required, available, dstRoot);
            return true;
        }

        // ---- Shared helpers ----

        // Defensive, two-step ingest check rather than a single flat wait. Pass
        // NoNewContent* for a DB/index-only check (empty folder, orphan/absence
        // confirmation); pass NewContent* whenever Emby has to actually probe a
        // real, previously-unseen video file. Check early, give one more, more
        // generous chance, then fail fast rather than hang indefinitely.
        // 2-pass overload — unchanged behavior, used by Make Live and the
        // no-new-content checks elsewhere in this file. Delegates to the
        // N-pass version below.
        private Task<bool> WaitForConditionAsync(
            Func<bool> probe, CancellationToken token, string stageLabel,
            int firstWaitSeconds, int secondWaitSeconds, Action<string> onPass = null, string logContext = null)
        {
            return WaitForConditionAsync(
                probe, token, stageLabel,
                new[] { firstWaitSeconds, secondWaitSeconds }, onPass, logContext);
        }

        // N-pass version. Waits passSeconds[0], probes; if not met, waits
        // passSeconds[1], probes; and so on. onPass (if supplied) fires
        // before each wait with a human-readable "Nth Pass (Xs)..." message,
        // intended to be surfaced directly in UI progress/detail text.
        private async Task<bool> WaitForConditionAsync(
            Func<bool> probe, CancellationToken token, string stageLabel,
            int[] passSeconds, Action<string> onPass = null, string logContext = null)
        {
            // timings (e.g. "2s/7s") is printed on every single line below, not
            // just the terminal ones - with several stages sharing similarly-named
            // labels, this is what makes it unambiguous at a glance which polling
            // preset (fast "no new content" vs slow "new content") produced any
            // given line, especially once several movies are interleaved in a
            // batch run (logContext then also carries the movie name + [i/N]).
            string ctxTag = string.IsNullOrEmpty(logContext) ? string.Empty : logContext;
            string timings = string.Join("/", passSeconds.Select(s => s + "s"));
            int elapsed = 0;

            for (int i = 0; i < passSeconds.Length; i++)
            {
                int waitSeconds = passSeconds[i];
                string ordinal = OrdinalPass(i + 1);

                if (onPass != null)
                    onPass(string.Format("Checking for Movie Ingestion — {0} Pass ({1}s)...", ordinal, waitSeconds));

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), token).ConfigureAwait(false);
                elapsed += waitSeconds;

                if (probe())
                {
                    this.logger.Info("[{0}]{1} Condition met at ~{2}s (timings={3}, {4} pass).",
                        stageLabel, ctxTag, elapsed, timings, ordinal);
                    return true;
                }

                bool isLastPass = i == passSeconds.Length - 1;
                if (!isLastPass)
                {
                    this.logger.Info("[{0}]{1} Not met after {2} pass (timings={3}, {4}s elapsed) - starting {5} pass (+{6}s).",
                        stageLabel, ctxTag, ordinal, timings, elapsed, OrdinalPass(i + 2), passSeconds[i + 1]);
                }
            }

            this.logger.Warn("[{0}]{1} Condition NOT met after ~{2}s (timings={3}) - treating as failed.",
                stageLabel, ctxTag, elapsed, timings);
            return false;
        }

        private static string OrdinalPass(int n)
        {
            switch (n)
            {
                case 1: return "1st";
                case 2: return "2nd";
                case 3: return "3rd";
                default: return n + "th";
            }
        }

        private async Task<bool> EnsureHttpAsync(MoveContext ctx, CancellationToken token)
        {
            if (ctx.HttpClient != null && !string.IsNullOrEmpty(ctx.BaseUrl)) return true;

            if (string.IsNullOrEmpty(ctx.ApiKey))
            {
                this.logger.Warn("[Pipeline]{0} No Emby API key configured - cannot call the refresh endpoint.", ctx.LogContext);
                return false;
            }

            ctx.BaseUrl = await this.appHost.GetLocalApiUrl(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(ctx.BaseUrl))
            {
                this.logger.Warn("[Pipeline]{0} GetLocalApiUrl returned empty.", ctx.LogContext);
                return false;
            }

            ctx.HttpClient = this.appHost.Resolve<IHttpClient>();
            return true;
        }

        private static void Report(IProgress<double> progress, MakeLiveStage stage)
        {
            if (progress != null)
                progress.Report(MakeLiveStagePercent[(int)stage]);
        }

        private MakeLiveResult Fail(MoveContext ctx, MakeLiveStage stage, string reason,
            bool isHardStop = false)
        {
            this.logger.Warn("[Pipeline/{0}]{1} FAILED{2}: {3}",
                stage, ctx.LogContext,
                isHardStop ? " [HARD STOP]" : string.Empty,
                reason);
            return new MakeLiveResult
            {
                Success = false,
                FailedAtStage = stage,
                FailureReason = reason,
                IsHardStop = isHardStop,
            };
        }

        // -----------------------------------------------------------------------
        // The three helpers below are unchanged from the original file - kept
        // verbatim rather than re-derived, since this is exactly the kind of
        // detail that's easy to get subtly wrong from memory.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Logs full item state using the SHORT-FORM InternalId (e.g. 369781), not
        /// the long Guid Id — matching what appears in Emby's own native log lines
        /// (e.g. "ProviderManager: RefreshItem Start: 369781 CollectionFolder ...")
        /// so our log can be visually cross-referenced against Emby's own log.
        /// roleLabel should be one of: "Movie", "Movie Folder", "Movie Folder Parent",
        /// "Library" — making it unambiguous which tier of the hierarchy this is.
        /// </summary>
        private void LogItemState(string label, string roleLabel, BaseItem item)
        {
            if (item == null)
            {
                this.logger.Info("[TEST] {0}: ({1}) item is null", label, roleLabel);
                return;
            }

            this.logger.Info(
                "[TEST] {0}: ({1}) InternalId={2} Type={3} Name='{4}' Path='{5}' Tags=[{6}]",
                label,
                roleLabel,
                item.InternalId,
                item.GetType().Name,
                item.Name,
                item.Path ?? "(null)",
                item.Tags != null ? string.Join(",", item.Tags) : "(null)");
        }

        /// <summary>
        /// Locates the Movie item inside a Coming Soon folder by querying for the
        /// actual video file path, NOT the folder path. FindByPath(folderPath, ...)
        /// resolves to the containing Folder item (Type=Folder, Tags=[] always —
        /// folders don't carry tags), which is the wrong item entirely. This was
        /// the root cause discovered while testing: every test that mutated the
        /// Folder item left the real tagged Movie item completely untouched,
        /// producing the orphaned-original / new-item-at-destination symptom.
        /// </summary>
        private BaseItem FindMovieInFolder(string folderPath)
        {
            var query = new InternalItemsQuery
            {
                Tags = new[] { ActiveTagText },
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
            };
            var candidates = this.libraryManager.GetItemList(query);

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate.Path)) continue;
                string candidateFolder = Path.GetDirectoryName(candidate.Path);
                if (string.Equals(candidateFolder, folderPath, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Returns EVERY item matching this exact Path, not just one. Deliberately
        /// NOT using libraryManager.FindByPath here - per Emby's own source
        /// (LibraryManager.cs), FindByPath internally queries with the same Path
        /// filter, then sorts by DateCreated descending and takes Limit=1 - its own
        /// doc comment literally says "If this returns multiple items it could be
        /// tricky figuring out which one is correct... the others obsolete but not
        /// yet cleaned up." That means FindByPath can silently return a clean
        /// single result even while a duplicate row sits right next to it - exactly
        /// the failure mode being investigated here. This does the same query
        /// without Limit/OrderBy, so duplication is actually visible.
        /// </summary>
        private BaseItem[] FindAllByPath(string path, bool? isFolder)
        {
            var query = new InternalItemsQuery
            {
                Path = path,
                IsFolder = isFolder,
            };
            return this.libraryManager.GetItemList(query).ToArray();
        }

        // -----------------------------------------------------------------------
        // Best-effort follow-up refresh, fired AFTER AddPipeline has already
        // declared success off a ValidationOnly result. Purely a nice-to-have
        // (real metadata/images instead of just the bare validated item) - runs
        // fully detached on its own CancellationToken.None so it survives the
        // calling request, and any exception (timeout, provider offline, etc.)
        // is logged and swallowed here, never surfaced to the caller. This must
        // never be awaited by, or allowed to affect, AddComingSoonPipelineAsync's
        // result or AddMovieTracker state - that's the whole point of it living
        // down here as a fire-and-forget Task.Run rather than inline.
        // -----------------------------------------------------------------------
        private void FireAndForgetFullRefresh(
            IHttpClient httpClient, string baseUrl, long libraryInternalId, string apiKey, string movieTitle)
        {
            var _ = Task.Run(async () =>
            {
                try
                {
                    await CallRefreshEndpointAsync(httpClient, baseUrl, libraryInternalId,
                        "Library (Coming Soon target, post-success full refresh)", apiKey, MetadataRefreshMode.FullRefresh,
                        "AddPipeline/PostSuccessFullRefresh", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.Warn(
                        "[AddPipeline/PostSuccessFullRefresh] Best-effort full refresh failed for '{0}' " +
                        "(harmless - the add already succeeded; this only affects when/whether full " +
                        "metadata and images show up): {1}", movieTitle, ex.Message);
                }
            });
        }

        // -----------------------------------------------------------------------
        // Shared REST-refresh helper. Confirmed working: the in-process
        // BaseItem.RefreshMetadata(...) call (tried via many variants) NEVER
        // produced the native "ProviderManager: RefreshItem Start" log line that
        // Emby's own UI Refresh button produces. The same operation via
        // POST /Items/{id}/Refresh DOES produce that line immediately. This
        // helper performs that REST call and logs the POST method, the
        // short-form InternalId being targeted, and a clear role label so the
        // log is unambiguous about what was refreshed.
        // -----------------------------------------------------------------------
        private async Task CallRefreshEndpointAsync(
            IHttpClient httpClient, string baseUrl, long internalId, string roleLabel,
            string apiKey, MetadataRefreshMode mode, string stageLabel, CancellationToken token,
            string logContext = null)
        {
            string ctxTag = string.IsNullOrEmpty(logContext) ? string.Empty : logContext;
            string url = string.Format(
                "{0}/Items/{1}/Refresh?Recursive=true&MetadataRefreshMode={2}&ImageRefreshMode={2}&ReplaceAllMetadata=false&ReplaceAllImages=false&api_key={3}",
                baseUrl.TrimEnd('/'),
                internalId,
                mode,
                Uri.EscapeDataString(apiKey));

            this.logger.Info(
                "[{0}]{1} POST /Items/{2}/Refresh — target=({3}) InternalId={2} Mode={4}",
                stageLabel, ctxTag, internalId, roleLabel, mode);

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = token,
                TimeoutMs = 15000,
                LogErrors = true,
            };

            using (var response = await httpClient.Post(options).ConfigureAwait(false))
            {
                this.logger.Info(
                    "[{0}]{1} POST /Items/{2}/Refresh ({3}) returned (2xx implied)",
                    stageLabel, ctxTag, internalId, roleLabel);
            }
        }

        // -----------------------------------------------------------------------
        // Query Coming Soon items
        // -----------------------------------------------------------------------

        public BaseItem[] GetComingSoonItems()
        {
            var query = new InternalItemsQuery
            {
                Tags = new[] { ActiveTagText },
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
            };
            return this.libraryManager.GetItemList(query);
        }

        // -----------------------------------------------------------------------
        // Library lookup helpers
        // -----------------------------------------------------------------------

        private CollectionFolder FindCollectionFolder(string libraryPath)
        {
            CollectionFolder collectionFolder = null;

            var virtualFolders = this.libraryManager.GetVirtualFolders();

            foreach (var vf in virtualFolders)
            {
                if (vf.Locations == null) continue;
                foreach (var loc in vf.Locations)
                {
                    char[] separators = new char[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
                    string normLoc = loc.TrimEnd(separators);
                    string normLibPath = libraryPath.TrimEnd(separators);

                    if (string.Equals(normLoc, normLibPath, StringComparison.OrdinalIgnoreCase) ||
                        normLibPath.StartsWith(normLoc, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(vf.ItemId))
                        {
                            Guid itemGuid;
                            if (Guid.TryParse(vf.ItemId, out itemGuid))
                                collectionFolder = this.libraryManager.GetItemById(itemGuid) as CollectionFolder;

                            if (collectionFolder == null)
                            {
                                long longId;
                                if (long.TryParse(vf.ItemId, out longId))
                                    collectionFolder = this.libraryManager.GetItemById(longId) as CollectionFolder;
                            }
                        }
                        break;
                    }
                }
                if (collectionFolder != null) break;
            }

            return collectionFolder;
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Returns the file extension to use for the stub file on disk.
        /// Derives from the custom stub path when one is provided, otherwise
        /// falls back to the embedded resource's extension (.mp4).
        /// </summary>
        private static string GetStubExtension(string customStubPath)
        {
            if (!string.IsNullOrEmpty(customStubPath))
                return Path.GetExtension(customStubPath); // e.g. ".mkv", ".avi"
            return Path.GetExtension(StubResourceName);   // ".mp4" from the embedded resource constant
        }

        private async Task WriteStubAsync(string destinationPath, string customStubPath)
        {
            //need the dummy file to be differnt than the real file so that emby will see it as a new file which can get removed later when we refresh.

            // Use custom file if provided and valid

            if (!string.IsNullOrEmpty(customStubPath))
            {
                if (!File.Exists(customStubPath))
                    throw new InvalidOperationException(
                        string.Format(
                            "Custom placeholder video not found: {0}. " +
                            "Check the path on the Configuration tab or disable the custom video option.",
                            customStubPath));

                this.logger.Info("Copying custom stub from {0}", customStubPath);
                using (var src = File.OpenRead(customStubPath))
                using (var dest = File.OpenWrite(destinationPath))
                    await src.CopyToAsync(dest).ConfigureAwait(false);
                return;
            }

            // Fall back to embedded resource
            var asm = Assembly.GetExecutingAssembly();
            using (var src = asm.GetManifestResourceStream(StubResourceName))
            {
                if (src != null)
                {
                    using (var dest = File.OpenWrite(destinationPath))
                        await src.CopyToAsync(dest).ConfigureAwait(false);
                }
                else
                {
                    File.WriteAllBytes(destinationPath, new byte[0]);
                    this.logger.Warn("Embedded stub MP4 not found; using empty file.");
                }
            }
        }

        /// <summary>
        /// Builds the sanitized "Title (Year)" folder name used everywhere a
        /// Coming Soon folder/stub is created or looked up. This is the single
        /// choke point that guarantees a missing/unknown year (0 or negative)
        /// can never reach disk as a literal "(0)" — it falls back to the
        /// current calendar year instead. Upstream code should still avoid
        /// producing a 0 year in the first place (see AddMovieTracker.
        /// SetManualConfident and TmdbService.IsConfidentMatch), but this is
        /// the last line of defense.
        /// </summary>
        internal static string BuildComingSoonFolderName(string title, int releaseYear)
        {
            int safeYear = releaseYear > 0 ? releaseYear : DateTime.UtcNow.Year;
            return SanitizeName(string.Format("{0} ({1})", title, safeYear));
        }

        internal static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new System.Text.StringBuilder(name.Length);
            bool lastWasSpace = false;

            foreach (var c in name)
            {
                if (invalid.Contains(c))
                {
                    // Drop the character entirely rather than substituting an
                    // underscore - "Dune: Part Two" should become
                    // "Dune Part Two", not "Dune_ Part Two". Collapse the gap
                    // it leaves behind into a single space (handled below).
                    if (sb.Length > 0 && !lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                    continue;
                }

                if (c == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }


    }
}