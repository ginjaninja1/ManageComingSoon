// ManageComingSoon - Make Live Page View [Polling]
// StartPollTimer/StopPollTimer/PollProgress/BuildPollSignature.
// See MakeLivePageView.cs for the full file map.

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
        // Poll timer — 1s tick; rebuilds the movie list (including tracker rows)
        // on every tick where something actually changed, mirroring AddMoviePageView.
        // -----------------------------------------------------------------------

        private void StartPollTimer()
        {
            if (this.pollTimer != null) return;
            this.lastPolledSignature = string.Empty;
            this.pollTimer = new Timer(PollProgress, null,
                dueTime: TimeSpan.FromSeconds(1),
                period: TimeSpan.FromSeconds(1));
        }

        private void StopPollTimer()
        {
            var t = Interlocked.Exchange(ref this.pollTimer, null);
            if (t == null) return;
            // Plain Dispose() — see AddMoviePageView.Polling.cs for why
            // Dispose(WaitHandle) is deliberately avoided.
            t.Dispose();
        }

        private void PollProgress(object state)
        {
            if (this.disposed) return;
            try
            {
                var worker = GetMakeLiveWorker();
                double? taskPct = worker != null && worker.State == TaskState.Running
                    ? worker.CurrentProgress
                    : null;

                // Only push a UI refresh when something in the tracker changed
                // since the last tick — avoids the "bouncing" input-field problem
                // that AddMoviePageView's OnPollTick comment explains in detail.
                string signature = BuildPollSignature(taskPct);
                if (signature != this.lastPolledSignature)
                {
                    this.lastPolledSignature = signature;
                    // Pass taskPct into RebuildMovieList so UpdateOverallStatus
                    // can show the live percentage in the status banner.
                    RebuildMovieList(taskPct);
                    RaiseUIViewInfoChanged();
                }

                // Stop only when both the task has finished AND nothing is in-flight.
                if ((worker == null || worker.State != TaskState.Running)
                    && !MakeLiveTracker.AnyInFlight())
                    StopPollTimer();
            }
            catch (Exception)
            {
                // Swallow — defensive against shutdown races
            }
        }

        /// <summary>
        /// Cheap signature of everything PollProgress needs to detect a change in.
        /// Includes overall task percentage so progress-bar movement on the status
        /// footer triggers a push even when no individual tracker row state changed.
        /// Includes Message so that SetMessage() stage-text updates cause a push.
        /// </summary>
        private static string BuildPollSignature(double? taskPct)
        {
            var sb = new StringBuilder();
            sb.Append(taskPct.HasValue ? ((int)taskPct.Value).ToString() : "-").Append('|');
            foreach (var e in MakeLiveTracker.GetActive())
                sb.Append(e.FolderPath).Append(':')
                  .Append((int)e.State).Append(':')
                  .Append(e.Percent).Append(':')
                  .Append(e.Message ?? string.Empty).Append('|');
            foreach (var e in MakeLiveTracker.GetHistory())
                sb.Append(e.FolderPath).Append(':').Append((int)e.State).Append('|');
            return sb.ToString();
        }
    }
}