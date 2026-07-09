// ManageComingSoon - Make Live Page View [Rebuild]
// RebuildMovieList — three sections built from MakeLiveTracker (the single
// source of truth) and swapped atomically onto ui.MovieList.
// UpdateOverallStatus/SetOverallStatus for the status footer.
// See MakeLivePageView.cs for the full file map.
//
// Row sections:
//   1. Make All Live bulk-action row — always present.
//   2. Pending rows — one per MakeLiveState.Pending entry, sorted alphabetically.
//   3. Queued rows — items handed to the task but not yet Moving.
//   4. Active rows — Moving / ScanPending entries, in start order.
//   5. History divider + history rows (Complete/Failed), newest first.

namespace ManageComingSoon.UI.MakeLive
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using ManageComingSoon.Services;
    using ManageComingSoon.UI.Configuration;
    using ManageComingSoon.UIBaseClasses.Views;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal partial class MakeLivePageView : PluginPageView, IDisposable
    {
        // -----------------------------------------------------------------------
        // RebuildMovieList
        // Built into a fresh list and swapped atomically to avoid an
        // ArgumentOutOfRangeException when the request thread is mid-serialisation
        // of ui.MovieList at the same moment this runs on the background poll thread.
        //
        // taskPct: the overall task progress percentage from ITaskManager, or null
        // when called outside a poll tick (no percentage available). Passed straight
        // through to UpdateOverallStatus so the status banner can show "X%" during
        // a run. Previously this was set by PollProgress before calling
        // RebuildMovieList and then overwritten by UpdateOverallStatus(null) inside
        // it — that bug meant the percentage was never actually visible.
        // -----------------------------------------------------------------------

        private void RebuildMovieList(double? taskPct = null)
        {
            var ui = UI;
            var newList = new GenericItemList();

            var queued = MakeLiveTracker.GetAllQueued();
            var active = MakeLiveTracker.GetActive();
            bool isRunning = queued.Length > 0
                          || active.Length > 0
                          || MakeLiveTracker.IsCurrentRunActive
                          || MakeLiveTracker.AnyInFlight();

            // ---- 1. Make All Live row — always visible -------------------------
            int selectedCount = MakeLiveTracker.GetAllPending().Count(e => e.ToggledOn);

            string makeAllPrimary;
            if (isRunning)
            {
                var parts = new List<string>();
                if (active.Length > 0)
                    parts.Add(string.Format("{0} processing", active.Length));
                if (queued.Length > 0)
                    parts.Add(string.Format("{0} queued", queued.Length));
                makeAllPrimary = parts.Count > 0
                    ? string.Format("Make All Live  \u2014  {0}", string.Join(", ", parts))
                    : "Make All Live";
            }
            else
            {
                makeAllPrimary = selectedCount > 0
                    ? string.Format("Make All Live  ({0} selected)", selectedCount)
                    : "Make All Live";
            }

            var makeAllRow = new GenericListItem(
                isRunning ? IconNames.hourglass_empty : IconNames.add_circle,
                makeAllPrimary,
                string.Empty)
            {
                IconMode = ItemListIconMode.SmallRegular,
                Status = isRunning ? ItemStatus.InProgress : ItemStatus.Unavailable,
                HasPercentage = false,
                Button1 = new ButtonItem(isRunning ? "Processing\u2026" : "Make All Live")
                {
                    Icon = isRunning ? IconNames.hourglass_empty : IconNames.add_circle,
                    Data1 = "MakeAllLive",
                    CommandId = "MakeAllLive",
                    IsEnabled = !isRunning && selectedCount > 0,
                },
            };
            newList.Add(makeAllRow);

            // ---- 2. Pending (Coming Soon) rows — alphabetical -----------------
            foreach (var entry in MakeLiveTracker.GetAllPending())
                newList.Add(BuildMovieRow(entry, isRunning));

            // ---- 3. Queued rows — between Pending and Active ------------------
            foreach (var entry in queued)
                newList.Add(BuildQueuedRow(entry));

            // ---- 4. Active (Moving / ScanPending) rows — chronological --------
            foreach (var entry in active)
                newList.Add(BuildActiveTrackerRow(entry));

            // ---- 5. History divider + history rows ----------------------------
            var history = MakeLiveTracker.GetHistory();
            if (history.Length > 0)
            {
                newList.Add(new GenericListItem(
                    IconNames.done_all,
                    "Completed",
                    string.Empty)
                {
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Unavailable,
                    HasPercentage = false,
                    Button1 = new ButtonItem("Clear Completed")
                    {
                        StandardIcon = StandardIcons.Remove,
                        Data1 = "ClearCompleted",
                        CommandId = "ClearCompleted",
                        IsEnabled = true,
                    },
                });

                foreach (var entry in history)
                    newList.Add(BuildHistoryRow(entry));
            }

            ui.MovieList = newList;
            UpdateOverallStatus(taskPct);
        }

        // -----------------------------------------------------------------------
        // Overall status footer
        // -----------------------------------------------------------------------

        private void UpdateOverallStatus(double? taskPct)
        {
            var queued = MakeLiveTracker.GetAllQueued();
            var active = MakeLiveTracker.GetActive();

            if (queued.Length > 0 || active.Length > 0)
            {
                string pctStr = taskPct.HasValue
                    ? string.Format(" ({0}%)", (int)taskPct.Value)
                    : string.Empty;

                var statusParts = new List<string>();
                if (active.Length > 0)
                    statusParts.Add(string.Format("{0} in progress", active.Length));
                if (queued.Length > 0)
                    statusParts.Add(string.Format("{0} queued", queued.Length));

                SetOverallStatus(
                    string.Format("Make Live running{0} \u2014 {1}.", pctStr, string.Join(", ", statusParts)),
                    ItemStatus.InProgress);
                return;
            }

            var pending = MakeLiveTracker.GetAllPending();
            if (pending.Length == 0 && MakeLiveTracker.GetHistory().Length == 0)
            {
                SetOverallStatus(
                    "No Coming Soon movies found. Use the 'Add Coming Soon' tab to add some.",
                    ItemStatus.Unavailable);
                return;
            }

            int ready = 0, blocked = 0;
            foreach (var e in pending)
            {
                if (e.Analysis != null && e.Analysis.IsSafeToProceed)
                    ready++;
                else
                    blocked++;
            }
            int selected = pending.Count(e => e.ToggledOn);

            var parts = new List<string>();
            if (pending.Length > 0)
                parts.Add(string.Format("{0} movie(s)", pending.Length));
            if (ready > 0)
                parts.Add(string.Format("{0} ready", ready));
            if (blocked > 0)
                parts.Add(string.Format("{0} blocked", blocked));
            if (selected > 0)
                parts.Add(string.Format("{0} selected", selected));

            SetOverallStatus(
                string.Join("  /  ", parts),
                blocked > 0 ? ItemStatus.Warning : ItemStatus.Succeeded);
        }

        private void SetOverallStatus(string message, ItemStatus status)
        {
            UI.OverallStatus.StatusText = message;
            UI.OverallStatus.Status = status;
        }
    }
}