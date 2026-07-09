// ManageComingSoon - Add Movie Page View [Rebuild]
// RefreshAddButtonState + RebuildMovieList (builds fresh UI composition from
// AddMovieTracker state on every push) and the overall status footer.
// See AddMoviePageView.cs for the full file map.
//
// Design principle: RebuildMovieList is a pure function from tracker state
// to UI composition. It builds fresh GenericItemList and GenericListItem
// instances on every call — no caching, no reuse, no memory of previous
// state. Both MovieList (active rows) and CompletedList (completed rows)
// are assigned fresh instances before every broadcast so the client always
// receives a clean composition with no inherited rendering state.
//
// DestinationConflict change: there is no longer a DestinationConflict state.
// Conflict is an annotation (HasDestinationConflict) on a Confident entry.
// UpdateOverallStatus therefore has a single "conflict" counter that reads
// entry.IsAddBlocked across Confident entries — no separate state to count.
//
// AnyConfident renamed to AnyNeedingPoll in AddMovieTracker, updated here.

namespace ManageComingSoon.UI.AddMovie
{
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using ManageComingSoon.Model;
    using ManageComingSoon.Services;
    using ManageComingSoon.Storage;
    using ManageComingSoon.UI.Configuration;
    using ManageComingSoon.UIBaseClasses.Views;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    internal partial class AddMoviePageView : PluginPageView, IDisposable
    {
        // -----------------------------------------------------------------------
        // Add button state — disabled with guidance when no TMDB key is configured
        // -----------------------------------------------------------------------

        private void RefreshAddButtonState()
        {
            bool hasKey = !string.IsNullOrWhiteSpace(this.plugin.Configuration.TmdbApiKey);
            UI.AddToListButton.Caption = hasKey
                ? "Add via Provider Match"
                : "Register for and add a TMDB API key first";
            UI.AddToListButton.IsEnabled = hasKey;
            UI.AddManualButton.Caption = "Add Manual";
            UI.AddManualButton.IsEnabled = true;
        }

        // -----------------------------------------------------------------------
        // RebuildMovieList
        //
        // Builds two fresh GenericItemLists from current tracker state:
        //
        //   MovieList:     "Add All" header + active rows (non-Added, newest first)
        //   CompletedList: "Completed" header + completed rows (most-recently-
        //                  completed first)
        //
        // Two separate lists rather than one combined list because the Emby
        // web client's list renderer (genericedit.js renderItemListItems) diffs
        // by array index position, not by item identity. A single combined list
        // causes items shifting position when moving from active to completed to
        // inherit stale DOM state (e.g. a stuck spinner) from whatever was
        // previously at that index. Two lists each only ever grow or shrink at
        // one end — no mid-list identity changes.
        // -----------------------------------------------------------------------

        private void RebuildMovieList()
        {
            var ui = UI;
            var entries = AddMovieTracker.GetAllSorted();
            var active = entries.Where(e => e.State != AddMovieState.Added).ToArray();
            var completed = entries.Where(e => e.State == AddMovieState.Added).ToArray();

            // ---- MovieList: Add All header + active rows -------------------

            var movieList = new GenericItemList();

            int selectedCount = active.Count(e =>
                e.State == AddMovieState.Confident && !e.IsAddBlocked && e.IncludedInBulkAdd);
            int queuedCount = active.Count(e => e.State == AddMovieState.Queued);
            bool anyAdding = active.Any(e => e.State == AddMovieState.Adding);
            bool addAllEnabled = selectedCount > 0 || queuedCount > 0;

            ItemStatus addAllStatus = anyAdding || queuedCount > 0
                ? ItemStatus.InProgress
                : addAllEnabled ? ItemStatus.Succeeded : ItemStatus.Unavailable;

            string addAllCaption;
            if (selectedCount > 0 && queuedCount > 0)
                addAllCaption = string.Format(
                    "Add All Matched to Library  ({0} selected, {1} queued)",
                    selectedCount, queuedCount);
            else if (queuedCount > 0)
                addAllCaption = string.Format(
                    "Add All Matched to Library  ({0} queued)", queuedCount);
            else if (selectedCount > 0)
                addAllCaption = string.Format(
                    "Add All Matched to Library  ({0} selected)", selectedCount);
            else
                addAllCaption = "Add All Matched to Library";

            var addAllRow = new GenericListItem(
                IconNames.add_circle,
                addAllCaption,
                string.Empty)
            {
                IconMode = ItemListIconMode.SmallRegular,
                Status = addAllStatus,
                HasPercentage = false,
                Button1 = new ButtonItem(
                    queuedCount > 0 && selectedCount == 0 ? "Resume Queue" : "Add All")
                {
                    Icon = IconNames.add_circle,
                    Data1 = "AddAll",
                    CommandId = "AddAll",
                    IsEnabled = addAllEnabled,
                },
                SubItems = active.Select(BuildMovieRow).ToList(),
            };

            movieList.Add(addAllRow);

            // ---- CompletedList: Completed header + completed rows ----------

            var completedList = new GenericItemList();

            var completedRow = new GenericListItem(
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
                    IsEnabled = completed.Length > 0,
                },
                SubItems = completed.Select(BuildMovieRow).ToList(),
            };

            completedList.Add(completedRow);

            ui.MovieList = movieList;
            ui.CompletedList = completedList;

            // Diagnostic: row counts actually sent to the client on this
            // broadcast, tagged with the instance that produced them and a
            // per-instance monotonic sequence number.
            //   - If active/completed counts here don't move in a clean
            //     staircase (e.g. 5,4,3,2,1,0) that matches what the browser
            //     shows a moment later, the server itself is the source —
            //     look at AddMovieTracker's mutation methods / GetAllSorted.
            //   - If the counts here DO move cleanly but the browser still
            //     flickers, the problem is downstream of this method: either
            //     multiple instanceId values are interleaved (see the
            //     construct/dispose log lines), or the client itself.
            //   - If more than one distinct instanceId appears with
            //     overlapping "constructed"/"disposed" log lines, that alone
            //     confirms duplicate live AddMoviePageView instances.
            this.logger.Info(
                "MCS-DIAG instance={0} event=rows seq={1} active={2} completed={3} names=[{4}]",
                this.instanceId,
                System.Threading.Interlocked.Increment(ref this.broadcastSeq),
                active.Length,
                completed.Length,
                string.Join(", ", active.Take(5)
                    .Select(e => string.Format("{0}[{1}]", e.DisplayTitle, e.State))));
        }

        // -----------------------------------------------------------------------
        // Overall status footer
        // -----------------------------------------------------------------------

        private void UpdateOverallStatus()
        {
            var entries = AddMovieTracker.GetAllSorted();

            if (entries.Length == 0)
            {
                SetOverallStatus(string.Empty, ItemStatus.Unavailable);
                return;
            }

            int searching = entries.Count(e => e.State == AddMovieState.Searching);
            int confident = entries.Count(e => e.State == AddMovieState.Confident && !e.IsAddBlocked);
            int conflict = entries.Count(e => e.IsAddBlocked);
            int multiple = entries.Count(e => e.State == AddMovieState.MultipleMatches);
            int noResults = entries.Count(e => e.State == AddMovieState.NoResults
                                             || e.State == AddMovieState.SearchFailed);
            int queued = entries.Count(e => e.State == AddMovieState.Queued);
            int adding = entries.Count(e => e.State == AddMovieState.Adding);
            int added = entries.Count(e => e.State == AddMovieState.Added);
            int failed = entries.Count(e => e.State == AddMovieState.AddFailed);

            var parts = new List<string>();
            if (searching > 0) parts.Add(string.Format("{0} searching", searching));
            if (confident > 0) parts.Add(string.Format("{0} matched", confident));
            if (multiple > 0) parts.Add(string.Format("{0} needs selection", multiple));
            if (conflict > 0) parts.Add(string.Format("{0} target conflict", conflict));
            if (queued > 0) parts.Add(string.Format("{0} queued", queued));
            if (adding > 0) parts.Add(string.Format("{0} adding", adding));
            if (added > 0) parts.Add(string.Format("{0} added", added));
            if (noResults > 0) parts.Add(string.Format("{0} not found", noResults));
            if (failed > 0) parts.Add(string.Format("{0} failed", failed));

            ItemStatus status = ItemStatus.Unavailable;
            if (adding > 0 || searching > 0 || queued > 0)
                status = ItemStatus.InProgress;
            else if (failed > 0 || noResults > 0 || conflict > 0 || multiple > 0)
                status = ItemStatus.Warning;
            else if (confident > 0 || added > 0)
                status = ItemStatus.Succeeded;

            SetOverallStatus(string.Join("  /  ", parts), status);
        }

        private void SetOverallStatus(string message, ItemStatus status)
        {
            UI.OverallStatus.StatusText = message;
            UI.OverallStatus.Status = status;
        }
    }
}
