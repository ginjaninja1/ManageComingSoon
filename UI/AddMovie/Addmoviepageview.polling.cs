// ManageComingSoon - Add Movie Page View [Polling]
// Background poll timer (StartPollTimer / StopPollTimer / OnPollTick /
// BuildPollSignature) and the destination-conflict checks it drives.
// See AddMoviePageView.cs for the full file map.
//
// Destination conflict change: DestinationConflict is no longer a separate
// state. CheckDestinationForEntry calls AddMovieTracker.SetConflict /
// ClearConflict, which annotate a Confident entry with HasDestinationConflict
// and ConflictReason without changing its state. The guard at the top of
// CheckDestinationForEntry therefore checks entry.State == Confident only
// (not DestinationConflict separately).
//
// Poll timer stop condition: StopPollTimer is called when neither AnyInFlight()
// nor AnyNeedingPoll() is true. AnyNeedingPoll covers Confident and Queued
// rows — the two states that need periodic disk conflict checks.
//
// Thread safety note: CheckAllDestinations mutates tracker state (SetConflict /
// ClearConflict) on the poll thread while command handlers may concurrently
// read and transition Confident entries. Each individual tracker method is
// locked, so individual transitions are safe. A command handler that calls
// SetQueued between our Get() and our SetConflict() will find the entry no
// longer Confident, and SetConflict guards against that — so the interleaving
// is benign. The broader recommendation (returning a list of desired changes
// and applying them inside RebuildAndBroadcast's lock) would be more rigorous
// but is deferred given the guard already in place.

namespace ManageComingSoon.UI.AddMovie
{
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using ManageComingSoon.Model;
    using ManageComingSoon.Services;
    using ManageComingSoon.Storage;
    using ManageComingSoon.UI.Configuration;
    using ManageComingSoon.UIBaseClasses.Views;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    internal partial class AddMoviePageView : PluginPageView, IDisposable
    {
        // -----------------------------------------------------------------------
        // Destination conflict check
        // -----------------------------------------------------------------------

        /// <summary>
        /// Evaluates whether a Confident entry's destination folder is available.
        /// Calls SetConflict / ClearConflict on the tracker as appropriate.
        /// Returns true if the entry's conflict annotation changed (rebuild needed).
        /// </summary>
        private bool CheckDestinationForEntry(string id)
        {
            var entry = AddMovieTracker.Get(id);
            if (entry == null || entry.State != AddMovieState.Confident) return false;

            string targetPath = ConfigurationPageView.PathFromKey(
                this.plugin.Configuration.ComingSoonTargetKey);
            if (string.IsNullOrEmpty(targetPath)) return false;

            string safeName = EmbyLibrarySharedService.BuildComingSoonFolderName(
                entry.ConfirmedTitle, entry.ConfirmedYear);
            string destFolder = Path.Combine(targetPath, safeName);

            bool conflict = false;
            string reason = string.Empty;

            var duplicate = AddMovieTracker.FindEarlierDuplicateDestination(
                safeName, id, entry.CreatedAt);

            if (duplicate != null)
            {
                conflict = true;
                reason = "Movie already in list";
            }
            else
            {
                try
                {
                    if (Directory.Exists(destFolder))
                    {
                        conflict = true;
                        reason = string.Format("Target folder already in use: {0}", destFolder);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Warn(
                        "Destination check failed for '{0}': {1}",
                        entry.ConfirmedTitle, ex.Message);
                    return false;
                }
            }

            if (conflict && !entry.HasDestinationConflict)
            {
                AddMovieTracker.SetConflict(id, reason);
                return true;
            }

            if (!conflict && entry.HasDestinationConflict)
            {
                AddMovieTracker.ClearConflict(id);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks all Confident entries for destination conflicts.
        /// Returns true if any entry's conflict annotation changed.
        /// </summary>
        private bool CheckAllDestinations()
        {
            bool anyChanged = false;
            foreach (var entry in AddMovieTracker.GetAllSorted())
            {
                if (entry.State == AddMovieState.Confident)
                    if (CheckDestinationForEntry(entry.Id))
                        anyChanged = true;
            }
            return anyChanged;
        }

        // -----------------------------------------------------------------------
        // Poll timer — 1 s tick; disk check every 5 ticks (every 5 s)
        // -----------------------------------------------------------------------

        private void StartPollTimer()
        {
            if (this.pollTimer != null) return;
            this.diskPollCounter = 0;
            this.lastPolledSignature = string.Empty;
            this.pollTimer = new Timer(
                OnPollTick, null,
                dueTime: TimeSpan.FromSeconds(1),
                period: TimeSpan.FromSeconds(1));
        }

        private void StopPollTimer()
        {
            var t = Interlocked.Exchange(ref this.pollTimer, null);
            if (t == null) return;
            t.Dispose();
            this.diskPollCounter = 0;
        }

        private void OnPollTick(object state)
        {
            if (this.disposed) return;
            try
            {
                this.diskPollCounter++;
                if (this.diskPollCounter >= 5)
                {
                    this.diskPollCounter = 0;
                    // CheckAllDestinations mutates tracker annotations; the
                    // signature check below will catch the resulting change
                    // and trigger a rebuild if needed.
                    CheckAllDestinations();
                }

                string sig = BuildPollSignature();
                if (sig != this.lastPolledSignature)
                {
                    this.lastPolledSignature = sig;
                    RequestUiRefresh();
                }

                if (!AddMovieTracker.AnyInFlight() && !AddMovieTracker.AnyNeedingPoll())
                    StopPollTimer();
            }
            catch (Exception ex)
            {
                try { this.logger.ErrorException("OnPollTick error", ex); }
                catch { }
            }
        }

        /// <summary>
        /// Signature of all tracked entries' mutable display fields.
        /// A change in signature triggers a RebuildAndBroadcast from OnPollTick.
        /// HasDestinationConflict is included so conflict annotation changes
        /// are caught without a separate mechanism.
        /// </summary>
        private static string BuildPollSignature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in AddMovieTracker.GetAllSorted())
            {
                sb.Append(e.Id).Append(':')
                  .Append((int)e.State).Append(':')
                  .Append(e.HasDestinationConflict ? '1' : '0').Append(':')
                  .Append(e.AddingStep).Append(':')
                  .Append(e.AddingDetail).Append(':')
                  .Append(e.AddingPercent).Append('|');
            }
            return sb.ToString();
        }
    }
}