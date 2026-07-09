// ManageComingSoon - Add Movie Page View [ROOT]
// Multi-movie search page. GenericItemList is the entire UI surface.
//
// This class is split across partial-class files by concern:
//
//   AddMoviePageView.cs            - THIS FILE. Class skeleton: constants,
//                                     fields, ctor, Dispose, UI accessor,
//                                     ITaskManager event handlers, and
//                                     AddMovieTask event subscriptions.
//
//   AddMoviePageView.Commands.cs   - RunCommand dispatch table and every
//                                     Handle*/RunSearchAsync/FetchCastAsync
//                                     method it routes to.
//
//   AddMoviePageView.Conflict.cs   - CheckDestinationForEntry: point-in-time
//                                     destination check called at confirmation
//                                     and on toggle-on. No polling.
//
//   AddMoviePageView.Rebuild.cs    - RefreshAddButtonState, RebuildMovieList,
//                                     and the overall status footer
//                                     (UpdateOverallStatus/SetOverallStatus).
//
//   AddMoviePageView.RowBuilders.cs - BuildMovieRow and all the small
//                                     State → (icon/text/percent/button)
//                                     mapping helpers.
//
// UI refresh model — event-driven, with coalesced broadcasts:
//   AddMovieTracker.[UI] methods fire OnStateChanged after every mutation.
//   AddMovieTask.StateChanged relays this to OnTrackerStateChanged here,
//   which requests a UI refresh. Multiple fast mutations collapse into one
//   ContentData broadcast, so the Emby client receives fewer transient list
//   shapes while still rendering the latest tracker state.
//   OnTaskCompleted handles task-level status footer updates separately.

namespace ManageComingSoon.UI.AddMovie
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
    using ManageComingSoon.Model;
    using ManageComingSoon.Services;
    using ManageComingSoon.UI.Configuration;
    using ManageComingSoon.UIBaseClasses.Views;
    using MediaBrowser.Model.Events;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Tasks;

    internal partial class AddMoviePageView : PluginPageView, IDisposable
    {
        private const int MaxDefaultCandidates = 3;
        private const int MaxExpandedCandidates = 10;
        private const int UiRefreshCoalesceMs = 100;

        private readonly ManageComingSoonPlugin plugin;
        private readonly TmdbService tmdbService;
        private readonly EmbyLibraryAddService libraryService;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;

        // Diagnostic instance identity — see RebuildMovieList's row-count log
        // line. If more than one instanceId ever appears live in the server
        // log at once (constructed but not yet disposed), that confirms
        // multiple AddMoviePageView instances are broadcasting independently,
        // which can arrive at the client out of order and look exactly like
        // rows flickering/reappearing even though each broadcast individually
        // carries correct data.
        private readonly Guid instanceId = Guid.NewGuid();
        private int broadcastSeq;

        private bool disposed;

        // Lock that serialises every actual rebuild/broadcast — ensures that
        // reading AddMovieTracker state, building ContentData, and calling
        // RaiseUIViewInfoChanged() are never interleaved with each other across
        // the tracker StateChanged thread and any command-handler thread that
        // also requests a UI refresh. Without this, a background-task mutation
        // arriving mid-rebuild can produce an inconsistent ContentData snapshot
        // that confuses the client renderer.
        private readonly object rebuildLock = new object();
        private readonly object uiRefreshLock = new object();
        private Timer uiRefreshTimer;
        private bool uiRefreshScheduled;
        private bool preserveStatusOnNextRefresh;

        // Cancelled on Dispose() to stop all background Task.Run work (searches) cleanly
        private readonly CancellationTokenSource cts = new CancellationTokenSource();

        // Transient UI state (resets on page reload)
        private readonly HashSet<string> expandedCandidates =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> expandedInfo =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public AddMoviePageView(
            PluginInfo pluginInfo,
            ManageComingSoonPlugin plugin,
            TmdbService tmdbService,
            EmbyLibraryAddService libraryService,
            ILogger logger,
            ITaskManager taskManager)
            : base(pluginInfo.Id)
        {
            this.plugin = plugin;
            this.tmdbService = tmdbService;
            this.libraryService = libraryService;
            this.taskManager = taskManager;
            this.logger = logger;

            this.ContentData = new AddMovieUI();
            this.ShowSave = false;

            this.taskManager.TaskExecuting += OnTaskExecuting;
            this.taskManager.TaskCompleted += OnTaskCompleted;

            // Single subscription — AddMovieTracker fires OnStateChanged from every
            // [UI] mutation method; AddMovieTask.StateChanged relays it here.
            // No poll timer, no pairing of tracker calls with event raises.
            AddMovieTask.StateChanged += OnTrackerStateChanged;

            RefreshAddButtonState();
            // Initial build — no broadcast needed (no client connected yet).
            // The tracker is the single point of truth: page navigation does
            // not alter its state. Whatever the tracker holds is rendered as-is.
            RebuildMovieList();
            UpdateOverallStatus();

            this.logger.Info(
                "MCS-DIAG instance={0} event=constructed",
                this.instanceId);
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
            lock (this.uiRefreshLock)
            {
                if (this.uiRefreshTimer != null)
                {
                    this.uiRefreshTimer.Dispose();
                    this.uiRefreshTimer = null;
                }
                this.uiRefreshScheduled = false;
                this.preserveStatusOnNextRefresh = false;
            }
            AddMovieTask.StateChanged -= OnTrackerStateChanged;
            this.taskManager.TaskExecuting -= OnTaskExecuting;
            this.taskManager.TaskCompleted -= OnTaskCompleted;

            this.logger.Info(
                "MCS-DIAG instance={0} event=disposed",
                this.instanceId);
        }

        private AddMovieUI UI => (AddMovieUI)this.ContentData;

        // -----------------------------------------------------------------------
        // RequestUiRefresh — single choke-point for all UI refresh requests.
        //
        // Tracker mutations are intentionally noisy and immediate; broadcasts
        // are coalesced here so a burst of valid intermediate states becomes
        // one ContentData push containing the latest tracker snapshot.
        // -----------------------------------------------------------------------

        private void RequestUiRefresh(bool preserveStatus = false)
        {
            if (this.disposed) return;
            lock (this.uiRefreshLock)
            {
                if (this.disposed) return;

                this.preserveStatusOnNextRefresh |= preserveStatus;
                if (this.uiRefreshScheduled) return;

                this.uiRefreshScheduled = true;
                if (this.uiRefreshTimer == null)
                    this.uiRefreshTimer = new Timer(OnUiRefreshTimer, null,
                        Timeout.Infinite, Timeout.Infinite);

                this.uiRefreshTimer.Change(UiRefreshCoalesceMs, Timeout.Infinite);
            }
        }

        private void OnUiRefreshTimer(object state)
        {
            bool preserveStatus;
            lock (this.uiRefreshLock)
            {
                if (this.disposed) return;
                this.uiRefreshScheduled = false;
                preserveStatus = this.preserveStatusOnNextRefresh;
                this.preserveStatusOnNextRefresh = false;
            }

            RebuildAndBroadcastNow(preserveStatus);
        }

        // -----------------------------------------------------------------------
        // RebuildAndBroadcastNow — actual serialized ContentData build/send.
        // -----------------------------------------------------------------------

        private void RebuildAndBroadcastNow(bool preserveStatus)
        {
            if (this.disposed) return;
            lock (this.rebuildLock)
            {
                if (this.disposed) return;
                RebuildMovieList();
                if (!preserveStatus)
                    UpdateOverallStatus();
                RaiseUIViewInfoChanged();
            }
        }

        // -----------------------------------------------------------------------
        // Tracker state-change handler — called whenever AddMovieTracker fires
        // OnStateChanged via AddMovieTask.StateChanged. Covers all [UI] mutations:
        // search results, queue transitions, pipeline progress, completion, failure.
        // -----------------------------------------------------------------------

        private void OnTrackerStateChanged()
        {
            if (this.disposed) return;
            RequestUiRefresh();
        }

        // -----------------------------------------------------------------------
        // ITaskManager event handlers
        // -----------------------------------------------------------------------

        private void OnTaskExecuting(object sender,
            GenericEventArgs<IScheduledTaskWorker> e)
        {
            // No action needed — AddMovieTask.ItemStarted fires immediately when
            // the first item begins, which triggers the first requested refresh.
            // Keeping this handler for future use or logging if needed.
            if (this.disposed) return;
            if (!(e.Argument.ScheduledTask is AddMovieTask)) return;
        }

        private void OnTaskCompleted(object sender, TaskCompletionEventArgs e)
        {
            if (this.disposed) return;
            if (!(e.Task.ScheduledTask is AddMovieTask)) return;

            bool succeeded = e.Result.Status == TaskCompletionStatus.Completed;
            SetOverallStatus(
                succeeded
                    ? "Add Coming Soon completed successfully."
                    : string.Format("Add Coming Soon finished with status: {0}", e.Result.Status),
                succeeded ? ItemStatus.Succeeded : ItemStatus.Failed);

            RequestUiRefresh(preserveStatus: true);
        }

        // -----------------------------------------------------------------------
        // Task worker lookup
        // -----------------------------------------------------------------------

        private IScheduledTaskWorker GetAddMovieWorker()
        {
            return this.taskManager.ScheduledTasks
                .FirstOrDefault(t => t.ScheduledTask is AddMovieTask);
        }
    }
}
