// ManageComingSoon - Add Movie Page View [Commands]
// RunCommand dispatch table + every Handle*/RunSearchAsync/FetchCastAsync
// method it routes to. See AddMoviePageView.cs for the full file map.

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
    using ManageComingSoon.Services;
    using ManageComingSoon.UI.Configuration;
    using ManageComingSoon.UIBaseClasses.Views;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Tasks;

    internal partial class AddMoviePageView : PluginPageView, IDisposable
    {
        // -----------------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------------

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (commandId == "AddToList")
            {
                HandleAddToList();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "AddManual")
            {
                HandleAddManual();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "AddAll")
            {
                HandleAddAll();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Remove_", StringComparison.Ordinal))
            {
                HandleRemove(commandId.Substring(7));
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "ClearCompleted")
            {
                HandleClearCompleted();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("ToggleBulk_", StringComparison.Ordinal))
            {
                HandleToggleBulk(commandId.Substring(11));
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Manual_", StringComparison.Ordinal))
            {
                HandleManual(commandId.Substring(7));
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Retry_", StringComparison.Ordinal))
            {
                HandleRetry(commandId.Substring(6));
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Select_", StringComparison.Ordinal))
            {
                HandleSelect(commandId);
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Add_", StringComparison.Ordinal))
            {
                // Single-item add — still uses the task so it survives navigation.
                // Transition the entry to Queued and trigger the task identically
                // to Add All with a single-item queue.
                string id = commandId.Substring(4);
                HandleAddOne(id);
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("ShowMore_", StringComparison.Ordinal))
            {
                expandedCandidates.Add(commandId.Substring(9));
                RequestUiRefresh();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("ShowLess_", StringComparison.Ordinal))
            {
                expandedCandidates.Remove(commandId.Substring(9));
                RequestUiRefresh();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId.StartsWith("Info_", StringComparison.Ordinal))
            {
                HandleInfo(commandId);
                return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        // -----------------------------------------------------------------------
        // Add to list
        // -----------------------------------------------------------------------

        // Safety cap — guards against a pasted wall of text silently kicking off
        // dozens of simultaneous TMDB searches.
        private const int MaxBulkEntries = 25;

        private sealed class BulkMovieEntry
        {
            public string Name;
            public int? Year;
        }

        // Splits the Movie Name field on '|' into individual movies, then each
        // movie on ';' into name + optional year.
        private static List<BulkMovieEntry> ParseBulkMovieInput(string raw)
        {
            var result = new List<BulkMovieEntry>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (var rawSegment in raw.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(rawSegment)) continue;

                string namePart = rawSegment;
                string yearPart = null;

                int semi = rawSegment.IndexOf(';');
                if (semi >= 0)
                {
                    namePart = rawSegment.Substring(0, semi);
                    yearPart = rawSegment.Substring(semi + 1);
                }

                string name = namePart.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                result.Add(new BulkMovieEntry { Name = name, Year = ParseYear(yearPart) });
            }

            return result;
        }

        private static int? ParseYear(string text)
        {
            int parsed;
            if (!string.IsNullOrWhiteSpace(text)
                && int.TryParse(text.Trim(), out parsed)
                && parsed > 1800 && parsed < 2200)
                return parsed;
            return null;
        }

        private void HandleAddToList()
        {
            var ui = UI;

            RefreshAddButtonState();

            if (string.IsNullOrWhiteSpace(this.plugin.Configuration.TmdbApiKey))
            {
                SetOverallStatus(
                    "TMDB API key not configured. Please add one on the Configuration tab before adding movies.",
                    ItemStatus.Failed);
                RequestUiRefresh(preserveStatus: true);
                return;
            }

            string rawName = ui.MovieName ?? string.Empty;
            string rawYear = ui.ReleaseYear;

            var entries = ParseBulkMovieInput(rawName);

            if (entries.Count == 0)
            {
                SetOverallStatus("Please enter a movie name.", ItemStatus.Failed);
                RequestUiRefresh(preserveStatus: true);
                return;
            }

            if (entries.Count > MaxBulkEntries)
            {
                SetOverallStatus(
                    string.Format(
                        "Too many entries on one line ({0}); please add {1} or fewer at a time.",
                        entries.Count, MaxBulkEntries),
                    ItemStatus.Failed);
                RequestUiRefresh(preserveStatus: true);
                return;
            }

            // The standalone Year field only applies for a single, un-delimited entry.
            if (entries.Count == 1 && entries[0].Year == null)
                entries[0].Year = ParseYear(rawYear);

            ui.MovieName = string.Empty;
            ui.ReleaseYear = string.Empty;

            var addedEntries = new List<AddMovieEntry>(entries.Count);
            foreach (var item in entries)
                addedEntries.Add(AddMovieTracker.Add(item.Name, item.Year));

            RequestUiRefresh();

            for (int i = 0; i < addedEntries.Count; i++)
            {
                string id = addedEntries[i].Id;
                string name = entries[i].Name;
                int? year = entries[i].Year;
                Task.Run(() => RunSearchAsync(id, name, year), this.cts.Token);
            }
        }

        private void HandleAddManual()
        {
            var ui = UI;

            string rawName = ui.MovieName ?? string.Empty;
            string rawYear = ui.ReleaseYear;

            var entries = ParseBulkMovieInput(rawName);

            if (entries.Count == 0)
            {
                SetOverallStatus("Please enter a movie name.", ItemStatus.Failed);
                RequestUiRefresh(preserveStatus: true);
                return;
            }

            if (entries.Count > MaxBulkEntries)
            {
                SetOverallStatus(
                    string.Format(
                        "Too many entries on one line ({0}); please add {1} or fewer at a time.",
                        entries.Count, MaxBulkEntries),
                    ItemStatus.Failed);
                RequestUiRefresh(preserveStatus: true);
                return;
            }

            // The standalone Year field only applies for a single, un-delimited entry.
            if (entries.Count == 1 && entries[0].Year == null)
                entries[0].Year = ParseYear(rawYear);

            ui.MovieName = string.Empty;
            ui.ReleaseYear = string.Empty;

            foreach (var item in entries)
            {
                var entry = AddMovieTracker.AddManual(item.Name, item.Year);
                CheckDestinationForEntry(entry.Id);
            }

            RequestUiRefresh();
        }

        // -----------------------------------------------------------------------
        // Add All — enqueues selected Confident entries then fires AddMovieTask
        // -----------------------------------------------------------------------

        private void HandleAddAll()
        {
            // Re-check destinations for all Confident rows before transitioning.
            foreach (var e in AddMovieTracker.GetAllSorted()
                .Where(e => e.State == AddMovieState.Confident))
                CheckDestinationForEntry(e.Id);

            string targetPath = ConfigurationPageView.PathFromKey(
                this.plugin.Configuration.ComingSoonTargetKey);

            // Transition all currently-selected Confident entries into the queue
            // and record their destination path immediately so queued rows can
            // display it without waiting for the task to start.
            var toQueue = AddMovieTracker.GetAllSorted()
                .Where(e => e.State == AddMovieState.Confident && e.IncludedInBulkAdd)
                .ToArray();

            foreach (var e in toQueue)
            {
                AddMovieTracker.SetQueued(e.Id);
                if (!string.IsNullOrEmpty(targetPath))
                {
                    string folderName = EmbyLibrarySharedService.BuildComingSoonFolderName(
                        e.ConfirmedTitle, e.ConfirmedYear);
                    AddMovieTracker.RecordFolderPath(
                        e.Id, System.IO.Path.Combine(targetPath, folderName));
                }
            }

            // If there are queued entries (newly queued or surviving from a prior
            // navigation) fire the task.
            var queued = AddMovieTracker.GetAllSorted()
                .Where(e => e.State == AddMovieState.Queued)
                .ToArray();

            if (queued.Length == 0)
            {
                RequestUiRefresh();
                return;
            }

            RequestUiRefresh();

            // Fire the scheduled task — it will run serially through all Queued entries.
            // ITaskManager enforces that only one instance runs at a time.
            this.taskManager.Execute(
                this.taskManager.ScheduledTasks.FirstOrDefault(
                    t => t.ScheduledTask is AddMovieTask),
                new TaskOptions());
        }

        // -----------------------------------------------------------------------
        // Add one to library — transitions the single entry to Queued and fires
        // the task with a one-item queue, so single-add also survives navigation.
        // -----------------------------------------------------------------------

        private void HandleAddOne(string id)
        {
            var entry = AddMovieTracker.Get(id);
            if (entry == null) return;
            if (entry.State != AddMovieState.Confident
                && entry.State != AddMovieState.AddFailed)
                return;

            // Gate: re-check destination immediately before queuing
            CheckDestinationForEntry(id);
            entry = AddMovieTracker.Get(id);
            if (entry == null || entry.IsAddBlocked)
            {
                RequestUiRefresh();
                return;
            }

            string targetPath = ConfigurationPageView.PathFromKey(
                this.plugin.Configuration.ComingSoonTargetKey);

            if (string.IsNullOrEmpty(targetPath))
            {
                AddMovieTracker.SetAddFailed(id,
                    "No target library path configured. Please set one on the Configuration tab.");
                RequestUiRefresh();
                return;
            }

            AddMovieTracker.SetQueued(id);

            // Record destination path immediately so the queued row can display
            // it without waiting for the task to start.
            string folderName = EmbyLibrarySharedService.BuildComingSoonFolderName(
                entry.ConfirmedTitle, entry.ConfirmedYear);
            AddMovieTracker.RecordFolderPath(
                id, System.IO.Path.Combine(targetPath, folderName));

            RequestUiRefresh();

            this.taskManager.Execute(
                this.taskManager.ScheduledTasks.FirstOrDefault(
                    t => t.ScheduledTask is AddMovieTask),
                new TaskOptions());
        }

        // -----------------------------------------------------------------------
        // Remove
        // -----------------------------------------------------------------------

        private void HandleRemove(string id)
        {
            var entry = AddMovieTracker.Get(id);
            if (entry == null) return;

            if (entry.State == AddMovieState.Adding)
            {
                SetOverallStatus(
                    "Cannot remove a row while it is being added to the library.",
                    ItemStatus.Failed);
                RequestUiRefresh(preserveStatus: true);
                return;
            }

            expandedCandidates.Remove(id);
            AddMovieTracker.Remove(id);
            RequestUiRefresh();
        }

        private void HandleClearCompleted()
        {
            var completed = AddMovieTracker.GetAllSorted()
                .Where(e => e.State == AddMovieState.Added)
                .ToArray();

            foreach (var entry in completed)
            {
                expandedCandidates.Remove(entry.Id);
                AddMovieTracker.Remove(entry.Id);
            }

            RequestUiRefresh();
        }

        private void HandleToggleBulk(string id)
        {
            var entry = AddMovieTracker.Get(id);
            if (entry == null) return;

            // Toggling off: always allowed.
            if (entry.IncludedInBulkAdd)
            {
                AddMovieTracker.SetIncludedInBulkAdd(id, false);
                RequestUiRefresh();
                return;
            }

            // Toggling on: re-run the destination check first.
            // The user may have resolved the conflict externally since confirmation.
            CheckDestinationForEntry(id);
            entry = AddMovieTracker.Get(id);
            if (entry == null) return;

            if (entry.IsAddBlocked)
            {
                // Conflict still active — deny the toggle and tell the user why.
                // The status footer is the established feedback channel on this page.
                SetOverallStatus(
                    string.IsNullOrEmpty(entry.ConflictReason)
                        ? "Cannot select: destination conflict is still active."
                        : string.Format("Cannot select: {0}", entry.ConflictReason),
                    ItemStatus.Failed);
                RequestUiRefresh(preserveStatus: true);
                return;
            }

            AddMovieTracker.SetIncludedInBulkAdd(id, true);
            RequestUiRefresh();
        }

        // -----------------------------------------------------------------------
        // Manual confirm — bypasses TMDB entirely
        // -----------------------------------------------------------------------

        private void HandleManual(string id)
        {
            var entry = AddMovieTracker.Get(id);
            if (entry == null) return;

            this.logger.Debug("Manual confirm (no TMDB match) for '{0}'", entry.SearchName);

            AddMovieTracker.SetManualConfident(id);
            CheckDestinationForEntry(id);

            RequestUiRefresh();
        }

        // -----------------------------------------------------------------------
        // Retry
        // -----------------------------------------------------------------------

        private void HandleRetry(string id)
        {
            var entry = AddMovieTracker.Get(id);
            if (entry == null) return;

            if (entry.State == AddMovieState.AddFailed)
            {
                // Re-add to queue and fire the task for a single-item retry
                HandleAddOne(id);
                return;
            }

            // Re-search (NoResults / SearchFailed / MultipleMatches)
            AddMovieTracker.SetSearching(id);
            RequestUiRefresh();
            Task.Run(
                () => RunSearchAsync(id, entry.SearchName, entry.SearchYear),
                this.cts.Token);
        }

        // -----------------------------------------------------------------------
        // Select candidate
        // -----------------------------------------------------------------------

        private void HandleSelect(string commandId)
        {
            int lastUnderscore = commandId.LastIndexOf('_');
            if (lastUnderscore < 0) return;
            string indexStr = commandId.Substring(lastUnderscore + 1);
            string prefix = commandId.Substring(0, lastUnderscore);  // "Select_{id}"
            string id = prefix.Substring("Select_".Length);

            int index;
            if (!int.TryParse(indexStr, out index)) return;

            var entry = AddMovieTracker.Get(id);
            if (entry == null) return;
            if (index < 0 || index >= entry.Candidates.Count) return;

            var chosen = entry.Candidates[index];
            var match = new ManageComingSoon.Model.TmdbMovieResult
            {
                Id = chosen.TmdbId,
                Title = chosen.Title,
                Overview = chosen.Overview,
                ReleaseDate = chosen.ReleaseDate,
                PosterPath = chosen.PosterPath,
                Popularity = chosen.Popularity,
            };

            AddMovieTracker.SetConfident(id, match);
            CheckDestinationForEntry(id);

            RequestUiRefresh();
        }

        // -----------------------------------------------------------------------
        // Info — toggle cast expansion; fetch cast if not yet loaded
        // -----------------------------------------------------------------------

        private void HandleInfo(string commandId)
        {
            // commandId format: "Info_{id}_{candidateIndex}"
            int lastUnderscore = commandId.LastIndexOf('_');
            if (lastUnderscore < 0) return;
            string indexStr = commandId.Substring(lastUnderscore + 1);
            string prefix = commandId.Substring(0, lastUnderscore);
            string id = prefix.Substring("Info_".Length);

            int index;
            if (!int.TryParse(indexStr, out index)) return;

            var entry = AddMovieTracker.Get(id);
            if (entry == null) return;
            if (index < 0 || index >= entry.Candidates.Count) return;

            string infoKey = id + "_" + index;

            if (expandedInfo.Contains(infoKey))
            {
                expandedInfo.Remove(infoKey);
            }
            else
            {
                expandedInfo.Add(infoKey);
                var candidate = entry.Candidates[index];
                if (candidate.CastNames == null)
                {
                    Task.Run(
                        () => FetchCastAsync(id, index, candidate),
                        this.cts.Token);
                }
            }

            RequestUiRefresh();
        }

        private async Task FetchCastAsync(string entryId, int candidateIndex,
            AddMovieCandidate candidate)
        {
            if (this.cts.IsCancellationRequested) return;
            var cfg = this.plugin.Configuration;
            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey)) return;

            try
            {
                var cast = await this.tmdbService
                    .GetCastAsync(cfg.TmdbApiKey, candidate.TmdbId, this.cts.Token)
                    .ConfigureAwait(false);

                if (this.cts.IsCancellationRequested) return;

                AddMovieTracker.SetCandidateCast(entryId, candidateIndex, cast);
                RequestUiRefresh();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "FetchCastAsync failed for '{0}'", ex, candidate.Title);
            }
        }

        // -----------------------------------------------------------------------
        // TMDB search
        // -----------------------------------------------------------------------

        private async Task RunSearchAsync(string id, string name, int? year)
        {
            if (this.cts.IsCancellationRequested) return;
            var cfg = this.plugin.Configuration;

            if (string.IsNullOrWhiteSpace(cfg.TmdbApiKey))
            {
                AddMovieTracker.SetSearchFailed(id,
                    "TMDB API key not configured. Please set it on the Configuration tab.");
                RequestUiRefresh();
                return;
            }

            try
            {
                this.logger.Debug("Searching TMDB for '{0}' year={1}",
                    name, year.HasValue ? year.Value.ToString() : "none");

                var results = await this.tmdbService
                    .SearchAsync(cfg.TmdbApiKey, name, year, this.cts.Token)
                    .ConfigureAwait(false);

                if (this.cts.IsCancellationRequested) return;

                this.logger.Debug(
                    "TMDB search for '{0}' returned {1} result(s): {2}",
                    name, results.Count,
                    string.Join(" | ", results.Select(r =>
                        string.Format("{0} ({1}) [id={2}, pop={3}]",
                            r.Title, r.ReleaseYear, r.Id, r.Popularity))));

                if (results.Count == 0)
                    AddMovieTracker.SetNoResults(id);
                else if (this.tmdbService.IsConfidentMatch(results, name, year))
                {
                    AddMovieTracker.SetConfident(id, results[0]);
                    CheckDestinationForEntry(id);
                }
                else
                    AddMovieTracker.SetMultipleMatches(id, results);
            }
            catch (OperationCanceledException)
            {
                // Page was navigated away — write a terminal state so the entry
                // does not remain stuck in Searching. The user can re-search on return.
                AddMovieTracker.SetSearchFailed(id, "Search was cancelled — click Retry to search again.");
                RequestUiRefresh();
                return;
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("TMDB search failed for '{0}'", ex, name);
                AddMovieTracker.SetSearchFailed(id, string.Format("Search failed: {0}", ex.Message));
            }
            RequestUiRefresh();
        }
    }
}
