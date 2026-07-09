// ManageComingSoon - Make Live Page View [ROOT]
// Moves "Coming Soon" entries from staging to the live library.
// GenericItemList is the entire UI surface.
//
// This class is split across partial-class files by concern:
//
//   MakeLivePageView.cs            - THIS FILE. Class skeleton: constants,
//                                     fields, ctor, Dispose, UI accessor,
//                                     ITaskManager event handlers, and
//                                     MakeLiveTask.ItemCompleted subscription.
//
//   MakeLivePageView.Commands.cs   - RunCommand dispatch table and every
//                                     Handle*/LoadAndAnalyse/AnalyseOne/
//                                     GetMakeLiveWorker method.
//
//   MakeLivePageView.Polling.cs    - StartPollTimer/StopPollTimer/PollProgress/
//                                     BuildPollSignature.
//
//   MakeLivePageView.Rebuild.cs    - RebuildMovieList and the overall status
//                                     footer (UpdateOverallStatus/SetOverallStatus).
//
//   MakeLivePageView.RowBuilders.cs - BuildActiveTrackerRow, BuildQueuedRow,
//                                     BuildHistoryRow, BuildMovieRow, and all
//                                     state -> (icon/status/text) helpers.
//
// Poll timer runs while the MakeLiveTask is executing OR tracker rows are
// in-flight. Default row icon: video_library (see StateToIcon in RowBuilders).

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
    using MediaBrowser.Model.Events;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Tasks;

    internal partial class MakeLivePageView : PluginPageView, IDisposable
    {
        // -----------------------------------------------------------------------
        // Single source of truth for which mode is currently "production".
        // To swap, change this one line. Config UI will expose the choice later.
        // -----------------------------------------------------------------------
        private const EmbyLibraryMakeService.MakeLiveMode LiveMakeLiveMode =
            EmbyLibraryMakeService.MakeLiveMode.Advanced; // Mode C (Isolation)

        private readonly ManageComingSoonPlugin plugin;
        private readonly EmbyLibraryMakeService libraryService;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;

        private Timer pollTimer;
        private bool disposed;

        // Cancelled on Dispose() to stop all background Task.Run work cleanly
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        private readonly MigrationAnalyzer analyzer;

        // Signature of all tracker entries (State, Percent, Message) as of the
        // last UI push from PollProgress — identical to AddMoviePageView's pattern
        // for avoiding unnecessary pushes that would stomp in-progress UI state.
        private string lastPolledSignature = string.Empty;

        public MakeLivePageView(
            PluginInfo pluginInfo,
            ManageComingSoonPlugin plugin,
            EmbyLibraryMakeService libraryService,
            ITaskManager taskManager,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.plugin = plugin;
            this.libraryService = libraryService;
            this.taskManager = taskManager;
            this.logger = logger;
            this.analyzer = new MigrationAnalyzer(logger);

            this.ContentData = new MakeLiveUI();
            this.ShowSave = false;

            this.taskManager.TaskExecuting += OnTaskExecuting;
            this.taskManager.TaskCompleted += OnTaskCompleted;

            MakeLiveTask.ItemCompleted += OnMakeLiveItemCompleted;

            LoadAndAnalyse();
            RebuildMovieList();

            var runningWorker = GetMakeLiveWorker();
            if ((runningWorker != null && runningWorker.State == TaskState.Running)
                || MakeLiveTracker.AnyInFlight())
            {
                StartPollTimer();
            }
        }

        // -----------------------------------------------------------------------
        // IDisposable
        // -----------------------------------------------------------------------

        public void Dispose()
        {
            if (this.disposed) return;
            this.disposed = true;
            this.cts.Cancel();
            this.cts.Dispose();
            MakeLiveTask.ItemCompleted -= OnMakeLiveItemCompleted;
            StopPollTimer();
            this.taskManager.TaskExecuting -= OnTaskExecuting;
            this.taskManager.TaskCompleted -= OnTaskCompleted;
        }

        private MakeLiveUI UI => (MakeLiveUI)this.ContentData;

        // -----------------------------------------------------------------------
        // MakeLiveTask per-item completion callback
        // -----------------------------------------------------------------------

        private void OnMakeLiveItemCompleted()
        {
            if (this.disposed) return;
            RebuildMovieList();
            RaiseUIViewInfoChanged();
        }

        // -----------------------------------------------------------------------
        // ITaskManager event handlers
        // -----------------------------------------------------------------------

        private void OnTaskExecuting(object sender, GenericEventArgs<IScheduledTaskWorker> e)
        {
            if (this.disposed) return;
            if (!(e.Argument.ScheduledTask is MakeLiveTask)) return;
            StartPollTimer();
        }

        private void OnTaskCompleted(object sender, TaskCompletionEventArgs e)
        {
            if (this.disposed) return;
            if (!(e.Task.ScheduledTask is MakeLiveTask)) return;

            StopPollTimer();

            bool succeeded = e.Result.Status == TaskCompletionStatus.Completed;
            SetOverallStatus(
                succeeded
                    ? "Make Live completed successfully."
                    : string.Format("Make Live finished with status: {0}", e.Result.Status),
                succeeded ? ItemStatus.Succeeded : ItemStatus.Failed);

            MakeLiveTracker.ClearCurrentRun();
            LoadAndAnalyse();
            RebuildMovieList();
            RaiseUIViewInfoChanged();
        }
    }
}