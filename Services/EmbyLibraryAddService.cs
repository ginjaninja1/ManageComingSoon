// ManageComingSoon - Emby Library Add Service
// Handles the "Add Coming Soon" pipeline only.
// Derives from EmbyLibrarySharedService for all shared helpers.
//
// Public API consumed by AddMoviePageView / AddMovieTask:
//   AddComingSoonAsync()          — legacy fire-and-forget path (kept intact)
//   AddComingSoonPipelineAsync()  — single-movie pipeline; called by AddMovieTask

namespace ManageComingSoon.Services
{
    using System;
    using System.IO;
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

    public class EmbyLibraryAddService : EmbyLibrarySharedService
    {
        // -----------------------------------------------------------------------
        // Stub resource
        // -----------------------------------------------------------------------
        private const string StubResourceName = "ManageComingSoon.comingsoon.mp4";

        // -----------------------------------------------------------------------
        // Add Movie pipeline ingest-poll presets
        // Three passes so a fast-ingesting server can confirm quickly without
        // giving up the extra headroom a slower server might need on the final pass.
        // Pass 3 bumped 30 s → 60 s after a real-world batch add showed one item
        // (out of five identical stubs) get picked up via a full ffprobe run
        // rather than the cheap new-stub path — that can run well past 30 s.
        // -----------------------------------------------------------------------
        private const int AddPipelineIngestPass1Seconds = 2;
        private const int AddPipelineIngestPass2Seconds = 7;
        private const int AddPipelineIngestPass3Seconds = 60;
        private static readonly int[] AddPipelineIngestPassSeconds =
        {
            AddPipelineIngestPass1Seconds,
            AddPipelineIngestPass2Seconds,
            AddPipelineIngestPass3Seconds,
        };

        // -----------------------------------------------------------------------
        // Constructor — all dependencies forwarded to base
        // -----------------------------------------------------------------------
        public EmbyLibraryAddService(
            IServerApplicationHost appHost,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILibraryMonitor libraryMonitor,
            ILogger logger)
            : base(appHost, libraryManager, itemRepository, providerManager,
                   fileSystem, libraryMonitor, logger)
        {
        }

        // -----------------------------------------------------------------------
        // Public wrappers for AddMovieTask post-queue refresh
        // These expose the shared base helpers at the minimum required visibility.
        // -----------------------------------------------------------------------

        public new Task<(IHttpClient Client, string BaseUrl)?> ResolveHttpAsync(
            string apiKey, string logPrefix, CancellationToken token)
            => base.ResolveHttpAsync(apiKey, logPrefix, token);

        public CollectionFolder FindCollectionFolderPublic(string libraryPath)
            => base.FindCollectionFolder(libraryPath);

        public Task CallRefreshEndpointAsync(
            IHttpClient httpClient, string baseUrl, long internalId, string roleLabel,
            string apiKey, MetadataRefreshMode mode, string stageLabel,
            CancellationToken token)
            => base.CallRefreshEndpointAsync(
                httpClient, baseUrl, internalId, roleLabel,
                apiKey, mode, stageLabel, token);

        // -----------------------------------------------------------------------
        // Stage result types
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
            /* ReadinessCheck            */  5,
            /* WriteFiles                */ 30,
            /* RefreshTargetLibrary      */ 40,
            /* ConfirmIngestedAndTagged  */ 95,   // up to ~69 s: 2 s / 7 s / 60 s passes
            /* Complete                  */ 100
        };

        // -----------------------------------------------------------------------
        // Legacy fire-and-forget add path (kept intact; not called by AddMovieTask)
        // -----------------------------------------------------------------------
        // Steps 1-3: Create placeholder, register pending path, scan.
        // Step 4 (tagging) is handled by ComingSoonEntryPoint.OnItemAdded
        // which fires AFTER Emby has fully committed the item's metadata.
        // -----------------------------------------------------------------------

        /*
        public async Task<string> AddComingSoonAsync(
            TmdbMovieResult movie,
            string libraryPath,
            string customStubPath,
            CancellationToken token)
        {
            string safeName = BuildComingSoonFolderName(movie.Title, movie.ReleaseYear);
            string folderPath = Path.Combine(libraryPath, safeName);

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

            this.logger.Info("[Step 2] Registering pending path for tagging: {0}", folderPath);
            ComingSoonEntryPoint.RegisterPendingPath(folderPath);

            this.logger.Info("[Step 3] Releasing LibraryMonitor suppression with immediate refresh for {0}", folderPath);
            this.libraryMonitor.ReportFileSystemChangeComplete(folderPath, true);
            this.logger.Info("[Step 3] ReportFileSystemChangeComplete called – FileRefresher should start shortly");

            this.logger.Info("Placeholder created. Awaiting FileRefresher ingest and ItemAdded.");
            return folderPath;
        }
        */
        // -----------------------------------------------------------------------
        // Single-movie pipeline — called by AddMovieTask for every queued entry.
        //
        // Design notes vs. AddComingSoonAsync:
        //   1. Uses an explicit REST refresh trigger (CallRefreshEndpointAsync)
        //      rather than relying entirely on LibraryMonitor's debounced FileRefresher.
        //   2. Actively polls for evidence the tag was applied (WaitForConditionAsync)
        //      with a real timeout, so the task can report success or failure
        //      instead of waiting indefinitely.
        //
        // NOTE: the dependency on ComingSoonEntryPoint.OnItemAdded for the tag
        // WRITE is unchanged — RegisterPendingPath is still the only mechanism
        // that makes Emby apply the Coming Soon tag. What this pipeline adds is
        // active polling for proof that the tag arrived, with a timeout.
        // -----------------------------------------------------------------------
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
                // Stage 0 — ReadinessCheck (destination-conflict gate ran in page
                // view before this was called; this is the progress checkpoint).
                ReportAdd(progress, AddComingSoonStage.ReadinessCheck);

                // Stage 1 — WriteFiles
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
                    return FailAdd(AddComingSoonStage.WriteFiles,
                        "Failed to create folder/stub: " + ex.Message, folderPath);
                }

                // Still required: the only mechanism that makes
                // ComingSoonEntryPoint.OnItemAdded apply the Coming Soon tag.
                ComingSoonEntryPoint.RegisterPendingPath(folderPath);
                this.logger.Info("[AddPipeline] Registered pending path for tagging: {0}", folderPath);

                ReportAdd(progress, AddComingSoonStage.WriteFiles);

                // Stage 2 — RefreshTargetLibrary
                var targetLibrary = FindCollectionFolder(targetPath);
                if (targetLibrary == null)
                {
                    this.logger.Warn("[AddPipeline] Could not locate target CollectionFolder for {0}", targetPath);
                    return FailAdd(AddComingSoonStage.RefreshTargetLibrary,
                        "Could not locate the target library.", folderPath);
                }

                var http = await ResolveHttpAsync(apiKey, "AddPipeline/RefreshTargetLibrary", token)
                    .ConfigureAwait(false);
                if (http == null)
                    return FailAdd(AddComingSoonStage.RefreshTargetLibrary,
                        string.IsNullOrEmpty(apiKey)
                            ? "No Emby API key configured."
                            : "Could not resolve the local Emby API URL.",
                        folderPath);

                try
                {
                    // ValidationOnly — only needs to register the new path and allow
                    // ComingSoonEntryPoint.OnItemAdded to apply the tag. Must not be
                    // exposed to a slow/offline metadata provider; ValidationOnly never
                    // touches a provider so a provider outage cannot block an add.
                    await CallRefreshEndpointAsync(
                        http.Value.Client, http.Value.BaseUrl,
                        targetLibrary.InternalId, "Library (Coming Soon target)",
                        apiKey, MetadataRefreshMode.ValidationOnly,
                        "AddPipeline/RefreshTargetLibrary", token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("[AddPipeline] ValidationOnly refresh failed", ex);
                    return FailAdd(AddComingSoonStage.RefreshTargetLibrary,
                        "Library refresh call failed: " + ex.Message, folderPath);
                }
                ReportAdd(progress, AddComingSoonStage.RefreshTargetLibrary);

                // Stage 3 — ConfirmIngestedAndTagged
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

                return new AddComingSoonResult
                {
                    Success = true,
                    FolderPath = folderPath,
                    FinalItemId = taggedMovie.Id
                };
            }
            catch (OperationCanceledException)
            {
                // AddMovieTask races this pipeline against ComingSoonEntryPoint's
                // ItemAdded fast path and deliberately cancels whichever one
                // loses. This is that expected, harmless outcome — not a bug —
                // so it's logged quietly rather than as an Error-level unhandled
                // exception with a full stack trace, which would be misleading
                // noise for anyone reading the server log.
                this.logger.Info(
                    "[AddPipeline] Pipeline for '{0}' was cancelled — superseded by a faster completion path.",
                    movie.Title);
                return new AddComingSoonResult
                {
                    Success = false,
                    FailedAtStage = AddComingSoonStage.Complete,
                    FailureReason = "Cancelled — superseded by a faster completion path.",
                    FolderPath = folderPath
                };
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[AddPipeline] Unhandled exception", ex);
                return FailAdd(AddComingSoonStage.Complete,
                    "Unhandled exception — see server log: " + ex.Message, folderPath);
            }
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        private static void ReportAdd(IProgress<double> progress, AddComingSoonStage stage)
        {
            progress?.Report(AddComingSoonStagePercent[(int)stage]);
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

        // FindMovieInFolder and FireAndForgetFullRefresh have moved to
        // EmbyLibrarySharedService — both were duplicated verbatim (or, in
        // FireAndForgetFullRefresh's case, generically reusable) between this
        // class and EmbyLibraryMakeService. See the base class for the current
        // implementations and doc comments.

        // -----------------------------------------------------------------------
        // Stub file helpers
        // -----------------------------------------------------------------------

        private static string GetStubExtension(string customStubPath)
        {
            if (!string.IsNullOrEmpty(customStubPath))
                return Path.GetExtension(customStubPath);          // e.g. ".mkv"
            return Path.GetExtension(StubResourceName);            // ".mp4"
        }

        private async Task WriteStubAsync(string destinationPath, string customStubPath)
        {
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
    }
}