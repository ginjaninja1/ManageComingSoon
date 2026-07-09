// ManageComingSoon - Make Live Page View [RowBuilders]
// BuildActiveTrackerRow, BuildQueuedRow, BuildHistoryRow, BuildMovieRow, and
// the centralised State -> (icon/status) mapping helpers.
// See MakeLivePageView.cs for the full file map.
//
// Icon conventions:
//   • StateToIcon returns video_library for unactioned/default states.
//     Used only for tracker rows; Coming Soon rows always use video_library directly.
//   • Ready/blocked state on Coming Soon rows is conveyed via ItemStatus
//     (Succeeded / Failed) alone — icon column stays visually uniform.
//   • The Make Live button uses add_circle.
//
// Primary / secondary line layout for Queued and Active rows:
//   Primary   — "Movie Name (Year)   C:\ComingSoon  →  D:\Movies"
//               Built by BuildTrackerPrimaryText(entry).  Set once at queue
//               time from entry.TargetLibraryPath; stays fixed for the row's
//               lifetime so the user always knows which item and where it is going.
//   Secondary — Live stage message fed by MakeLiveTracker.SetMessage(), which
//               MakeLiveTask updates at each pipeline stage.
//               Examples: "Creating destination folder…"
//                         "Confirming movie is live — 2nd check (60s)…"
//
// History row layout:
//   Primary   — "Movie Name (Year)  —  dd MMM yyyy HH:mm"
//   Secondary — "D:\SourceLibrary -> D:\DestLibrary"

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

    internal partial class MakeLivePageView : PluginPageView, IDisposable
    {
        // -----------------------------------------------------------------------
        // Centralised State -> icon/status mapping
        // All tracker row builders call these so decisions live in one place.
        // -----------------------------------------------------------------------

        private static IconNames StateToIcon(MakeLiveState state)
        {
            switch (state)
            {
                case MakeLiveState.Queued: return IconNames.hourglass_empty;
                // Both active sub-phases share autorenew so the user sees a consistent
                // spinning circle. The secondary text (stage messages) distinguishes them.
                case MakeLiveState.Moving: return IconNames.autorenew;
                case MakeLiveState.ScanPending: return IconNames.autorenew;
                case MakeLiveState.Complete: return IconNames.check_circle;
                case MakeLiveState.Failed: return IconNames.warning;
                default: return IconNames.video_library;
            }
        }

        private static ItemStatus StateToItemStatus(MakeLiveState state)
        {
            switch (state)
            {
                case MakeLiveState.Queued: return ItemStatus.InProgress;
                case MakeLiveState.Moving: return ItemStatus.InProgress;
                case MakeLiveState.ScanPending: return ItemStatus.InProgress;
                case MakeLiveState.Complete: return ItemStatus.Succeeded;
                case MakeLiveState.Failed: return ItemStatus.Failed;
                default: return ItemStatus.Unavailable;
            }
        }

        // -----------------------------------------------------------------------
        // Shared primary-text builder for Queued and Active tracker rows.
        //
        // Format: "Movie Name (Year)   C:\ComingSoon  →  D:\Movies"
        //
        // Both source and destination are library-level paths (the folder
        // containing the movie folder, not the movie folder itself), so the
        // display reads as a library-to-library move.  When no move is configured,
        // both sides are the same library path — "C:\ComingSoon → C:\ComingSoon" —
        // making it obvious the item stays in place without a special-case layout.
        //
        // TargetLibraryPath is set by SetQueued() from the configured target path,
        // or null when no move is configured. The fallback to sourceLib handles null.
        // -----------------------------------------------------------------------

        private static string BuildTrackerPrimaryText(MakeLiveEntry entry)
        {
            string namePart = entry.Year > 0
                ? string.Format("{0} ({1})", entry.ItemName, entry.Year)
                : entry.ItemName;

            string sourceLib = !string.IsNullOrEmpty(entry.FolderPath)
                ? Path.GetDirectoryName(entry.FolderPath) ?? entry.FolderPath
                : string.Empty;

            // No move: TargetLibraryPath is null; fall back to sourceLib so both
            // sides of the arrow show the same path ("C:\ComingSoon → C:\ComingSoon").
            string destLib = !string.IsNullOrEmpty(entry.TargetLibraryPath)
                ? entry.TargetLibraryPath
                : sourceLib;

            return string.IsNullOrEmpty(sourceLib)
                ? namePart
                : string.Format("{0}   {1}  \u2192  {2}", namePart, sourceLib, destLib);
        }

        // -----------------------------------------------------------------------
        // In-flight tracker row (Moving or ScanPending)
        //
        // Primary:   BuildTrackerPrimaryText — fixed record, set at queue time.
        // Secondary: entry.Message — live stage message from MakeLiveTask via
        //            MakeLiveTracker.SetMessage(). Updates each pipeline stage,
        //            including per-check updates during the long ConfirmTargetState
        //            wait ("Confirming movie is live — 2nd check (60s)…").
        // -----------------------------------------------------------------------

        private GenericListItem BuildActiveTrackerRow(MakeLiveEntry entry)
        {
            var item = new GenericListItem(
                StateToIcon(entry.State),
                BuildTrackerPrimaryText(entry),
                entry.Message ?? string.Empty)
            {
                IconMode = ItemListIconMode.SmallRegular,
                Status = StateToItemStatus(entry.State),
                HasPercentage = true,
                PercentComplete = entry.Percent,
            };

            item.Button1 = new ButtonItem("Make Live")
            {
                Icon = IconNames.add_circle,
                Data1 = "_noop",
                CommandId = "_noop",
                IsEnabled = false,
            };

            item.Toggle = new ToggleButtonItem("Select")
            {
                IsChecked = entry.ToggledOn,
                Data1 = "_noop",
                CommandId = "_noop",
                IsEnabled = false,
            };

            return item;
        }

        // -----------------------------------------------------------------------
        // Queued row — item selected and handed to the task queue but not yet
        // picked up by MakeLiveTask.Register().  Rendered between Pending and
        // Active rows so the user sees the full pipeline at a glance:
        // Pending → Queued → Moving / ScanPending → Complete/Failed.
        //
        // Primary:   BuildTrackerPrimaryText — same fixed format as Active rows.
        // Secondary: static "Queued — awaiting processing" since no stage messages
        //            are available until Register() fires and the pipeline starts.
        //
        // Button1 is an enabled "Remove" button so the user can pull the item
        // out of the queue before the task picks it up.  Firing DequeueOne_
        // reverts the entry to Pending and rebuilds the list.
        //
        // Toggle is shown checked-and-disabled: confirms the item was selected;
        // the Remove button is the intentional escape hatch.
        // -----------------------------------------------------------------------

        private GenericListItem BuildQueuedRow(MakeLiveEntry entry)
        {
            var item = new GenericListItem(
                IconNames.hourglass_empty,
                BuildTrackerPrimaryText(entry),
                "Queued \u2014 awaiting processing")
            {
                IconMode = ItemListIconMode.SmallRegular,
                // Status intentionally not set: InProgress replaces the row icon
                // with an animated spinner, which hides hourglass_empty.  Leaving
                // Status at default lets hourglass_empty render as a static icon.
                HasPercentage = false,
            };

            item.Button1 = new ButtonItem("Remove")
            {
                Icon = IconNames.remove_circle,
                Data1 = "DequeueOne_" + entry.FolderPath,
                CommandId = "DequeueOne_" + entry.FolderPath,
                IsEnabled = true,
            };

            item.Toggle = new ToggleButtonItem("Select")
            {
                IsChecked = true,
                Data1 = "_noop",
                CommandId = "_noop",
                IsEnabled = false,
            };

            return item;
        }

        // -----------------------------------------------------------------------
        // History row (Complete or Failed)
        //
        // Primary   — "Movie Name (Year)  —  dd MMM yyyy HH:mm"
        // Secondary — "D:\SourceLibrary -> D:\DestLibrary"
        //             (Path.GetDirectoryName strips the movie subfolder so only
        //              the containing library folder is shown)
        // -----------------------------------------------------------------------

        private GenericListItem BuildHistoryRow(MakeLiveEntry entry)
        {
            string yearPart = entry.Year > 0
                ? string.Format(" ({0})", entry.Year)
                : string.Empty;
            string datePart = entry.CompletedAt.HasValue
                ? entry.CompletedAt.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm")
                : string.Empty;
            string primary = string.IsNullOrEmpty(datePart)
                ? string.Format("{0}{1}", entry.ItemName, yearPart)
                : string.Format("{0}{1}  \u2014  {2}", entry.ItemName, yearPart, datePart);

            string srcParent = !string.IsNullOrEmpty(entry.FolderPath)
                ? Path.GetDirectoryName(entry.FolderPath) ?? entry.FolderPath
                : string.Empty;
            string dstParent = !string.IsNullOrEmpty(entry.TargetFolderPath)
                ? Path.GetDirectoryName(entry.TargetFolderPath) ?? entry.TargetFolderPath
                : string.Empty;

            // Failed rows: always show the stored error message. Previously this
            // fell through to the srcParent branch below (FolderPath is populated
            // on every entry, including failures), so the actual failure reason
            // set by SetFailed() was never surfaced — the row just repeated the
            // source path, identical to a successful row.
            string secondary;
            if (entry.State == MakeLiveState.Failed)
                secondary = !string.IsNullOrEmpty(entry.Message) ? entry.Message : "Failed — no reason recorded.";
            else if (!string.IsNullOrEmpty(srcParent) && !string.IsNullOrEmpty(dstParent)
                && !string.Equals(srcParent, dstParent, StringComparison.OrdinalIgnoreCase))
                secondary = string.Format("{0} -> {1}", srcParent, dstParent);
            else if (!string.IsNullOrEmpty(srcParent))
                secondary = srcParent;
            else
                secondary = entry.Message ?? string.Empty;

            return new GenericListItem(
                StateToIcon(entry.State),
                primary,
                secondary)
            {
                IconMode = ItemListIconMode.SmallRegular,
                Status = StateToItemStatus(entry.State),
                HasPercentage = false,
            };
        }

        // -----------------------------------------------------------------------
        // Pending (Coming Soon) row — built from a MakeLiveState.Pending tracker
        // entry. No local MakeLiveRow class; tracker entry is the source of truth.
        //
        // Icon is always video_library; ItemStatus carries the ready/blocked signal.
        //
        // Button1 (Make Live / Retry):
        //   • In-flight  → disabled placeholder (belt-and-suspenders; normally
        //                   RebuildMovieList emits an Active row instead)
        //   • Ready + On → "Make Live" enabled
        //   • Ready + Off → disabled placeholder (preserves column width)
        //   • Blocked    → "Retry" enabled (triggers fresh analysis)
        //
        // Toggle (Select): ToggleButtonItem with IsChecked driving the visual;
        //   disabled while any run phase is active to prevent mid-run changes.
        // -----------------------------------------------------------------------

        private GenericListItem BuildMovieRow(MakeLiveEntry entry, bool isRunning = false)
        {
            bool isOn = entry.ToggledOn;
            bool isReady = entry.Analysis != null && entry.Analysis.IsSafeToProceed;
            bool isInFlight = MakeLiveTracker.IsInFlight(entry.FolderPath);
            bool isLocked = isInFlight || isRunning;

            var item = new GenericListItem(
                IconNames.video_library,
                entry.Year > 0
                    ? string.Format("{0} ({1})", entry.ItemName, entry.Year)
                    : entry.ItemName,
                BuildSecondaryText(entry))
            {
                IconMode = ItemListIconMode.SmallRegular,
                Status = isReady ? ItemStatus.Succeeded : ItemStatus.Failed,
                HasPercentage = false,
            };

            if (isLocked)
            {
                item.Button1 = new ButtonItem("Make Live")
                {
                    Icon = IconNames.add_circle,
                    Data1 = "_noop",
                    CommandId = "_noop",
                    IsEnabled = false,
                };
            }
            else if (isReady && isOn)
            {
                item.Button1 = new ButtonItem("Make Live")
                {
                    Icon = IconNames.add_circle,
                    Data1 = "MakeLiveOne_" + entry.FolderPath,
                    CommandId = "MakeLiveOne_" + entry.FolderPath,
                    IsEnabled = true,
                };
            }
            else if (!isReady)
            {
                item.Button1 = new ButtonItem("Retry")
                {
                    StandardIcon = StandardIcons.Refresh,
                    Data1 = "Retry_" + entry.FolderPath,
                    CommandId = "Retry_" + entry.FolderPath,
                    IsEnabled = true,
                };
            }
            else
            {
                // Ready but not selected — placeholder preserves column width.
                item.Button1 = new ButtonItem("Make Live")
                {
                    Icon = IconNames.add_circle,
                    Data1 = "_noop",
                    CommandId = "_noop",
                    IsEnabled = false,
                };
            }

            item.Toggle = new ToggleButtonItem("Select")
            {
                IsChecked = isOn,
                Data1 = isLocked ? "_noop" : "Toggle_" + entry.FolderPath,
                CommandId = isLocked ? "_noop" : "Toggle_" + entry.FolderPath,
                IsEnabled = !isLocked,
            };

            return item;
        }

        // -----------------------------------------------------------------------
        // Secondary text for Pending (Coming Soon) rows
        // -----------------------------------------------------------------------

        private static string BuildSecondaryText(MakeLiveEntry entry)
        {
            bool isReady = entry.Analysis != null && entry.Analysis.IsSafeToProceed;
            if (isReady)
                return string.Format("Ready — {0}", entry.FolderPath);

            var warnings = entry.Analysis?.Warnings;
            return warnings != null && warnings.Count > 0
                ? warnings[0]
                : "Not ready — see server log for details";
        }
    }
}