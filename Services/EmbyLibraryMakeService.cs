// ManageComingSoon - Emby Library Make Service
// Handles the "Make Live" pipeline only.
// Derives from EmbyLibrarySharedService for all shared helpers.
//
// Public API consumed by MakeLivePageView / MakeLiveTask:
//   GetComingSoonItems()       — inherited from shared (also used by MakeLiveTask)
//   MakeLivePipelineAsync()    — eleven-checkpoint move+tag-removal pipeline

namespace ManageComingSoon.Services
{
    using ManageComingSoon.Model;
    using ManageComingSoon.Storage;
    using MediaBrowser.Common.Net;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Persistence;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class EmbyLibraryMakeService : EmbyLibrarySharedService
    {
        // -----------------------------------------------------------------------
        // Constructor — all dependencies forwarded to base
        // -----------------------------------------------------------------------
        public EmbyLibraryMakeService(
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
        // MakeLive mode
        // Internal implementation detail, not a user-facing or per-run choice.
        // The Advanced (reparent-in-place) strategy is settled as the sole
        // production path — confirmed working end-to-end as long as the
        // pipeline's own sequence is followed (destination folder created and
        // ingested, then the Movie row reparented onto it — see
        // Stage_LibraryUpdateAndRefreshAsync). Normal remains in the code as
        // the historical alternative but is never selected; the constant that
        // pins this (LiveMakeLiveMode) lives in MakeLivePageView.cs.
        // -----------------------------------------------------------------------

        public enum MakeLiveMode
        {
            /// <summary>
            /// Tag removal only. Never touches Path or parent on the live Movie
            /// row — Emby is left to discover the moved file as a new item via a
            /// plain Default-mode refresh. Historical alternative; not selected.
            /// </summary>
            Normal,

            /// <summary>
            /// Reparents the existing Movie row onto the bootstrapped destination
            /// Folder (Path + parent updated) before refreshing, preserving the
            /// item's identity (watch state, played position, etc.) across the
            /// move. Settled production path — see LiveMakeLiveMode.
            /// </summary>
            Advanced
        }

        // -----------------------------------------------------------------------
        // Pipeline stage checkpoints
        // MakeLiveStage / MakeLiveResult live in ManageComingSoon.Model — they're
        // the pipeline's data contract, not its implementation.
        // -----------------------------------------------------------------------

        // Fixed checkpoints so the progress bar advances identically regardless
        // of mode or whether a move is configured.
        private static readonly double[] MakeLiveStagePercent =
        {
            /* ReadinessCheck           */  5,
            /* CaptureState             */ 12,
            /* CreateTargetFolder       */ 20,
            /* EstablishTargetIds       */ 40,   // up-to-9 s no-new-content poll
            /* MoveFiles                */ 55,
            /* LibraryUpdateAndRefresh  */ 65,
            /* ConfirmTargetState       */ 85,   // up-to-80 s new-content poll
            /* RefreshSourceOrphans     */ 95,   // up-to-9 s no-new-content poll
            /* SteadyStateCheck         */ 98,
            /* UnlockTags               */ 99,
            /* Complete                 */ 100
        };

        // -----------------------------------------------------------------------
        // Pipeline context — carries all captured/derived state between stages
        // -----------------------------------------------------------------------
        private class MoveContext
        {
            public string SourceFolderPath;
            public string TargetLibraryRootPath;   // as configured; null when no move
            public string TargetMovieFolderPath;   // root + movie folder name
            public string MovieNewVideoPath;

            public bool MoveRequested;
            public bool DeleteStub;
            public int MaxStubFileSizeMb;
            public bool UnlockTags;
            public MakeLiveMode Mode;

            /// <summary>
            /// The Coming Soon placeholder Movie row, resolved ONCE via a single
            /// database lookup (FindMovieInFolder) in Stage_CaptureState. With
            /// Advanced mode settled as the only path, this reference is
            /// authoritative for the rest of the pipeline: Stage_LibraryUpdate-
            /// AndRefreshAsync reparents it in place (SaveItem, same InternalId/
            /// Guid) — it is never replaced by a new row. Every later stage
            /// (delete-stub, unlock-tags, etc.) should read/write THIS reference
            /// (or re-fetch by ctx.MovieOriginalInternalId if a fresh in-memory
            /// copy is needed) rather than re-resolving by path.
            /// </summary>
            public BaseItem Movie;
            public BaseItem FinalItem;
            public long MovieOriginalInternalId;
            public Guid MovieOriginalId;
            public string MovieOriginalVideoPath;

            public Folder SourceFolder;
            public long? SourceFolderInternalId;

            public CollectionFolder SourceLibrary;
            public CollectionFolder TargetLibrary;
            public Folder DestinationFolder;  // bootstrapped real Folder row, once known

            public string ApiKey;
            public string BaseUrl;
            public IHttpClient HttpClient;

            public string CustomStubPath;  // configured stub video, or null = embedded resource.
                                           // No longer used to identify the stub file on disk —
                                           // ctx.Movie.Path (database ground truth) is used instead.
                                           // Retained for possible future diagnostics/logging only.
            public string LogContext;       // e.g. " 'Inception (2024)' [2/7]"

            /// <summary>
            /// Called by each stage with a short human-readable description of what
            /// the pipeline is doing — drives the secondary-text line of the
            /// in-flight tracker row in the Make Live UI.
            /// </summary>
            public Action<string> OnStageMessage;
        }

        // -----------------------------------------------------------------------
        // Public pipeline entry point
        // -----------------------------------------------------------------------

        public async Task<MakeLiveResult> MakeLivePipelineAsync(
            string folderPath,
            string targetPath,
            bool deleteStub,
            int maxStubFileSizeMb,
            bool unlockTags,
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
                MaxStubFileSizeMb = maxStubFileSizeMb,
                UnlockTags = unlockTags,
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
                // Stage 0 — ReadinessCheck (MigrationAnalyzer gate ran in page view)
                Report(progress, MakeLiveStage.ReadinessCheck);

                ctx.OnStageMessage?.Invoke("Reading library state\u2026");
                if (!Stage_CaptureState(ctx))
                    return Fail(ctx, MakeLiveStage.CaptureState,
                        "Could not resolve the Movie item for this folder.");
                ctx.LogContext = " '" + ctx.Movie.Name + "'" +
                    (string.IsNullOrEmpty(logContext) ? string.Empty : " " + logContext);
                Report(progress, MakeLiveStage.CaptureState);

                if (ctx.DeleteStub)
                {
                    string stubFailure;
                    if (!CheckStubFileReady(ctx, out stubFailure))
                        return Fail(ctx, MakeLiveStage.CaptureState, stubFailure);
                }

                if (ctx.MoveRequested)
                {
                    ctx.OnStageMessage?.Invoke("Creating destination folder\u2026");
                    if (!await Stage_CreateTargetFolderAsync(ctx, token).ConfigureAwait(false))
                        return Fail(ctx, MakeLiveStage.CreateTargetFolder,
                            "Could not create the destination folder.");
                }
                Report(progress, MakeLiveStage.CreateTargetFolder);

                if (ctx.MoveRequested)
                {
                    ctx.OnStageMessage?.Invoke("Registering destination folder\u2026");
                    if (!await Stage_EstablishTargetIdsAsync(ctx, token).ConfigureAwait(false))
                        return Fail(ctx, MakeLiveStage.EstablishTargetIds,
                            "Destination folder was not ingested by Emby in time.");
                }
                Report(progress, MakeLiveStage.EstablishTargetIds);

                // Mode C (Isolation): suppress LibraryMonitor around the physical
                // move. Confirmed to stop source-side identity loss by ruling out
                // LibraryMonitor's own debounced refresh racing the explicit REST calls.
                bool suppressMonitor = ctx.MoveRequested && ctx.Mode == MakeLiveMode.Advanced;
                if (ctx.MoveRequested)
                {
                    if (!CheckDiskSpaceForMove(ctx))
                        return Fail(ctx, MakeLiveStage.MoveFiles,
                            "Insufficient disk space on destination drive.", isHardStop: true);

                    ctx.OnStageMessage?.Invoke("Moving files\u2026");

                    if (suppressMonitor)
                    {
                        this.logger.Info(
                            "[Pipeline/MoveFiles]{0} Suppressing LibraryMonitor for source and destination during move.",
                            ctx.LogContext);
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
                        return Fail(ctx, MakeLiveStage.MoveFiles,
                            "Failed to move files to the destination folder.");
                }
                Report(progress, MakeLiveStage.MoveFiles);

                ctx.OnStageMessage?.Invoke("Removing Coming Soon tag\u2026");
                if (!await Stage_LibraryUpdateAndRefreshAsync(ctx, token).ConfigureAwait(false))
                    return Fail(ctx, MakeLiveStage.LibraryUpdateAndRefresh,
                        "Library refresh call failed — check the configured API key.");
                Report(progress, MakeLiveStage.LibraryUpdateAndRefresh);

                ctx.OnStageMessage?.Invoke("Confirming library state\u2026");
                if (!await Stage_ConfirmTargetStateAsync(ctx, token).ConfigureAwait(false))
                    return Fail(ctx, MakeLiveStage.ConfirmTargetState,
                        "Movie was not confirmed at the target path within the timeout.");
                Report(progress, MakeLiveStage.ConfirmTargetState);

                // Stub deletion happens AFTER reparent + confirm, not before.
                // ctx.Movie's reparented Path is always ctx.MovieNewVideoPath —
                // deterministically the STUB's own original filename, moved to
                // the target folder — never some differently-named "real" file.
                // Deleting it before Stage_LibraryUpdateAndRefreshAsync/
                // ConfirmTargetState means the Movie gets reparented onto (and
                // then refreshed against) a file that's already gone, and Emby's
                // own refresh call removes the row entirely instead of keeping
                // it — this was reproduced end-to-end in testing: "ItemRemoved |
                // Type=Movie ... InternalId=...", followed by ConfirmTargetState
                // only ever finding the bare target Folder because the Movie
                // itself no longer existed to be found.
                // Deleting here, after identity is already locked in, is safe.
                ctx.OnStageMessage?.Invoke("Cleaning up placeholder stub\u2026");
                await Stage_DeletePlaceholderStubAsync(ctx, token).ConfigureAwait(false);

                if (ctx.MoveRequested)
                {
                    // MoveFiles moves only the source folder's CONTENTS (see
                    // comment there) so the empty source folder itself is always
                    // left behind and always needs tidying up here. By this
                    // point ctx.Movie has already been reparented onto the
                    // target (Stage_LibraryUpdateAndRefreshAsync, Advanced —
                    // the settled path), so it's safe to remove the old folder.
                    ctx.OnStageMessage?.Invoke("Cleaning up source folder\u2026");
                    await Stage_CleanupSourceFolderAsync(ctx, token).ConfigureAwait(false);
                }
                Report(progress, MakeLiveStage.RefreshSourceOrphans);

                ctx.OnStageMessage?.Invoke("Final state check\u2026");
                var steadyState = await Stage_CheckSteadyStateAsync(ctx, token).ConfigureAwait(false);
                Report(progress, MakeLiveStage.SteadyStateCheck);

                ctx.OnStageMessage?.Invoke("Unlocking tags field\u2026");
                Stage_UnlockTags(ctx);
                Report(progress, MakeLiveStage.UnlockTags);

                this.logger.Info("[Pipeline/Complete]{0} ===== SUCCESS =====", ctx.LogContext);
                Report(progress, MakeLiveStage.Complete);

                return new MakeLiveResult
                {
                    Success = true,
                    FinalItemId = steadyState.FinalId,
                    IdentityPreserved = steadyState.IdentityPreserved,
                    DuplicateDetected = steadyState.DuplicateDetected,
                };
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[Pipeline]{0} Unhandled exception", ex, ctx.LogContext);
                return Fail(ctx, MakeLiveStage.Complete,
                    "Unhandled exception — see server log: " + ex.Message);
            }
        }

        // -----------------------------------------------------------------------
        // Stage implementations
        // -----------------------------------------------------------------------

        private bool Stage_CaptureState(MoveContext ctx)
        {
            ctx.Movie = FindMovieInFolder(ctx.SourceFolderPath);
            if (ctx.Movie == null)
            {
                this.logger.Warn("[Pipeline/CaptureState]{0} No tagged Movie found in {1}",
                    ctx.LogContext, ctx.SourceFolderPath);
                return false;
            }

            ctx.MovieOriginalInternalId = ctx.Movie.InternalId;
            ctx.MovieOriginalId = ctx.Movie.Id;
            ctx.MovieOriginalVideoPath = ctx.Movie.Path;

            LogItemState("Pipeline/CaptureState - Movie", "Movie", ctx.Movie);

            ctx.SourceLibrary = FindCollectionFolder(ctx.SourceFolderPath);
            if (ctx.SourceLibrary == null)
                this.logger.Warn("[Pipeline/CaptureState]{0} Could not resolve source CollectionFolder.", ctx.LogContext);

            if (ctx.MoveRequested)
            {
                string folderName = Path.GetFileName(ctx.SourceFolderPath);
                ctx.TargetMovieFolderPath = Path.Combine(ctx.TargetLibraryRootPath, folderName);
                string videoFileName = Path.GetFileName(ctx.MovieOriginalVideoPath ?? string.Empty);
                ctx.MovieNewVideoPath = Path.Combine(ctx.TargetMovieFolderPath, videoFileName);

                ctx.TargetLibrary = FindCollectionFolder(ctx.TargetLibraryRootPath);
                if (ctx.TargetLibrary == null)
                    this.logger.Warn("[Pipeline/CaptureState]{0} Could not resolve target CollectionFolder.", ctx.LogContext);
            }

            ctx.SourceFolder = this.libraryManager.FindByPath(ctx.SourceFolderPath, true) as Folder;
            if (ctx.SourceFolder != null)
            {
                ctx.SourceFolderInternalId = ctx.SourceFolder.InternalId;
                LogItemState("Pipeline/CaptureState - SourceFolder", "Movie Folder", ctx.SourceFolder);
            }

            return true;
        }

        private async Task<bool> Stage_CreateTargetFolderAsync(MoveContext ctx, CancellationToken token)
        {
            try
            {
                Directory.CreateDirectory(ctx.TargetMovieFolderPath);
                this.logger.Info("[Pipeline/CreateTargetFolder]{0} Created: {1}",
                    ctx.LogContext, ctx.TargetMovieFolderPath);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "[Pipeline/CreateTargetFolder]{0} Failed to create {1}",
                    ex, ctx.LogContext, ctx.TargetMovieFolderPath);
                return false;
            }

            if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false)) return false;

            try
            {
                // ValidationOnly: only needs to register the new empty folder in
                // Emby's index — not trigger a full metadata pass.
                await CallRefreshEndpointAsync(
                    ctx.HttpClient, ctx.BaseUrl, ctx.TargetLibrary.InternalId,
                    "Library (target, bootstrap)", ctx.ApiKey,
                    MetadataRefreshMode.ValidationOnly,
                    "Pipeline/CreateTargetFolder/BootstrapRefresh", token, ctx.LogContext)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "[Pipeline/CreateTargetFolder]{0} Bootstrap refresh failed", ex, ctx.LogContext);
                return false;
            }

            return true;
        }

        private async Task<bool> Stage_EstablishTargetIdsAsync(MoveContext ctx, CancellationToken token)
        {
            bool met = await WaitForConditionAsync(
                () =>
                {
                    ctx.DestinationFolder =
                        this.libraryManager.FindByPath(ctx.TargetMovieFolderPath, true) as Folder;
                    return ctx.DestinationFolder != null;
                },
                token, "Pipeline/EstablishTargetIds",
                NoNewContentFirstWaitSeconds, NoNewContentSecondWaitSeconds,
                logContext: ctx.LogContext).ConfigureAwait(false);

            if (!met)
            {
                this.logger.Warn(
                    "[Pipeline/EstablishTargetIds]{0} DestinationFolder not ingested in time at {1}",
                    ctx.LogContext, ctx.TargetMovieFolderPath);
                return false;
            }

            LogItemState("Pipeline/EstablishTargetIds - DestinationFolder",
                "Movie Folder", ctx.DestinationFolder);
            return true;
        }

        private bool Stage_MoveFiles(MoveContext ctx)
        {
            // NOTE: we deliberately move CONTENTS, not the folder itself.
            // TargetMovieFolderPath was already created (Stage_CreateTargetFolder)
            // and registered with Emby (Stage_EstablishTargetIds) so real target
            // IDs could be obtained before any files moved. Directory.Move()
            // requires its destination to NOT already exist — calling it against
            // TargetMovieFolderPath here would therefore throw every single time.
            // Moving contents into the pre-existing destination sidesteps that
            // entirely, and leaves an empty source folder behind for
            // Stage_CleanupSourceFolderAsync to remove later.
            try
            {
                foreach (var filePath in Directory.GetFiles(ctx.SourceFolderPath))
                {
                    string destFile = Path.Combine(ctx.TargetMovieFolderPath, Path.GetFileName(filePath));
                    File.Move(filePath, destFile);
                }

                foreach (var dirPath in Directory.GetDirectories(ctx.SourceFolderPath))
                {
                    string destDir = Path.Combine(ctx.TargetMovieFolderPath, Path.GetFileName(dirPath));
                    // destDir doesn't pre-exist (it's a sub-folder name, not the
                    // bootstrapped root), so a plain Directory.Move/rename is fine.
                    Directory.Move(dirPath, destDir);
                }

                this.logger.Info("[Pipeline/MoveFiles]{0} Moved contents of '{1}' → '{2}'",
                    ctx.LogContext, ctx.SourceFolderPath, ctx.TargetMovieFolderPath);
                return true;
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "[Pipeline/MoveFiles]{0} Failed moving folder contents into '{1}'",
                    ex, ctx.LogContext, ctx.TargetMovieFolderPath);
                return false;
            }
        }

        /// <summary>
        /// Preflight check for stub deletion, run once ctx.Movie is known (right
        /// after Stage_CaptureState succeeds, before any destructive work).
        /// The database's own Path for the placeholder (ctx.MovieOriginalVideoPath
        /// / ctx.Movie.Path — same value at this point in the pipeline) is ground
        /// truth: no filename is reconstructed or guessed from the folder name or
        /// a configured custom stub extension.
        ///
        /// Confirms both that the file still exists on disk (the DB and
        /// filesystem should always agree, but this catches the rare case where
        /// they don't) and that it's within the configured maximum size — a
        /// single check, since size can only be read once existence is confirmed.
        /// Only called when ctx.DeleteStub is requested; the max-size setting is
        /// moot otherwise, mirroring its EnabledCondition on the Configuration tab.
        ///
        /// Failure here is per-item, not a batch-wide hard stop — each folder's
        /// stub file size is independent, unlike disk space which is drive-wide.
        /// </summary>
        private bool CheckStubFileReady(MoveContext ctx, out string failureReason)
        {
            failureReason = null;
            string stubPath = ctx.MovieOriginalVideoPath;

            this.logger.Debug(
                "[Pipeline/StubPreflight]{0} Checking placeholder at '{1}' (database path) against " +
                "configured max {2} MB.", ctx.LogContext, stubPath, ctx.MaxStubFileSizeMb);

            if (string.IsNullOrEmpty(stubPath) || !File.Exists(stubPath))
            {
                failureReason = string.Format(
                    "The database records the placeholder for this item at '{0}', but no file was " +
                    "found there on disk. The database and filesystem are out of sync for this item " +
                    "— resolve manually before retrying.",
                    stubPath);
                this.logger.Warn("[Pipeline/StubPreflight]{0} {1}", ctx.LogContext, failureReason);
                return false;
            }

            long sizeBytes;
            try
            {
                sizeBytes = new FileInfo(stubPath).Length;
            }
            catch (Exception ex)
            {
                failureReason = string.Format(
                    "Could not read the placeholder's file size at '{0}': {1}", stubPath, ex.Message);
                this.logger.Warn("[Pipeline/StubPreflight]{0} {1}", ctx.LogContext, failureReason);
                return false;
            }

            long maxBytes = (long)ctx.MaxStubFileSizeMb * 1024 * 1024;
            if (sizeBytes > maxBytes)
            {
                failureReason = string.Format(
                    "Placeholder '{0}' is {1:N0} MB, exceeding the configured maximum of {2} MB. " +
                    "Refusing to proceed with stub deletion enabled — increase the limit on the " +
                    "Configuration tab, or disable stub deletion, then retry.",
                    Path.GetFileName(stubPath), sizeBytes / (1024.0 * 1024.0), ctx.MaxStubFileSizeMb);
                this.logger.Warn("[Pipeline/StubPreflight]{0} {1}", ctx.LogContext, failureReason);
                return false;
            }

            this.logger.Debug(
                "[Pipeline/StubPreflight]{0} OK: '{1}' is {2:N0} bytes (limit {3} MB).",
                ctx.LogContext, stubPath, sizeBytes, ctx.MaxStubFileSizeMb);
            return true;
        }

        /// <summary>
        /// Deletes the placeholder stub video, when the caller requested it
        /// (ctx.DeleteStub) — consolidated here from MakeLiveTask so it runs
        /// inside the pipeline itself, before Stage_LibraryUpdateAndRefreshAsync's
        /// refresh call, instead of after the pipeline returns where no refresh
        /// call would ever see it for the last item in a batch.
        ///
        /// Ground truth: ctx.Movie.Path IS the database's current record for this
        /// item — no filename reconstruction. Pre-reparent it's the original
        /// placeholder path; by this point in the pipeline (after
        /// Stage_LibraryUpdateAndRefreshAsync's reparent) it's already the moved
        /// location. Same configured max-size limit as the preflight check in
        /// CheckStubFileReady; re-checked here as belt-and-braces in case the
        /// file changed on disk between preflight and this stage.
        /// </summary>
        private async Task Stage_DeletePlaceholderStubAsync(MoveContext ctx, CancellationToken token)
        {
            if (!ctx.DeleteStub) return;

            string stubPath = ctx.Movie.Path;

            if (string.IsNullOrEmpty(stubPath) || !File.Exists(stubPath))
            {
                this.logger.Info(
                    "[Pipeline/DeletePlaceholderStub]{0} Stub not found at '{1}' (already removed?).",
                    ctx.LogContext, stubPath);
                return;
            }

            long sizeBytes;
            try
            {
                sizeBytes = new FileInfo(stubPath).Length;
            }
            catch (Exception ex)
            {
                this.logger.Warn(
                    "[Pipeline/DeletePlaceholderStub]{0} Could not read size of '{1}': {2}. Skipping delete.",
                    ctx.LogContext, stubPath, ex.Message);
                return;
            }

            long maxBytes = (long)ctx.MaxStubFileSizeMb * 1024 * 1024;
            if (sizeBytes > maxBytes)
            {
                this.logger.Warn(
                    "[Pipeline/DeletePlaceholderStub]{0} File at '{1}' is {2:N0} bytes (exceeds " +
                    "configured {3} MB stub threshold). Not deleting to avoid data loss. This should " +
                    "already have been caught by the preflight check — remove manually if it is " +
                    "genuinely a stub.",
                    ctx.LogContext, stubPath, sizeBytes, ctx.MaxStubFileSizeMb);
                return;
            }

            try
            {
                File.Delete(stubPath);
                this.logger.Info("[Pipeline/DeletePlaceholderStub]{0} Stub deleted: {1}",
                    ctx.LogContext, stubPath);
            }
            catch (Exception ex)
            {
                this.logger.Warn(
                    "[Pipeline/DeletePlaceholderStub]{0} Could not delete stub '{1}': {2}",
                    ctx.LogContext, stubPath, ex.Message);
                return;
            }

            // Follow-up refresh so THIS item's own run notices the stub is gone,
            // instead of relying on some later item's refresh (the "last item in
            // the batch never gets cleaned up" problem). This is safe to scope
            // narrowly, unlike Stage_CleanupSourceFolderAsync's refresh: the
            // folder we're targeting here (DestinationFolder / SourceFolder)
            // still physically exists — we only removed one file inside it, so
            // Emby can validate its children without hitting a
            // DirectoryNotFoundException.
            long? targetId = ctx.MoveRequested
                ? ctx.DestinationFolder?.InternalId
                : ctx.SourceFolderInternalId;
            if (!targetId.HasValue) return;
            if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false)) return;

            try
            {
                await CallRefreshEndpointAsync(
                    ctx.HttpClient, ctx.BaseUrl, targetId.Value,
                    "Movie Folder (post stub-delete refresh)", ctx.ApiKey,
                    MetadataRefreshMode.Default,
                    "Pipeline/DeletePlaceholderStub", token, ctx.LogContext)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.Warn(
                    "[Pipeline/DeletePlaceholderStub]{0} Follow-up refresh failed (non-fatal): {1}",
                    ctx.LogContext, ex.Message);
            }
        }

        /// <summary>
        /// Reverts the Tags lock instantiated by ComingSoonEntryPoint when the
        /// placeholder was first ingested. Deliberately runs LAST — after every
        /// other stage that could trigger a metadata refresh (the follow-up
        /// refresh inside Stage_DeletePlaceholderStubAsync, and the refresh
        /// inside Stage_CleanupSourceFolderAsync). Unlocking any earlier would
        /// risk exactly the tag-loss scenario the lock exists to prevent — see
        /// ComingSoonEntryPoint's own comment on the subtitle-triggered re-probe
        /// case that motivated the lock in the first place.
        ///
        /// Skipped entirely when ctx.DeleteStub is true: stub deletion removes
        /// the very file ctx.Movie.Path points to (see
        /// Stage_DeletePlaceholderStubAsync), so this row is expected to be
        /// superseded once real content eventually lands and Emby re-derives it.
        /// Unlocking a row that's about to go stale anyway is wasted work, and
        /// skipping it removes the whole category of "row vanished mid-refresh"
        /// risk rather than just catching it after the fact.
        ///
        /// Re-fetches by ctx.MovieOriginalInternalId (stable across the Advanced-
        /// mode reparent — SaveItem preserves it) rather than trusting the
        /// in-memory ctx.Movie reference, since two refreshes have run since it
        /// was last touched. Non-fatal on any failure — logs a single calm Warn
        /// line and continues; the pipeline's overall Success is unaffected.
        /// </summary>
        private void Stage_UnlockTags(MoveContext ctx)
        {
            if (ctx.DeleteStub)
            {
                this.logger.Debug(
                    "[Pipeline/UnlockTags]{0} Skipped — stub deletion means this row is expected to " +
                    "be superseded once real content lands.", ctx.LogContext);
                return;
            }

            if (!ctx.UnlockTags)
            {
                this.logger.Debug(
                    "[Pipeline/UnlockTags]{0} Skipped — disabled in Configuration.", ctx.LogContext);
                return;
            }

            BaseItem current;
            try
            {
                current = this.libraryManager.GetItemById(ctx.MovieOriginalInternalId);
            }
            catch (Exception ex)
            {
                this.logger.Warn(
                    "[Pipeline/UnlockTags]{0} Lookup by InternalId={1} failed: {2}. Non-fatal, continuing.",
                    ctx.LogContext, ctx.MovieOriginalInternalId, ex.Message);
                return;
            }

            if (current == null)
            {
                this.logger.Warn(
                    "[Pipeline/UnlockTags]{0} Movie no longer present in the library (InternalId={1}) " +
                    "— unexpected since no stub deletion occurred. Non-fatal, continuing.",
                    ctx.LogContext, ctx.MovieOriginalInternalId);
                return;
            }

            if (current.LockedFields == null ||
                !current.LockedFields.Contains(MediaBrowser.Model.Entities.MetadataFields.Tags))
            {
                this.logger.Debug(
                    "[Pipeline/UnlockTags]{0} Tags not locked — nothing to unlock.", ctx.LogContext);
                return;
            }

            try
            {
                current.LockedFields = current.LockedFields
                    .Where(f => f != MediaBrowser.Model.Entities.MetadataFields.Tags)
                    .ToArray();
                this.libraryManager.UpdateItem(current, current.GetParent(), ItemUpdateType.MetadataEdit, null);
                this.logger.Info("[Pipeline/UnlockTags]{0} Tags field unlocked.", ctx.LogContext);
            }
            catch (Exception ex)
            {
                this.logger.Warn(
                    "[Pipeline/UnlockTags]{0} Could not unlock Tags: {1}. Non-fatal, continuing.",
                    ctx.LogContext, ex.Message);
            }
        }

        private async Task<bool> Stage_LibraryUpdateAndRefreshAsync(MoveContext ctx, CancellationToken token)
        {
            string activeTag = ActiveTagText;
            var tags = (ctx.Movie.Tags ?? new string[0])
                .Where(t => !string.Equals(t, activeTag, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            ctx.Movie.Tags = tags;

            if (!ctx.MoveRequested)
            {
                // No folder boundary crossed — identical for both modes.
                this.libraryManager.UpdateItem(ctx.Movie, ctx.Movie.GetParent(),
                    ItemUpdateType.MetadataEdit, null);
                LogItemState("Pipeline/LibraryUpdateAndRefresh - tag removed (no move)", "Movie", ctx.Movie);

                if (ctx.SourceLibrary == null) return false;
                if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false)) return false;

                try
                {
                    await CallRefreshEndpointAsync(
                        ctx.HttpClient, ctx.BaseUrl, ctx.SourceLibrary.InternalId,
                        "Library", ctx.ApiKey, MetadataRefreshMode.Default,
                        "Pipeline/LibraryUpdateAndRefresh/Trigger", token, ctx.LogContext)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException(
                        "[Pipeline/LibraryUpdateAndRefresh]{0} Default refresh failed", ex, ctx.LogContext);
                    return false;
                }
                return true;
            }

            if (ctx.Mode == MakeLiveMode.Advanced)
            {
                // Reparent the existing Movie row onto the real, bootstrapped
                // destination Folder, attempting to preserve its identity.
                // SaveItem persists the row keyed by its EXISTING InternalId/Guid,
                // with no re-derivation involved — this is the actual fix for the
                // duplicate-row symptom. See the original EmbyLibraryService for
                // full derivation history.
                ctx.Movie.Path = ctx.MovieNewVideoPath;
                ctx.Movie.ParentId = ctx.DestinationFolder.InternalId;

                this.itemRepository.SaveItem(ctx.Movie, token);
                LogItemState(
                    "Pipeline/LibraryUpdateAndRefresh - tag+path+ParentId SaveItem (persisted directly)",
                    "Movie", ctx.Movie);

                this.libraryManager.UpdateItem(ctx.Movie, ctx.DestinationFolder,
                    ItemUpdateType.MetadataEdit, null);
                LogItemState(
                    "Pipeline/LibraryUpdateAndRefresh - UpdateItem (cache/event refresh)",
                    "Movie", ctx.Movie);

                this.logger.Info(
                    "[Pipeline/LibraryUpdateAndRefresh]{0} Reparented onto target Folder InternalId={1} ('{2}').",
                    ctx.LogContext, ctx.DestinationFolder.InternalId, ctx.DestinationFolder.Path);
            }
            else
            {
                // Normal mode: tag removal only.
                this.libraryManager.UpdateItem(ctx.Movie, ctx.Movie.GetParent(),
                    ItemUpdateType.MetadataEdit, null);
                LogItemState(
                    "Pipeline/LibraryUpdateAndRefresh - tag removed, path/parent untouched",
                    "Movie", ctx.Movie);
            }

            if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false)) return false;

            try
            {
                await CallRefreshEndpointAsync(
                    ctx.HttpClient, ctx.BaseUrl, ctx.TargetLibrary.InternalId,
                    "Library (target)", ctx.ApiKey, MetadataRefreshMode.Default,
                    "Pipeline/LibraryUpdateAndRefresh/Trigger", token, ctx.LogContext)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "[Pipeline/LibraryUpdateAndRefresh]{0} Default refresh failed for target library",
                    ex, ctx.LogContext);
                return false;
            }
            return true;
        }

        private async Task<bool> Stage_ConfirmTargetStateAsync(MoveContext ctx, CancellationToken token)
        {
            string expectedPath = ctx.MoveRequested ? ctx.TargetMovieFolderPath : ctx.SourceFolderPath;

            BaseItem confirmedItem = null;
            bool met = await WaitForConditionAsync(
                () =>
                {
                    confirmedItem = this.libraryManager.FindByPath(expectedPath, true);
                    return confirmedItem != null;
                },
                token, "Pipeline/ConfirmTargetState",
                NewContentFirstWaitSeconds, NewContentSecondWaitSeconds,
                logContext: ctx.LogContext).ConfigureAwait(false);

            if (!met) return false;

            ctx.FinalItem = confirmedItem;
            LogItemState("Pipeline/ConfirmTargetState - confirmed", "Movie Folder", ctx.FinalItem);
            return true;
        }

        /// <summary>
        /// Tidies up the now-empty source folder after a successful move+reparent/
        /// re-discovery. Runs for BOTH modes (Normal and Advanced) — by this point
        /// MoveFiles has already relocated every file/sub-folder out of
        /// SourceFolderPath, and LibraryUpdateAndRefresh + ConfirmTargetState have
        /// already confirmed the Movie is live at the target, so the leftover
        /// source folder is safe to remove. This is deliberately the ONE place in
        /// the pipeline that deletes the source folder — non-fatal on any failure,
        /// since the Movie itself has already been made live either way.
        /// </summary>
        private async Task Stage_CleanupSourceFolderAsync(MoveContext ctx, CancellationToken token)
        {
            if (!ctx.MoveRequested) return;

            // Safety check, same posture as the stub-size guard elsewhere in the
            // app: only ever delete a folder we can positively confirm is empty.
            // A non-empty folder here means something didn't get moved (e.g. a
            // partial MoveFiles failure that we otherwise recovered from, or an
            // external write mid-run) — leave it for manual review rather than
            // risk deleting content that was never moved.
            try
            {
                if (Directory.Exists(ctx.SourceFolderPath))
                {
                    bool isEmpty = !Directory.EnumerateFileSystemEntries(ctx.SourceFolderPath).Any();
                    if (!isEmpty)
                    {
                        this.logger.Warn(
                            "[Pipeline/CleanupSourceFolder]{0} '{1}' is not empty — leaving in place " +
                            "rather than risk deleting unmoved content. Remove manually if appropriate.",
                            ctx.LogContext, ctx.SourceFolderPath);
                        return;
                    }

                    Directory.Delete(ctx.SourceFolderPath, recursive: false);
                    this.logger.Info("[Pipeline/CleanupSourceFolder]{0} Removed empty source folder '{1}'.",
                        ctx.LogContext, ctx.SourceFolderPath);
                }
            }
            catch (Exception ex)
            {
                this.logger.Warn(
                    "[Pipeline/CleanupSourceFolder]{0} Could not remove '{1}': {2} (non-fatal).",
                    ctx.LogContext, ctx.SourceFolderPath, ex.Message);
                return;
            }

            // Let Emby's DB catch up with the folder's removal.
            //
            // Refresh the whole SOURCE LIBRARY (ctx.SourceLibrary), not
            // ctx.SourceFolderInternalId. We previously narrowed this down to
            // the single deleted folder's own item id to avoid disturbing
            // sibling folders mid-batch — but that folder's directory no
            // longer exists on disk at this point (we just deleted it above),
            // and asking Emby to validate the CHILDREN of an item whose own
            // directory is gone throws a DirectoryNotFoundException inside
            // ProviderManager ("Error validating children..."), reproduced in
            // testing for every single item. The library's own root directory
            // still exists, so refreshing it recurses down safely and simply
            // notices one child folder is missing — completely normal orphan
            // detection, no exception.
            //
            // The original worry about this collateral-scanning a not-yet-
            // processed sibling (and stripping ITS "Coming Soon" tag via an
            // unrelated subtitle-triggered re-probe) is now covered separately:
            // ComingSoonEntryPoint locks the Tags field the moment it's
            // applied, so no metadata refresh — however broad — can overwrite
            // it anymore. That closes the original hole directly, so this
            // refresh no longer needs to be scoped down that tightly.
            if (ctx.SourceLibrary == null) return;
            if (!await EnsureHttpAsync(ctx, token).ConfigureAwait(false)) return;

            try
            {
                await CallRefreshEndpointAsync(
                    ctx.HttpClient, ctx.BaseUrl, ctx.SourceLibrary.InternalId,
                    "Library (source, orphan cleanup)", ctx.ApiKey,
                    MetadataRefreshMode.Default,
                    "Pipeline/CleanupSourceFolder", token, ctx.LogContext)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.Warn(
                    "[Pipeline/CleanupSourceFolder]{0} Refresh failed (non-fatal): {1}",
                    ctx.LogContext, ex.Message);
                return;
            }

            await WaitForConditionAsync(
                () => this.libraryManager.FindByPath(ctx.SourceFolderPath, true) == null,
                token, "Pipeline/CleanupSourceFolder/WaitForRemoval",
                NoNewContentFirstWaitSeconds, NoNewContentSecondWaitSeconds,
                logContext: ctx.LogContext).ConfigureAwait(false);
        }

        private class SteadyStateResult
        {
            public Guid? FinalId;
            public bool? IdentityPreserved;
            public bool? DuplicateDetected;
        }

        private async Task<SteadyStateResult> Stage_CheckSteadyStateAsync(
            MoveContext ctx, CancellationToken token)
        {
            if (!ctx.MoveRequested)
                return new SteadyStateResult { FinalId = ctx.FinalItem?.Id };

            string expectedPath = ctx.TargetMovieFolderPath;
            var allAtPath = FindAllByPath(expectedPath, isFolder: true);

            bool duplicateDetected = allAtPath.Length > 1;
            bool identityPreserved = allAtPath.Any(i => i.InternalId == ctx.MovieOriginalInternalId);
            bool originalAmongResults = identityPreserved;

            Guid? finalId = allAtPath.Length > 0 ? allAtPath[0].Id : (Guid?)null;

            if (duplicateDetected)
            {
                this.logger.Warn(
                    "[Pipeline/SteadyStateCheck]{0} DUPLICATE detected: {1} item(s) at '{2}' " +
                    "(InternalIds: [{3}]); original InternalId={4} is {5}. Advanced mode SaveItem " +
                    "successfully persisted the original row's new path/parent, but the recursive " +
                    "folder-validate scan subsequently re-derived a Guid from the NEW path and " +
                    "registered the original row as a recognized child under the new parent at the " +
                    "relational level that the recursive folder-validate scan checks — so the scan " +
                    "creates a fresh row for the same file. The move itself succeeded, but watch " +
                    "state/userdata on the original row may not have carried over.",
                    ctx.LogContext, allAtPath.Length, expectedPath,
                    string.Join(", ", allAtPath.Select(i => i.InternalId.ToString())),
                    ctx.MovieOriginalInternalId,
                    originalAmongResults ? "present" : "NOT present");
            }
            else if (ctx.MoveRequested && !identityPreserved && allAtPath.Length == 1)
            {
                this.logger.Warn(
                    "[Pipeline/SteadyStateCheck]{0} Identity NOT preserved: item at '{1}' has " +
                    "InternalId={2}, expected {3}. The move succeeded, but watch state/userdata on " +
                    "the original row was NOT carried over.",
                    ctx.LogContext, expectedPath,
                    allAtPath.Length > 0 ? allAtPath[0].InternalId.ToString() : "none",
                    ctx.MovieOriginalInternalId);
            }

            this.logger.Info(
                "[Pipeline/SteadyStateCheck]{0} identity preserved={1}, duplicate detected={2}.",
                ctx.LogContext, identityPreserved, duplicateDetected);

            return new SteadyStateResult
            {
                FinalId = finalId,
                IdentityPreserved = identityPreserved,
                DuplicateDetected = duplicateDetected
            };
        }

        // -----------------------------------------------------------------------
        // HTTP context helper
        // -----------------------------------------------------------------------

        private async Task<bool> EnsureHttpAsync(MoveContext ctx, CancellationToken token)
        {
            if (ctx.HttpClient != null && !string.IsNullOrEmpty(ctx.BaseUrl)) return true;

            var http = await ResolveHttpAsync(ctx.ApiKey, "Pipeline", token).ConfigureAwait(false);
            if (http == null) return false;

            ctx.HttpClient = http.Value.Client;
            ctx.BaseUrl = http.Value.BaseUrl;
            return true;
        }

        // -----------------------------------------------------------------------
        // Progress / failure helpers
        // -----------------------------------------------------------------------

        private static void Report(IProgress<double> progress, MakeLiveStage stage)
        {
            progress?.Report(MakeLiveStagePercent[(int)stage]);
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
        // Disk space check (cross-drive moves only)
        // -----------------------------------------------------------------------

        private bool CheckDiskSpaceForMove(MoveContext ctx)
        {
            string srcRoot = Path.GetPathRoot(ctx.SourceFolderPath);
            string dstRoot = Path.GetPathRoot(ctx.TargetMovieFolderPath);

            if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                return true;  // same drive — rename, no space consumed

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
                available = new DriveInfo(dstRoot).AvailableFreeSpace;
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

        // -----------------------------------------------------------------------
        // Item lookup helpers
        // -----------------------------------------------------------------------

        // FindMovieInFolder has moved to EmbyLibrarySharedService — it was
        // duplicated verbatim between this class and EmbyLibraryAddService.
        // The testing-derived doc comment that used to live here (explaining why
        // FindByPath(folderPath) resolves to the wrong item) now lives on the
        // base class implementation.

        /// <summary>
        /// Returns EVERY item matching this exact Path, not just one.
        /// Deliberately NOT using libraryManager.FindByPath — per Emby's own source,
        /// FindByPath sorts by DateCreated desc and takes Limit=1, silently hiding
        /// duplicate rows. This does the same query without Limit/OrderBy so
        /// duplication is visible.
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
    }
}