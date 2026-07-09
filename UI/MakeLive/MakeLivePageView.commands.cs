// ManageComingSoon - Make Live Page View [Commands]
// RunCommand (the command dispatch table) and every Handle*/LoadAndAnalyse/
// AnalyseOne/GetMakeLiveWorker method it routes to.
// See MakeLivePageView.cs for the full file map.
//
// Toggle behaviour:
//   • LoadAndAnalyse auto-enables newly detected ready entries (default ON).
//   • Entries that fail analysis are auto-disabled regardless of prior state.
//   • Entries that pass re-analysis are auto-re-enabled UNLESS the user has
//     explicitly deselected them (ManuallyToggledOff).
//   • HandleToggle records explicit deselections in ManuallyToggledOff so
//     LoadAndAnalyse can respect the user's intent on subsequent rebuilds.
//   • RefreshList and task completion clear both sets (fresh slate).
//
// REFACTOR NOTE: LoadAndAnalyse, HandleToggle, ReanalyseRow, and
// HandleMakeLiveAsync now read from / write to MakeLiveTracker Pending entries
// instead of a local rows list.  MakeLiveRow is gone.
//
// DequeueOne_ (added with RowBuilders queued-row Remove button):
//   Reverts a Queued entry back to Pending before MakeLiveTask.Register()
//   picks it up.  Guard: IsInFlight() check rejects stale commands fired
//   after the task has already consumed the entry.  Requires
//   MakeLiveTracker.SetPending — see HandleDequeueOne for full contract.

namespace ManageComingSoon.UI.MakeLive
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using ManageComingSoon.Services;
    using ManageComingSoon.UI.Configuration;
    using ManageComingSoon.UIBaseClasses.Views;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Tasks;

    internal partial class MakeLivePageView : PluginPageView, IDisposable
    {
        // -----------------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------------

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (commandId == "RefreshList")
            {
                // ClearAllPending + re-upsert resets toggle state to a clean slate.
                LoadAndAnalyse();
                RebuildMovieList();
                RaiseUIViewInfoChanged();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "ClearCompleted")
            {
                MakeLiveTracker.ClearCompleted();
                RebuildMovieList();
                RaiseUIViewInfoChanged();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Toggle_", StringComparison.Ordinal))
            {
                HandleToggle(commandId.Substring(7));
                RaiseUIViewInfoChanged();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("MakeLiveOne_", StringComparison.Ordinal))
            {
                // Guard: ignore if a run is already in progress. The button should
                // be disabled in this state, but a fast double-click or stale UI
                // could still deliver the command.
                if (IsRunActive())
                {
                    this.logger.Warn("ManageComingSoon: MakeLiveOne ignored — a run is already in progress.");
                    return Task.FromResult<IPluginUIView>(this);
                }

                string folderPath = commandId.Substring(12);
                var _a = Task.Run(() => HandleMakeLiveAsync(new[] { folderPath }), this.cts.Token);
                StartPollTimer();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "MakeAllLive")
            {
                // Guard: ignore if a run is already in progress.
                if (IsRunActive())
                {
                    this.logger.Warn("ManageComingSoon: MakeAllLive ignored — a run is already in progress.");
                    return Task.FromResult<IPluginUIView>(this);
                }

                var selected = MakeLiveTracker.GetAllPending()
                    .Where(e => e.ToggledOn)
                    .Select(e => e.FolderPath)
                    .ToArray();
                var _b = Task.Run(() => HandleMakeLiveAsync(selected), this.cts.Token);
                StartPollTimer();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("DequeueOne_", StringComparison.Ordinal))
            {
                HandleDequeueOne(commandId.Substring(11));
                RaiseUIViewInfoChanged();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Retry_", StringComparison.Ordinal))
            {
                string folderPath = commandId.Substring(6);
                ReanalyseRow(folderPath);
                RebuildMovieList();
                RaiseUIViewInfoChanged();
                return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        // -----------------------------------------------------------------------
        // Toggle — explicit user interaction
        //
        // Turning OFF  → records ManuallyToggledOff so LoadAndAnalyse won't
        //                auto-re-enable on the next rebuild.
        // Turning ON   → clears ManuallyToggledOff; re-analyses first; only
        //                enables if fresh analysis passes.
        // -----------------------------------------------------------------------

        private void HandleToggle(string folderPath)
        {
            var entry = MakeLiveTracker.GetPending(folderPath);
            if (entry == null) return;

            if (entry.ToggledOn)
            {
                MakeLiveTracker.SetPendingToggle(folderPath, toggledOn: false, manuallyToggledOff: true);
                RebuildMovieList();
                return;
            }

            MakeLiveTracker.SetPendingToggle(folderPath, toggledOn: false, manuallyToggledOff: false);
            ReanalyseRow(folderPath);

            entry = MakeLiveTracker.GetPending(folderPath);
            if (entry != null && entry.Analysis != null && entry.Analysis.IsSafeToProceed)
                MakeLiveTracker.SetPendingToggle(folderPath, toggledOn: true, manuallyToggledOff: false);

            RebuildMovieList();
        }

        private void ReanalyseRow(string folderPath)
        {
            var entry = MakeLiveTracker.GetPending(folderPath);
            if (entry == null) return;
            MakeLiveTracker.SetPendingAnalysis(folderPath, AnalyseOne(folderPath));
        }

        // -----------------------------------------------------------------------
        // Dequeue — remove a Queued-but-not-yet-started entry from the pipeline,
        // reverting it to Pending so it reappears as a normal Coming Soon row.
        //
        // Guard: if MakeLiveTask.Register() has already consumed this entry
        // (state = Moving or ScanPending) the command is ignored. The Remove
        // button is only rendered on Queued rows, but a stale push or fast
        // double-click could still deliver the command after the transition.
        //
        // MakeLiveTracker.SetPending contract:
        //   1. Verify the entry is still Queued; no-op if not.
        //   2. Remove from TaskQueue (reverses EnqueueTask).
        //   3. Revert tracker entry to Pending, preserving ItemName, Year,
        //      Analysis, ToggledOn, and ManuallyToggledOff.
        // -----------------------------------------------------------------------

        private void HandleDequeueOne(string folderPath)
        {
            if (MakeLiveTracker.IsInFlight(folderPath))
            {
                this.logger.Warn(
                    "ManageComingSoon: DequeueOne ignored for '{0}' — entry is already in-flight.",
                    folderPath);
                return;
            }

            MakeLiveTracker.SetPending(folderPath);
            RebuildMovieList();
        }

        // -----------------------------------------------------------------------
        // Load Coming Soon items from Emby + run analysis; upsert into tracker
        // as Pending entries. MakeLiveTracker becomes the single source of truth.
        //
        // Guard: skipped during an active task run. Querying Emby mid-run is
        // unsafe (stale index) and unnecessary — the tracker already reflects
        // real pipeline state via Register/SetComplete/SetFailed.
        //
        // Auto-toggle rules applied after Pending entries are upserted:
        //   • New entry (not manually toggled off) and ready → auto-enable.
        //   • Entry currently enabled whose analysis now fails → auto-disable.
        //   • Entry previously auto-disabled that is now ready, and NOT
        //     ManuallyToggledOff → auto-re-enable.
        // -----------------------------------------------------------------------

        private void LoadAndAnalyse()
        {
            if (MakeLiveTracker.IsCurrentRunActive) return;

            MakeLiveTracker.ClearAllPending();
            try
            {
                var cfg = this.plugin.Configuration;
                string targetPath = cfg.MakeLiveMoveToNewLocation
                    ? ConfigurationPageView.PathFromKey(cfg.MakeLiveTargetKey)
                    : null;

                var items = this.libraryService.GetComingSoonItems();

                foreach (var item in items)
                {
                    string filePath = item.Path ?? string.Empty;
                    string folderPath = !string.IsNullOrEmpty(filePath)
                        ? Path.GetDirectoryName(filePath) ?? filePath
                        : string.Empty;
                    if (string.IsNullOrEmpty(folderPath)) continue;

                    MakeLiveTracker.UpsertPending(
                        folderPath,
                        item.Name,
                        item.ProductionYear ?? 0,
                        AnalyseOne(folderPath, targetPath));
                }

                foreach (var entry in MakeLiveTracker.GetAllPending())
                {
                    bool isReady = entry.Analysis != null && entry.Analysis.IsSafeToProceed;

                    if (isReady)
                    {
                        if (!entry.ToggledOn && !entry.ManuallyToggledOff)
                            MakeLiveTracker.SetPendingToggle(entry.FolderPath, toggledOn: true, manuallyToggledOff: false);
                    }
                    else
                    {
                        if (entry.ToggledOn)
                            MakeLiveTracker.SetPendingToggle(entry.FolderPath, toggledOn: false, manuallyToggledOff: false);
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: Failed to load coming soon list", ex);
            }
        }

        private MigrationAnalysisResult AnalyseOne(string folderPath)
        {
            var cfg = this.plugin.Configuration;
            string targetPath = cfg.MakeLiveMoveToNewLocation
                ? ConfigurationPageView.PathFromKey(cfg.MakeLiveTargetKey)
                : null;
            return AnalyseOne(folderPath, targetPath);
        }

        private MigrationAnalysisResult AnalyseOne(string folderPath, string targetPath)
        {
            if (!string.IsNullOrEmpty(targetPath))
                return this.analyzer.Analyze(folderPath, targetPath);

            var result = this.analyzer.Analyze(folderPath, folderPath);
            result.DestinationDirectory = "(same location – no move)";
            result.WorstCaseScenarioText =
                "No folder move. Only the stub video will be deleted (if configured). " +
                "The Coming Soon tag will be removed. This is low risk.";
            return result;
        }

        // -----------------------------------------------------------------------
        // Make Live — single row or bulk; always LiveMakeLiveMode
        // -----------------------------------------------------------------------

        private async Task HandleMakeLiveAsync(string[] folderPaths)
        {
            if (folderPaths == null || folderPaths.Length == 0) return;

            var cfg = this.plugin.Configuration;
            string targetPath = cfg.MakeLiveMoveToNewLocation
                ? ConfigurationPageView.PathFromKey(cfg.MakeLiveTargetKey)
                : null;
            string customStubPath = null;

            var goodPaths = new List<string>();
            var blockedPaths = new List<string>();

            foreach (var folderPath in folderPaths)
            {
                var fresh = AnalyseOne(folderPath, targetPath);
                MakeLiveTracker.SetPendingAnalysis(folderPath, fresh);

                if (fresh.IsSafeToProceed)
                    goodPaths.Add(folderPath);
                else
                {
                    blockedPaths.Add(folderPath);
                    MakeLiveTracker.SetPendingToggle(folderPath, toggledOn: false, manuallyToggledOff: false);
                }
            }

            if (blockedPaths.Count > 0)
                this.logger.Warn("ManageComingSoon: Make Live gate blocked {0} item(s).", blockedPaths.Count);

            if (goodPaths.Count == 0)
            {
                SetOverallStatus(
                    "Nothing to make live — all selected items failed the final check. See row status for details.",
                    ItemStatus.Failed);
                RebuildMovieList();
                RaiseUIViewInfoChanged();
                return;
            }

            foreach (var folderPath in goodPaths)
            {
                var entry = MakeLiveTracker.GetPending(folderPath);

                // Transition to Queued before EnqueueTask so the UI immediately
                // shows "waiting" rows even before MakeLiveTask calls Register().
                // Pass targetPath so the primary line can show Source → Dest from
                // the moment the row enters the queue.
                MakeLiveTracker.SetQueued(folderPath, targetPath);

                MakeLiveTracker.EnqueueTask(
                    folderPath,
                    entry != null ? entry.ItemName : Path.GetFileName(folderPath),
                    entry != null ? entry.Year : 0,
                    targetPath,
                    cfg.MakeLiveDeleteStubFile,
                    LiveMakeLiveMode,
                    customStubPath);
            }

            string statusMsg = blockedPaths.Count > 0
                ? string.Format("Queued {0} movie(s); {1} blocked — see row status.",
                    goodPaths.Count, blockedPaths.Count)
                : string.Format("Queued {0} movie(s) for processing...", goodPaths.Count);

            SetOverallStatus(statusMsg, ItemStatus.InProgress);
            RebuildMovieList();
            RaiseUIViewInfoChanged();

            try
            {
                var worker = GetMakeLiveWorker();
                if (worker != null)
                {
                    var _ = this.taskManager.Execute(worker, new TaskOptions());
                    this.logger.Info("ManageComingSoon: MakeLiveTask triggered via ITaskManager.");
                }
                else
                {
                    this.logger.Warn("ManageComingSoon: Could not find MakeLiveTask in ITaskManager.");
                    SetOverallStatus("Failed to start make-live task – see server log.", ItemStatus.Failed);
                    RaiseUIViewInfoChanged();
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: Failed to trigger MakeLiveTask", ex);
                SetOverallStatus("Failed to start make-live task – see server log.", ItemStatus.Failed);
                RaiseUIViewInfoChanged();
            }
        }

        // -----------------------------------------------------------------------
        // Scheduler helpers
        // -----------------------------------------------------------------------

        private IScheduledTaskWorker GetMakeLiveWorker()
        {
            foreach (var t in this.taskManager.ScheduledTasks)
                if (t.ScheduledTask is MakeLiveTask)
                    return t;
            return null;
        }

        /// <summary>
        /// True when a make-live run is in progress or items are waiting in the queue.
        /// Covers three timing phases:
        ///   • Queued   — items handed to EnqueueTask, task not yet started.
        ///   • Active   — Moving or ScanPending entries in the tracker.
        ///   • Draining — task worker still shows Running after the last item.
        /// Used to guard command entry points and lock row UI during a run.
        /// </summary>
        private bool IsRunActive()
        {
            if (MakeLiveTracker.GetAllQueued().Length > 0) return true;
            if (MakeLiveTracker.AnyInFlight()) return true;
            var worker = GetMakeLiveWorker();
            return worker != null && worker.State == TaskState.Running;
        }
    }
}