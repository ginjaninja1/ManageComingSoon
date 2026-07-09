namespace ManageComingSoon.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Tasks;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Tasks;

    public class MakeLiveTask : IScheduledTask
    {
        // Static dependencies – set by ManageComingSoonPlugin before any execution
        private static EmbyLibraryMakeService staticLibraryService;
        private static ILogger staticLogger;
        private static Func<string> staticGetApiKey;
        private static Func<int> staticGetMaxStubFileSizeMb;
        private static Func<bool> staticGetUnlockTags;

        public static void SetDependencies(
            EmbyLibraryMakeService libraryService,
            ILogger logger,
            Func<string> getApiKey,
            Func<int> getMaxStubFileSizeMb,
            Func<bool> getUnlockTags)
        {
            staticLibraryService = libraryService;
            staticLogger = logger;
            staticGetApiKey = getApiKey;
            staticGetMaxStubFileSizeMb = getMaxStubFileSizeMb;
            staticGetUnlockTags = getUnlockTags;
        }

        public static event Action ItemCompleted;

        public MakeLiveTask() { }

        private EmbyLibraryMakeService LibraryService => staticLibraryService;
        private ILogger Logger => staticLogger;

        public string Name => "Make Coming Soon Live";
        public string Description => "Managed by the Manage Coming Soon plugin. Do not schedule manually — trigger via the Make Live tab only.";
        public string Category => "GinjaNinja Tools";
        public string Key => "ManageComingSoon_MakeLive";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
            => new TaskTriggerInfo[0];

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var items = MakeLiveTracker.DequeueAll();

            if (items.Length == 0)
            {
                this.Logger.Info("ManageComingSoon: MakeLiveTask started but queue is empty – nothing to do.");
                progress.Report(100);
                return;
            }

            string apiKey = staticGetApiKey != null ? staticGetApiKey() : null;
            if (string.IsNullOrEmpty(apiKey))
            {
                this.Logger.Warn("ManageComingSoon: MakeLiveTask starting with NO Emby API key configured – " +
                    "every item in this run will fail at the library-refresh stage. Set it on the Configuration tab.");
            }

            int maxStubFileSizeMb = staticGetMaxStubFileSizeMb != null ? staticGetMaxStubFileSizeMb() : 100;
            bool unlockTags = staticGetUnlockTags != null && staticGetUnlockTags();
            this.Logger.Debug(
                "ManageComingSoon: MakeLiveTask config read – maxStubFileSizeMb={0}, unlockTags={1}.",
                maxStubFileSizeMb, unlockTags);

            this.Logger.Info("ManageComingSoon: MakeLiveTask starting – {0} item(s) to process.", items.Length);

            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < items.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.Logger.Warn(
                        "ManageComingSoon: MakeLiveTask cancelled – {0} item(s) remaining in queue were skipped.",
                        items.Length - i);

                    for (int k = i; k < items.Length; k++)
                        MakeLiveTracker.EnqueueTask(
                            items[k].FolderPath, items[k].ItemName, items[k].Year,
                            items[k].TargetPath, items[k].DeleteStub, items[k].Mode,
                            items[k].CustomStubPath);
                    break;
                }

                var item = items[i];
                double pct = (double)i / items.Length * 100.0;
                progress.Report(pct);

                string batchTag = items.Length > 1 ? string.Format("[{0}/{1}]", i + 1, items.Length) : null;
                this.Logger.Info("ManageComingSoon: MakeLiveTask processing {0} '{1}'",
                    batchTag ?? "[1/1]", item.ItemName);

                MakeLiveTracker.Register(item.FolderPath, item.ItemName, item.Year);

                if (!MakeLiveTracker.IsInFlight(item.FolderPath))
                {
                    this.Logger.Info(
                        "ManageComingSoon: MakeLiveTask {0} '{1}' — skipped (removed from queue by user).",
                        batchTag ?? "[1/1]", item.ItemName);
                    continue;
                }

                try
                {
                    progress.Report(pct + 5);

                    double sliceStart = pct;
                    double sliceSize = 100.0 / items.Length;
                    var itemProgress = new Progress<double>(p =>
                    {
                        progress.Report(sliceStart + (p / 100.0) * sliceSize);
                        MakeLiveTracker.SetPercent(item.FolderPath, (int)p);
                    });

                    Action<string> onStageMessage =
                        msg => MakeLiveTracker.SetMessage(item.FolderPath, msg);

                    var result = await this.LibraryService.MakeLivePipelineAsync(
                        item.FolderPath,
                        item.TargetPath,
                        item.DeleteStub,
                        maxStubFileSizeMb,
                        unlockTags,
                        item.Mode,
                        apiKey,
                        itemProgress,
                        cancellationToken,
                        item.CustomStubPath,
                        batchTag,
                        onStageMessage).ConfigureAwait(false);

                    if (result.Success)
                    {
                        string targetFolderPath = !string.IsNullOrEmpty(item.TargetPath)
                            ? Path.Combine(item.TargetPath, Path.GetFileName(item.FolderPath))
                            : item.FolderPath;

                        MakeLiveTracker.SetScanPending(
                            item.FolderPath, result.FinalItemId ?? Guid.Empty, targetFolderPath);
                        MakeLiveTracker.SetComplete(item.FolderPath);

                        ItemCompleted?.Invoke();
                        successCount++;
                    }
                    else
                    {
                        string failMsg = result.IsHardStop
                            ? "Not enough disk space — check source and destination"
                            : string.Format("Failed ({0}): {1}", result.FailedAtStage, result.FailureReason);

                        this.Logger.Warn(
                            "ManageComingSoon: MakeLiveTask pipeline failed for {0} '{1}'{2}: {3}",
                            batchTag ?? "[1/1]", item.ItemName,
                            result.IsHardStop ? " [HARD STOP]" : string.Format(" at stage {0}", result.FailedAtStage),
                            result.IsHardStop ? "Insufficient disk space" : result.FailureReason);

                        MakeLiveTracker.SetFailed(item.FolderPath, failMsg);
                        ItemCompleted?.Invoke();
                        failCount++;

                        if (result.IsHardStop)
                        {
                            int remaining = items.Length - i - 1;
                            if (remaining > 0)
                            {
                                this.Logger.Warn(
                                    "ManageComingSoon: MakeLiveTask hard stop — reverting {0} unstarted item(s) to Pending.",
                                    remaining);
                                for (int k = i + 1; k < items.Length; k++)
                                    MakeLiveTracker.RevertQueuedToPending(items[k].FolderPath);
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.Logger.ErrorException(
                        "ManageComingSoon: MakeLiveTask unexpected exception for {0} '{1}'",
                        ex, batchTag ?? "[1/1]", item.ItemName);
                    MakeLiveTracker.SetFailed(
                        item.FolderPath,
                        string.Format("Failed: {0} – see server log", item.ItemName));
                    ItemCompleted?.Invoke();
                    failCount++;
                }
            }

            progress.Report(100);
            this.Logger.Info(
                "ManageComingSoon: MakeLiveTask complete – {0} succeeded, {1} failed.",
                successCount, failCount);
        }

        private async Task WaitForTagRemovalAsync(string folderPath, CancellationToken cancellationToken)
        {
            const int maxAttempts = 10;
            const int delayMs = 500;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested) return;

                var items = this.LibraryService.GetComingSoonItems();
                bool stillPresent = items.Any(i =>
                    string.Equals(
                        Path.GetDirectoryName(i.Path ?? string.Empty),
                        folderPath,
                        StringComparison.OrdinalIgnoreCase));

                if (!stillPresent)
                {
                    this.Logger.Debug(
                        "ManageComingSoon: Tag removal confirmed for '{0}' after {1} poll(s).",
                        folderPath, attempt + 1);
                    return;
                }

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            this.Logger.Warn(
                "ManageComingSoon: Tag removal not confirmed for '{0}' after {1}ms — proceeding anyway.",
                folderPath, maxAttempts * delayMs);
        }
    }
}