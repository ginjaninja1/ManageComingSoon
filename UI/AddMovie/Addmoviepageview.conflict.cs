// ManageComingSoon - Add Movie Page View [Conflict]
// Point-in-time destination conflict check.
//
// Called at two moments only — no polling:
//   1. When an entry becomes Confident (auto-match, manual confirm, candidate
//      select): CheckDestinationForEntry runs immediately. If blocked,
//      HasDestinationConflict is set, IncludedInBulkAdd is forced off, and
//      the row renders with the conflict reason as secondary text.
//
//   2. When the user toggles the bulk-add select on a blocked entry:
//      HandleToggleBulk re-runs CheckDestinationForEntry before allowing the
//      toggle. If still blocked, the toggle is denied and the overall status
//      footer shows "Path still in use: [reason]" so the user knows the check
//      ran and the block is still active.
//
// The check is NOT run while entries are Queued or Adding — a folder existing
// on disk at those stages means the pipeline created it, which is correct.
// IsAddBlocked is false by definition for any state other than Confident,
// so the check can never fire at the wrong time.

namespace ManageComingSoon.UI.AddMovie
{
    using System;
    using System.IO;
    using System.Collections.Generic;
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
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal partial class AddMoviePageView : PluginPageView, IDisposable
    {
        /// <summary>
        /// Point-in-time destination conflict check for a single Confident entry.
        ///
        /// Checks two things in order:
        ///   1. In-list duplicate: another tracker entry already claims the same
        ///      destination folder (pure in-memory, no disk access).
        ///   2. Disk conflict: the destination folder already exists on disk.
        ///
        /// If blocked: sets HasDestinationConflict + ConflictReason on the tracker,
        /// and forces IncludedInBulkAdd = false so the entry cannot drift into queue.
        /// If clear:   clears any prior conflict annotation.
        ///
        /// Returns true if the conflict annotation changed (caller should rebuild).
        /// No-op and returns false if the entry is not Confident.
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

            // In-list duplicate check (no disk I/O)
            var duplicate = AddMovieTracker.FindEarlierDuplicateDestination(
                safeName, id, entry.CreatedAt);

            if (duplicate != null)
            {
                conflict = true;
                reason = string.Format(
                    "Already in list: {0} ({1})",
                    duplicate.DisplayTitle, duplicate.DisplayYear);
            }
            else
            {
                // Disk check — only runs if no in-list duplicate found
                try
                {
                    if (Directory.Exists(destFolder))
                    {
                        conflict = true;
                        reason = string.Format("Path already in use: {0}", destFolder);
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
                AddMovieTracker.SetIncludedInBulkAdd(id, false);
                return true;
            }

            if (!conflict && entry.HasDestinationConflict)
            {
                AddMovieTracker.ClearConflict(id);
                return true;
            }

            return false;
        }
    }
}