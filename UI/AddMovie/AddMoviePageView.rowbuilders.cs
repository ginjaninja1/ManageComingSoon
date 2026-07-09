// ManageComingSoon - Add Movie Page View [RowBuilders]
// BuildMovieRow, the candidate + info sub-row builders, and the small
// State → (icon / text / button) mapping helpers.
// See AddMoviePageView.cs for the full file map.
//
// Design principle: this file is a thin dressing function from tracker state
// to UI composition. Every rebuild constructs fresh GenericListItem instances
// from scratch — no caching, no reuse, no memory of previous state.
//
// All domain interpretation (best display title, display year, display path,
// whether to show progress, whether the add button is blocked) is resolved
// by computed properties on AddMovieEntry. This file reads those properties
// and maps them to Emby UI primitives — it makes no domain decisions itself.
//
// DestinationConflict is not a separate state: it is entry.IsAddBlocked,
// an annotation on a Confident entry. BuildPrimaryButton has one Confident
// case whose IsEnabled reads that flag; BuildSecondaryText has one Confident
// case that checks it for the conflict reason. No DestinationConflict case
// appears anywhere in this file.

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
        // BuildMovieRow — dresses one tracker entry as a GenericListItem
        // -----------------------------------------------------------------------

        private GenericListItem BuildMovieRow(AddMovieEntry entry)
        {
            var row = new GenericListItem
            {
                PrimaryText = BuildPrimaryText(entry),
                SecondaryText = BuildSecondaryText(entry),
                IconMode = ItemListIconMode.SmallRegular,
                Status = StateToItemStatus(entry),
                HasPercentage = entry.ShowProgress,
                PercentComplete = entry.ShowProgress ? entry.AddingPercent : 0,
            };

            // Spinner replaces the static icon while actively adding.
            if (entry.State == AddMovieState.Adding)
                row.StandardIcon = StandardIcons.Loading;
            else
                row.Icon = StateToIcon(entry.State);

            row.Button1 = BuildPrimaryButton(entry);

            row.Button2 = new ButtonItem(
                entry.State == AddMovieState.Added ? "Clear" : "Remove")
            {
                StandardIcon = StandardIcons.Remove,
                Data1 = "Remove_" + entry.Id,
                CommandId = "Remove_" + entry.Id,
                // Removing is blocked while the pipeline holds the entry.
                IsEnabled = entry.State != AddMovieState.Adding,
            };

            // Toggle: only Confident rows participate in bulk-add selection.
            // Non-Added, non-Confident rows get a disabled placeholder so the
            // toggle column width stays consistent across mixed-state lists.
            if (entry.State == AddMovieState.Confident)
            {
                row.Toggle = new ToggleButtonItem("Select")
                {
                    IsChecked = entry.IncludedInBulkAdd,
                    Data1 = "ToggleBulk_" + entry.Id,
                    CommandId = "ToggleBulk_" + entry.Id,
                    IsEnabled = true,
                };
            }
            else if (entry.State != AddMovieState.Added)
            {
                row.Toggle = new ToggleButtonItem("Select")
                {
                    IsChecked = false,
                    Data1 = "_noop",
                    CommandId = "_noop",
                    IsEnabled = false,
                };
            }

            if (entry.State == AddMovieState.MultipleMatches
                && entry.Candidates.Count > 0)
                row.SubItems = BuildCandidateSubItems(entry);

            return row;
        }

        // -----------------------------------------------------------------------
        // Primary text
        // -----------------------------------------------------------------------

        private static string BuildPrimaryText(AddMovieEntry entry)
        {
            // DisplayTitle and DisplayYear are computed by AddMovieEntry:
            // confirmed values when known, search input before that.
            string titleYear = entry.DisplayYear > 0
                ? string.Format("{0} ({1})", entry.DisplayTitle, entry.DisplayYear)
                : entry.DisplayTitle;

            // Queued rows show the destination parent path as a visual confirmation
            // that the correct target folder has been resolved.
            if (entry.State == AddMovieState.Queued
                && !string.IsNullOrEmpty(entry.DisplayFolderPath))
                return string.Format("{0}  \u2192  {1}", titleYear, entry.DisplayFolderPath);

            return titleYear;
        }

        // -----------------------------------------------------------------------
        // Secondary text
        // -----------------------------------------------------------------------

        private static string BuildSecondaryText(AddMovieEntry entry)
        {
            switch (entry.State)
            {
                case AddMovieState.Searching:
                    return "Searching TMDB...";

                case AddMovieState.MultipleMatches:
                    return string.Format(
                        "Multiple matches found — select the correct one ({0} options):",
                        entry.Candidates.Count);

                case AddMovieState.NoResults:
                    return "No results found — search again or use 'Manual'";

                case AddMovieState.SearchFailed:
                    return string.IsNullOrEmpty(entry.ErrorMessage)
                        ? "Search failed — search again or use 'Manual'"
                        : string.Format("Search failed: {0} — search again or use 'Manual'",
                            TruncateError(entry.ErrorMessage));

                case AddMovieState.Confident:
                    // Destination conflict takes priority over the overview.
                    if (entry.IsAddBlocked)
                        return string.IsNullOrEmpty(entry.ConflictReason)
                            ? "Target folder already in use — resolve before adding"
                            : entry.ConflictReason;

                    if (entry.ConfirmedTmdbId == 0)
                        return "Manual Entry";

                    return string.IsNullOrEmpty(entry.ConfirmedOverview)
                        ? "Confident match found"
                        : Truncate(entry.ConfirmedOverview, 140);

                case AddMovieState.Queued:
                    return "In queue — will be processed by the next Add All run";

                case AddMovieState.Adding:
                    return string.IsNullOrEmpty(entry.AddingDetail)
                        ? "Adding to library — please wait..."
                        : entry.AddingDetail;

                case AddMovieState.Added:
                    // CompletedAt is always set by SetAdded; the fallback is a safety net.
                    string path = !string.IsNullOrEmpty(entry.DisplayFolderPath)
                        ? entry.DisplayFolderPath : "library";
                    return entry.CompletedAt.HasValue
                        ? string.Format("Added {0}  \u2014  {1}",
                            path,
                            entry.CompletedAt.Value.ToLocalTime().ToString("dd MMM yyyy, HH:mm"))
                        : string.Format("Added {0}", path);

                case AddMovieState.AddFailed:
                    return string.IsNullOrEmpty(entry.ErrorMessage)
                        ? "Add failed — retry or remove this entry"
                        : string.Format("Add failed: {0}", TruncateError(entry.ErrorMessage));

                default:
                    return string.Empty;
            }
        }

        // -----------------------------------------------------------------------
        // Primary button
        // -----------------------------------------------------------------------

        private static ButtonItem BuildPrimaryButton(AddMovieEntry entry)
        {
            switch (entry.State)
            {
                case AddMovieState.Confident:
                    // IsAddBlocked (destination conflict) disables the button
                    // without changing the label — the user can see what they
                    // would be pressing once the conflict clears.
                    return new ButtonItem("Add to Library")
                    {
                        Icon = IconNames.add_circle,
                        Data1 = "Add_" + entry.Id,
                        CommandId = "Add_" + entry.Id,
                        IsEnabled = !entry.IsAddBlocked,
                    };

                case AddMovieState.NoResults:
                case AddMovieState.SearchFailed:
                    return new ButtonItem("Manual")
                    {
                        Icon = IconNames.add_circle,
                        Data1 = "Manual_" + entry.Id,
                        CommandId = "Manual_" + entry.Id,
                        IsEnabled = true,
                    };

                case AddMovieState.AddFailed:
                    return new ButtonItem("Retry")
                    {
                        StandardIcon = StandardIcons.Refresh,
                        Data1 = "Add_" + entry.Id,
                        CommandId = "Add_" + entry.Id,
                        IsEnabled = true,
                    };

                case AddMovieState.Added:
                    return new ButtonItem("Added")
                    {
                        Icon = IconNames.check_circle,
                        Data1 = "_noop",
                        CommandId = "_noop",
                        IsEnabled = false,
                    };

                default:
                    // Searching, MultipleMatches, Queued, Adding — no action button.
                    return null;
            }
        }

        // -----------------------------------------------------------------------
        // State mapping helpers — icon and item status
        // -----------------------------------------------------------------------

        private static IconNames StateToIcon(AddMovieState state)
        {
            switch (state)
            {
                case AddMovieState.Queued: return IconNames.hourglass_empty;
                case AddMovieState.Added: return IconNames.check_circle;
                default: return IconNames.video_library;
            }
        }

        private static ItemStatus StateToItemStatus(AddMovieEntry entry)
        {
            switch (entry.State)
            {
                case AddMovieState.Searching: return ItemStatus.InProgress;
                case AddMovieState.MultipleMatches: return ItemStatus.Warning;
                case AddMovieState.NoResults: return ItemStatus.Failed;
                case AddMovieState.SearchFailed: return ItemStatus.Failed;
                case AddMovieState.Confident:
                    // A blocked Confident row is visually distinct from a clear one.
                    return entry.IsAddBlocked ? ItemStatus.Failed : ItemStatus.Succeeded;
                case AddMovieState.Queued: return ItemStatus.Unavailable;
                case AddMovieState.Adding: return ItemStatus.InProgress;
                case AddMovieState.Added: return ItemStatus.Succeeded;
                case AddMovieState.AddFailed: return ItemStatus.Failed;
                default: return ItemStatus.Unavailable;
            }
        }

        // -----------------------------------------------------------------------
        // Candidate sub-items (MultipleMatches state)
        // -----------------------------------------------------------------------

        private List<GenericListItem> BuildCandidateSubItems(AddMovieEntry entry)
        {
            var subItems = new List<GenericListItem>();
            bool isExpanded = this.expandedCandidates.Contains(entry.Id);
            int limit = isExpanded ? MaxExpandedCandidates : MaxDefaultCandidates;
            int shown = Math.Min(entry.Candidates.Count, limit);

            for (int i = 0; i < shown; i++)
            {
                var c = entry.Candidates[i];
                string cYear = c.ReleaseYear > 0 ? c.ReleaseYear.ToString() : "?";
                string cDesc = Truncate(c.Overview ?? string.Empty, 120);
                string infoKey = string.Format("{0}_{1}", entry.Id, i);
                bool infoOpen = this.expandedInfo.Contains(infoKey);

                var sub = new GenericListItem(
                    IconNames.movie,
                    string.Format("{0} ({1})", c.Title, cYear),
                    cDesc)
                {
                    IconMode = ItemListIconMode.SmallRegular,
                    Button1 = new ButtonItem(infoOpen ? "Hide Info" : "Info")
                    {
                        Icon = IconNames.info,
                        Data1 = string.Format("Info_{0}_{1}", entry.Id, i),
                        CommandId = string.Format("Info_{0}_{1}", entry.Id, i),
                    },
                    Button2 = new ButtonItem("Select")
                    {
                        Icon = IconNames.check_circle,
                        Data1 = string.Format("Select_{0}_{1}", entry.Id, i),
                        CommandId = string.Format("Select_{0}_{1}", entry.Id, i),
                    },
                };

                if (infoOpen)
                    sub.SubItems = BuildInfoSubItems(c);

                subItems.Add(sub);
            }

            // Expand / collapse footer row — only shown when there are more
            // candidates than the default limit.
            if (entry.Candidates.Count > MaxDefaultCandidates)
            {
                if (!isExpanded)
                {
                    int extra = entry.Candidates.Count - MaxDefaultCandidates;
                    subItems.Add(new GenericListItem(
                        IconNames.search,
                        string.Format("{0} more result(s)", extra),
                        string.Empty)
                    {
                        IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem("Show More")
                        {
                            Icon = IconNames.expand_more,
                            Data1 = "ShowMore_" + entry.Id,
                            CommandId = "ShowMore_" + entry.Id,
                        },
                        Button2 = new ButtonItem("Select")
                        {
                            Icon = IconNames.check_circle,
                            Data1 = "_noop",
                            CommandId = "_noop",
                            IsEnabled = false,
                        },
                    });
                }
                else
                {
                    subItems.Add(new GenericListItem(
                        IconNames.search,
                        "Showing all results",
                        string.Empty)
                    {
                        IconMode = ItemListIconMode.SmallRegular,
                        Button1 = new ButtonItem("Show Less")
                        {
                            Icon = IconNames.expand_less,
                            Data1 = "ShowLess_" + entry.Id,
                            CommandId = "ShowLess_" + entry.Id,
                        },
                        Button2 = new ButtonItem("Select")
                        {
                            Icon = IconNames.check_circle,
                            Data1 = "_noop",
                            CommandId = "_noop",
                            IsEnabled = false,
                        },
                    });
                }
            }

            return subItems;
        }

        // -----------------------------------------------------------------------
        // Info sub-items: TMDB link + cast
        // -----------------------------------------------------------------------

        private static List<GenericListItem> BuildInfoSubItems(AddMovieCandidate c)
        {
            var items = new List<GenericListItem>();

            items.Add(new GenericListItem(
                IconNames.open_in_new,
                "View on TMDB",
                c.TmdbUrl())
            {
                IconMode = ItemListIconMode.SmallRegular,
                HyperLink = c.TmdbUrl(),
                HyperLinkTargetExternal = true,
            });

            if (c.CastNames == null)
            {
                items.Add(new GenericListItem(
                    IconNames.movie,
                    "Fetching cast...",
                    string.Empty)
                { IconMode = ItemListIconMode.SmallRegular, StandardIcon = StandardIcons.Loading });
            }
            else if (c.CastNames.Count == 0)
            {
                items.Add(new GenericListItem(
                    IconNames.person,
                    "No cast information available",
                    string.Empty)
                { IconMode = ItemListIconMode.SmallRegular });
            }
            else
            {
                foreach (var name in c.CastNames)
                    items.Add(new GenericListItem(IconNames.person, name, string.Empty)
                    { IconMode = ItemListIconMode.SmallRegular });
            }

            return items;
        }

        // -----------------------------------------------------------------------
        // String helpers
        // -----------------------------------------------------------------------

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text ?? string.Empty;
            return text.Substring(0, maxLength - 3) + "...";
        }

        private static string TruncateError(string message)
        {
            return Truncate(message, 120);
        }
    }
}
