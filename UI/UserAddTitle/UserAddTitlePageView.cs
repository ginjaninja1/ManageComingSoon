namespace ManageComingSoon.UI.UserAddTitle
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
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
    using MediaBrowser.Model.Tasks;

    /// <summary>
    /// Rich ordinary-user page built exclusively around command responses.
    /// No RaiseUIViewInfoChanged, timers, task events, detached searches or
    /// server-push assumptions are used.
    /// </summary>
    internal sealed class UserAddTitlePageView : PluginPageView
    {
        private ComingSoonMediaType SelectedMediaType
            => string.Equals(UI.MediaType, "TvShow", StringComparison.OrdinalIgnoreCase)
                ? ComingSoonMediaType.TvShow : ComingSoonMediaType.Movie;

        private string GetComingSoonTargetKey(ComingSoonMediaType mediaType)
            => mediaType == ComingSoonMediaType.TvShow
                ? this.plugin.Configuration.TvComingSoonTargetKey
                : this.plugin.Configuration.MovieComingSoonTargetKey;

        private const int MaxBulkEntries = 25;
        private const int MaxDefaultCandidates = 3;
        private const int MaxExpandedCandidates = 10;
        // A TV add has two independently bounded ingest checks (Series, then
        // Episode) before its metadata refresh completes.  Keep the command
        // response open for that work: ordinary-user pages cannot receive the
        // admin-only push refresh used elsewhere in the plugin.
        private static readonly TimeSpan TvCompletionWaitPerEntry =
            TimeSpan.FromSeconds(145);

        private readonly ManageComingSoonPlugin plugin;
        private readonly TmdbService tmdbService;
        private readonly EmbyLibraryAddService libraryService;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;
        private readonly HashSet<string> expandedCandidates =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> expandedInfo =
            new HashSet<string>(StringComparer.Ordinal);
        private bool interactionStatusSet;

        // Formats the current session's username for log-line prefixing.
        // this.User is populated by the SDK per-request/session and is not
        // guaranteed non-null at every call site, so this always falls back
        // to a safe placeholder rather than risking a NullReferenceException
        // from inside a log call.
        private string UserTag() => "[" + (this.User?.Name ?? "unknown") + "]";

        public UserAddTitlePageView(
            PluginInfo pluginInfo,
            ManageComingSoonPlugin plugin,
            TmdbService tmdbService,
            EmbyLibraryAddService libraryService,
            ITaskManager taskManager,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.plugin = plugin;
            this.tmdbService = tmdbService;
            this.libraryService = libraryService;
            this.taskManager = taskManager;
            this.logger = logger;
            this.ContentData = new UserAddTitleUI();
            this.ShowSave = false;
            RebuildPage();
        }

        private UserAddTitleUI UI => (UserAddTitleUI)this.ContentData;

        public override async Task<IPluginUIView> RunCommand(
            string itemId,
            string commandId,
            string data)
        {
            this.interactionStatusSet = false;
            try
            {
                if (commandId == "AddViaTmdb")
                    await HandleAddViaTmdbAsync().ConfigureAwait(false);
                else if (commandId == "AddManual")
                    HandleAddManual();
                else if (commandId == "AddAll")
                    await HandleAddAllAsync().ConfigureAwait(false);
                else if (commandId == "RefreshStatus")
                    SyncSubmittedStates();
                else if (commandId == "ClearCompleted")
                    UserAddTitleTracker.ClearCompleted();
                else if (commandId != null && commandId.StartsWith("Remove_", StringComparison.Ordinal))
                    HandleRemove(commandId.Substring(7));
                else if (commandId != null && commandId.StartsWith("ToggleBulk_", StringComparison.Ordinal))
                    HandleToggleBulk(commandId.Substring(11));
                else if (commandId != null && commandId.StartsWith("Manual_", StringComparison.Ordinal))
                    HandleManual(commandId.Substring(7));
                else if (commandId != null && commandId.StartsWith("RetrySearch_", StringComparison.Ordinal))
                    await HandleRetrySearchAsync(commandId.Substring(12)).ConfigureAwait(false);
                else if (commandId != null && commandId.StartsWith("RetryAdd_", StringComparison.Ordinal))
                    await HandleRetryAddAsync(commandId.Substring(9)).ConfigureAwait(false);
                else if (commandId != null && commandId.StartsWith("Select_", StringComparison.Ordinal))
                    HandleSelect(commandId);
                else if (commandId != null && commandId.StartsWith("Submit_", StringComparison.Ordinal))
                    await HandleSubmitOneAsync(commandId.Substring(7)).ConfigureAwait(false);
                else if (commandId != null && commandId.StartsWith("ShowMore_", StringComparison.Ordinal))
                    this.expandedCandidates.Add(commandId.Substring(9));
                else if (commandId != null && commandId.StartsWith("ShowLess_", StringComparison.Ordinal))
                    this.expandedCandidates.Remove(commandId.Substring(9));
                else if (commandId != null && commandId.StartsWith("Info_", StringComparison.Ordinal))
                    await HandleInfoAsync(commandId).ConfigureAwait(false);
                else
                    return await base.RunCommand(itemId, commandId, data).ConfigureAwait(false);

                RebuildPage(preserveStatus: this.interactionStatusSet);
                return this;
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("[UserAddTitle]" + UserTag() + " User Add Title command failed", ex);
                SetStatus("The request failed: " + ex.Message, ItemStatus.Failed);
                RebuildPage(preserveStatus: true);
                return this;
            }
        }

        private async Task HandleAddViaTmdbAsync()
        {
            var parsed = ParseBulkTitleInput(UI.TitleName, UI.ReleaseYear);
            if (parsed.Count == 0)
            {
                SetStatus("Please enter a title.", ItemStatus.Failed);
                return;
            }
            if (parsed.Count > MaxBulkEntries)
            {
                SetStatus(string.Format("Please add {0} or fewer titles at a time.", MaxBulkEntries), ItemStatus.Failed);
                return;
            }
            if (string.IsNullOrWhiteSpace(this.plugin.Configuration.TmdbApiKey))
            {
                SetStatus("TMDB matching is unavailable because the administrator has not configured an API key. Manual submission is still available.", ItemStatus.Warning);
                return;
            }

            UI.TitleName = string.Empty;
            UI.ReleaseYear = string.Empty;

            var work = new List<Tuple<UserTitleEntry, BulkTitleEntry>>();
            foreach (var item in parsed)
                work.Add(Tuple.Create(UserAddTitleTracker.Add(item.Name, item.Year, SelectedMediaType), item));

            var searches = work.Select(pair => SearchEntryAsync(pair.Item1, pair.Item2.Name, pair.Item2.Year));
            await Task.WhenAll(searches).ConfigureAwait(false);
        }

        private void HandleAddManual()
        {
            var parsed = ParseBulkTitleInput(UI.TitleName, UI.ReleaseYear);
            if (parsed.Count == 0)
            {
                SetStatus("Please enter a title.", ItemStatus.Failed);
                return;
            }
            if (parsed.Count > MaxBulkEntries)
            {
                SetStatus(string.Format("Please add {0} or fewer titles at a time.", MaxBulkEntries), ItemStatus.Failed);
                return;
            }

            foreach (var item in parsed)
            {
                var entry = UserAddTitleTracker.AddManual(item.Name, item.Year, SelectedMediaType);
                CheckDestination(entry);
            }

            UI.TitleName = string.Empty;
            UI.ReleaseYear = string.Empty;
        }

        private async Task SearchEntryAsync(UserTitleEntry entry, string name, int? year)
        {
            try
            {
                var results = await this.tmdbService.SearchAsync(
                    this.plugin.Configuration.TmdbApiKey,
                    name,
                    year,
                    entry.MediaType,
                    CancellationToken.None).ConfigureAwait(false);

                entry.Candidates = results.Take(MaxExpandedCandidates)
                    .Select(r => new UserTitleCandidate { TitleResult = r })
                    .ToList();

                if (entry.Candidates.Count == 0)
                {
                    entry.State = UserTitleState.NoResults;
                    return;
                }

                if (this.tmdbService.IsConfidentMatch(results, name, year))
                {
                    entry.SelectedMatch = results[0];
                    entry.State = UserTitleState.Ready;
                    entry.IncludedInBulkAdd = true;
                    CheckDestination(entry);
                }
                else
                {
                    entry.State = UserTitleState.MultipleMatches;
                    entry.IncludedInBulkAdd = false;
                }
            }
            catch (Exception ex)
            {
                entry.State = UserTitleState.SearchFailed;
                entry.ErrorMessage = ex.Message;
                this.logger.ErrorException("[UserAddTitle]" + UserTag() + " TMDB search failed for '{0}'", ex, name);
            }
        }

        private async Task HandleRetrySearchAsync(string id)
        {
            var entry = UserAddTitleTracker.Get(id);
            if (entry == null) return;
            entry.State = UserTitleState.Searching;
            entry.ErrorMessage = null;
            entry.Candidates.Clear();
            entry.SelectedMatch = null;
            await SearchEntryAsync(entry, entry.SearchName, entry.SearchYear).ConfigureAwait(false);
        }

        private void HandleManual(string id)
        {
            var entry = UserAddTitleTracker.Get(id);
            if (entry == null) return;
            entry.IsManual = true;
            entry.SelectedMatch = null;
            entry.State = UserTitleState.Ready;
            entry.IncludedInBulkAdd = true;
            entry.ErrorMessage = null;
            CheckDestination(entry);
        }

        private void HandleSelect(string commandId)
        {
            int last = commandId.LastIndexOf('_');
            if (last < 0) return;
            int index;
            if (!int.TryParse(commandId.Substring(last + 1), out index)) return;
            string id = commandId.Substring("Select_".Length, last - "Select_".Length);
            var entry = UserAddTitleTracker.Get(id);
            if (entry == null || index < 0 || index >= entry.Candidates.Count) return;
            entry.SelectedMatch = entry.Candidates[index].TitleResult;
            entry.IsManual = false;
            entry.State = UserTitleState.Ready;
            entry.IncludedInBulkAdd = true;
            entry.ErrorMessage = null;
            CheckDestination(entry);
        }

        private async Task HandleInfoAsync(string commandId)
        {
            int last = commandId.LastIndexOf('_');
            if (last < 0) return;
            int index;
            if (!int.TryParse(commandId.Substring(last + 1), out index)) return;
            string id = commandId.Substring("Info_".Length, last - "Info_".Length);
            var entry = UserAddTitleTracker.Get(id);
            if (entry == null || index < 0 || index >= entry.Candidates.Count) return;
            string key = id + "_" + index;
            if (this.expandedInfo.Contains(key))
            {
                this.expandedInfo.Remove(key);
                return;
            }
            this.expandedInfo.Add(key);
            var candidate = entry.Candidates[index];
            if (candidate.CastNames == null)
            {
                try
                {
                    candidate.CastNames = await this.tmdbService.GetCastAsync(
                        this.plugin.Configuration.TmdbApiKey,
                        candidate.TitleResult.Id,
                        entry.MediaType,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    candidate.CastNames = new List<string>();
                    this.logger.ErrorException("[UserAddTitle]" + UserTag() + " TMDB cast lookup failed for '{0}'", ex, candidate.TitleResult.Title);
                }
            }
        }

        private void HandleToggleBulk(string id)
        {
            var entry = UserAddTitleTracker.Get(id);
            if (entry == null || entry.State != UserTitleState.Ready) return;
            if (!entry.IncludedInBulkAdd)
            {
                CheckDestination(entry);
                if (entry.HasDestinationConflict)
                {
                    return;
                }
            }
            entry.IncludedInBulkAdd = !entry.IncludedInBulkAdd;
        }

        private async Task HandleSubmitOneAsync(string id)
        {
            var entry = UserAddTitleTracker.Get(id);
            if (entry == null) return;
            var queued = PrepareSubmission(new[] { entry });
            if (queued.Count == 0) return;
            StartAddTask();
            await WaitForCommandCompletionAsync(
                queued,
                GetCommandCompletionWait(queued, TimeSpan.FromSeconds(4)))
                .ConfigureAwait(false);
            SyncSubmittedStates();
        }

        private async Task HandleAddAllAsync()
        {
            var selected = UserAddTitleTracker.GetAll()
                .Where(e => e.State == UserTitleState.Ready && e.IncludedInBulkAdd)
                .ToArray();
            if (selected.Length == 0)
            {
                SetStatus("No matched or manual titles are selected.", ItemStatus.Warning);
                return;
            }

            var queued = PrepareSubmission(selected);
            if (queued.Count == 0) return;
            StartAddTask();
            await WaitForCommandCompletionAsync(
                queued,
                GetCommandCompletionWait(
                    queued,
                    queued.Count == 1 ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);
            SyncSubmittedStates();
        }

        private List<UserTitleEntry> PrepareSubmission(IEnumerable<UserTitleEntry> entries)
        {
            var queued = new List<UserTitleEntry>();
            foreach (var entry in entries)
            {
                string targetPath = ConfigurationPageView.PathFromKey(
                    GetComingSoonTargetKey(entry.MediaType));
                if (string.IsNullOrWhiteSpace(targetPath)) continue;
                CheckDestination(entry);
                if (entry.HasDestinationConflict) continue;

                AddTitleEntry global;
                if (entry.IsManual)
                {
                    global = AddTitleTracker.AddManual(entry.SearchName, entry.SearchYear, entry.MediaType);
                }
                else
                {
                    if (entry.SelectedMatch == null) continue;
                    global = AddTitleTracker.Add(entry.SearchName, entry.SearchYear, entry.MediaType);
                    AddTitleTracker.SetConfident(global.Id, entry.SelectedMatch);
                }

                string folderName = EmbyLibrarySharedService.BuildComingSoonFolderName(
                    global.ConfirmedTitle,
                    global.ConfirmedYear);
                string destination = Path.Combine(targetPath, folderName);

                AddTitleTracker.SetQueued(global.Id);
                AddTitleTracker.RecordFolderPath(global.Id, destination);

                entry.GlobalTrackerId = global.Id;
                entry.DestinationPath = destination;
                entry.State = UserTitleState.Submitted;
                entry.SubmittedAt = DateTime.UtcNow;
                entry.IncludedInBulkAdd = false;
                queued.Add(entry);
            }

            return queued;
        }

        private void StartAddTask()
        {
            var worker = this.taskManager.ScheduledTasks
                .FirstOrDefault(t => t.ScheduledTask is AddTitleTask);
            if (worker == null)
                throw new InvalidOperationException("The title addition task was not found.");
            this.taskManager.Execute(worker, new TaskOptions());
        }

        private static TimeSpan GetCommandCompletionWait(
            IList<UserTitleEntry> entries,
            TimeSpan movieWait)
        {
            int tvCount = entries.Count(e => e.MediaType == ComingSoonMediaType.TvShow);
            if (tvCount == 0) return movieWait;

            // AddTitleTask processes entries serially. Allow every TV entry to
            // reach its real terminal state, plus the task's five-second gap
            // between consecutive entries. Titles retain their deliberately
            // short fast-path response behaviour.
            return TimeSpan.FromTicks(
                (TvCompletionWaitPerEntry.Ticks * tvCount) +
                (TimeSpan.FromSeconds(5).Ticks * Math.Max(0, entries.Count - 1)));
        }

        private async Task WaitForCommandCompletionAsync(
            IList<UserTitleEntry> entries,
            TimeSpan maximumWait)
        {
            DateTime stopAt = DateTime.UtcNow.Add(maximumWait);
            while (DateTime.UtcNow < stopAt)
            {
                SyncSubmittedStates();
                if (entries.All(e => e.State == UserTitleState.Added || e.State == UserTitleState.AddFailed))
                    return;
                await Task.Delay(150).ConfigureAwait(false);
            }
        }

        private async Task HandleRetryAddAsync(string id)
        {
            var local = UserAddTitleTracker.Get(id);
            if (local == null || string.IsNullOrEmpty(local.GlobalTrackerId)) return;
            var global = AddTitleTracker.Get(local.GlobalTrackerId);
            if (global == null)
            {
                local.State = UserTitleState.AddFailed;
                local.ErrorMessage = "The original server queue entry no longer exists.";
                return;
            }
            AddTitleTracker.SetQueued(global.Id);
            local.State = UserTitleState.Submitted;
            local.ErrorMessage = null;
            StartAddTask();
            var retried = new[] { local };
            await WaitForCommandCompletionAsync(
                retried,
                GetCommandCompletionWait(retried, TimeSpan.FromSeconds(4)))
                .ConfigureAwait(false);
            SyncSubmittedStates();
        }

        private void SyncSubmittedStates()
        {
            foreach (var local in UserAddTitleTracker.GetAll()
                .Where(e => !string.IsNullOrEmpty(e.GlobalTrackerId)))
            {
                var global = AddTitleTracker.Get(local.GlobalTrackerId);
                if (global == null) continue;
                local.DestinationPath = global.DisplayFolderPath;
                switch (global.State)
                {
                    case AddTitleState.Queued:
                        local.State = UserTitleState.Submitted;
                        break;
                    case AddTitleState.Adding:
                        local.State = UserTitleState.Adding;
                        break;
                    case AddTitleState.Added:
                        local.State = UserTitleState.Added;
                        local.CompletedAt = global.CompletedAt;
                        local.ErrorMessage = null;
                        break;
                    case AddTitleState.AddFailed:
                        local.State = UserTitleState.AddFailed;
                        local.ErrorMessage = global.ErrorMessage;
                        break;
                }
            }
        }

        private void HandleRemove(string id)
        {
            var entry = UserAddTitleTracker.Get(id);
            if (entry == null) return;
            if (entry.State == UserTitleState.Adding)
            {
                SetStatus("This " + entry.MediaType.DisplayName().ToLowerInvariant() +
                    " cannot be removed while the server is adding it.", ItemStatus.Warning);
                return;
            }
            this.expandedCandidates.Remove(id);
            UserAddTitleTracker.Remove(id);
        }

        private void CheckDestination(UserTitleEntry entry)
        {
            entry.HasDestinationConflict = false;
            entry.ConflictReason = null;
            string targetPath = ConfigurationPageView.PathFromKey(
                GetComingSoonTargetKey(entry.MediaType));
            if (string.IsNullOrWhiteSpace(targetPath)) return;

            string folderName = EmbyLibrarySharedService.BuildComingSoonFolderName(
                entry.DisplayTitle,
                entry.DisplayYear);
            string destination = Path.Combine(targetPath, folderName);
            entry.DestinationPath = destination;

            var duplicate = UserAddTitleTracker.GetAll()
                .FirstOrDefault(other => other.Id != entry.Id
                    && other.State != UserTitleState.AddFailed
                    && string.Equals(other.DestinationPath, destination, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
            {
                entry.HasDestinationConflict = true;
                entry.ConflictReason = "Already present in your request list.";
                entry.IncludedInBulkAdd = false;
                return;
            }

            // TMDBID library-wide check — catches cases the folder-name-based
            // check below cannot: the same title already Coming Soon under a
            // different folder name, or already available in the main
            // library outside the Coming Soon workflow.
            int tmdbId = entry.SelectedMatch != null ? entry.SelectedMatch.Id : 0;
            var tmdbConflict = this.libraryService.CheckTmdbLibraryConflict(tmdbId, entry.MediaType);
            if (tmdbConflict.Kind != EmbyLibrarySharedService.TmdbConflictKind.None)
            {
                entry.HasDestinationConflict = true;
                entry.ConflictReason = tmdbConflict.Reason;
                entry.IncludedInBulkAdd = false;
                return;
            }

            try
            {
                if (Directory.Exists(destination))
                {
                    entry.HasDestinationConflict = true;
                    entry.ConflictReason = entry.MediaType.DisplayName() + " already coming soon";
                    entry.IncludedInBulkAdd = false;
                }
            }
            catch (Exception ex)
            {
                entry.HasDestinationConflict = true;
                entry.ConflictReason = "The destination could not be checked: " + ex.Message;
                entry.IncludedInBulkAdd = false;
            }
        }

        private void RebuildPage(bool preserveStatus = false)
        {
            SyncSubmittedStates();
            RefreshButtonState();
            BuildActiveList();
            BuildCompletedList();
            if (!preserveStatus) UpdateOverallStatus();
        }

        private void RefreshButtonState()
        {
            bool hasKey = !string.IsNullOrWhiteSpace(this.plugin.Configuration.TmdbApiKey);
            UI.AddViaTmdbButton = new ButtonItem(hasKey
                ? "Add via TMDB Match"
                : "TMDB matching unavailable")
            {
                Icon = IconNames.search,
                Data1 = "AddViaTmdb",
                CommandId = "AddViaTmdb",
                IsEnabled = hasKey,
            };
        }

        private void BuildActiveList()
        {
            var entries = UserAddTitleTracker.GetAll()
                .Where(e => e.State != UserTitleState.Added && e.State != UserTitleState.AddFailed)
                .ToArray();
            var list = new GenericItemList();
            int selected = entries.Count(e => e.State == UserTitleState.Ready && e.IncludedInBulkAdd && !e.HasDestinationConflict);
            list.Add(new GenericListItem(IconNames.add_circle,
                selected > 0 ? string.Format("Add all selected ({0})", selected) : "Add all selected",
                "Matched and manual entries can be submitted together.")
            {
                IconMode = ItemListIconMode.SmallRegular,
                Status = selected > 0 ? ItemStatus.Succeeded : ItemStatus.Unavailable,
                Button1 = new ButtonItem("Add All")
                {
                    Icon = IconNames.add_circle,
                    Data1 = "AddAll",
                    CommandId = "AddAll",
                    IsEnabled = selected > 0
                },
                SubItems = entries.Select(BuildEntryRow).ToList()
            });
            UI.ActiveList = list;
        }

        private void BuildCompletedList()
        {
            var completed = UserAddTitleTracker.GetAll()
                .Where(e => e.State == UserTitleState.Added || e.State == UserTitleState.AddFailed)
                .ToArray();
            var list = new GenericItemList();
            list.Add(new GenericListItem(IconNames.done_all, "Completed and failed", string.Empty)
            {
                IconMode = ItemListIconMode.SmallRegular,
                Status = ItemStatus.Unavailable,
                Button1 = new ButtonItem("Clear Completed")
                {
                    StandardIcon = StandardIcons.Remove,
                    Data1 = "ClearCompleted",
                    CommandId = "ClearCompleted",
                    IsEnabled = completed.Length > 0
                },
                SubItems = completed.Select(BuildEntryRow).ToList()
            });
            UI.CompletedList = list;
        }

        private GenericListItem BuildEntryRow(UserTitleEntry entry)
        {
            var row = new GenericListItem
            {
                PrimaryText = entry.DisplayYear > 0
                    ? string.Format("[{0}] {1} ({2})", entry.MediaType.DisplayName(), entry.DisplayTitle, entry.DisplayYear)
                    : string.Format("[{0}] {1}", entry.MediaType.DisplayName(), entry.DisplayTitle),
                SecondaryText = BuildSecondaryText(entry),
                IconMode = ItemListIconMode.SmallRegular,
                Icon = StateIcon(entry.State),
                Status = StateStatus(entry)
            };

            row.Button1 = BuildPrimaryButton(entry);
            row.Button2 = new ButtonItem(entry.State == UserTitleState.Added ? "Clear" : "Remove")
            {
                StandardIcon = StandardIcons.Remove,
                Data1 = "Remove_" + entry.Id,
                CommandId = "Remove_" + entry.Id,
                IsEnabled = entry.State != UserTitleState.Adding
            };

            if (entry.State == UserTitleState.Ready)
            {
                row.Toggle = new ToggleButtonItem("Select")
                {
                    IsChecked = entry.IncludedInBulkAdd,
                    Data1 = "ToggleBulk_" + entry.Id,
                    CommandId = "ToggleBulk_" + entry.Id,
                    IsEnabled = !entry.HasDestinationConflict
                };
            }

            if (entry.State == UserTitleState.MultipleMatches)
                row.SubItems = BuildCandidateRows(entry);
            return row;
        }

        private ButtonItem BuildPrimaryButton(UserTitleEntry entry)
        {
            switch (entry.State)
            {
                case UserTitleState.Ready:
                    return new ButtonItem("Add to Library")
                    {
                        Icon = IconNames.add_circle,
                        Data1 = "Submit_" + entry.Id,
                        CommandId = "Submit_" + entry.Id,
                        IsEnabled = !entry.HasDestinationConflict
                    };
                case UserTitleState.NoResults:
                case UserTitleState.SearchFailed:
                    return new ButtonItem("Manual")
                    {
                        Icon = IconNames.add_circle,
                        Data1 = "Manual_" + entry.Id,
                        CommandId = "Manual_" + entry.Id
                    };
                case UserTitleState.AddFailed:
                    return new ButtonItem("Retry")
                    {
                        StandardIcon = StandardIcons.Refresh,
                        Data1 = "RetryAdd_" + entry.Id,
                        CommandId = "RetryAdd_" + entry.Id
                    };
                default:
                    return null;
            }
        }

        private List<GenericListItem> BuildCandidateRows(UserTitleEntry entry)
        {
            var rows = new List<GenericListItem>();
            bool expanded = this.expandedCandidates.Contains(entry.Id);
            int count = Math.Min(entry.Candidates.Count,
                expanded ? MaxExpandedCandidates : MaxDefaultCandidates);
            for (int i = 0; i < count; i++)
            {
                var candidate = entry.Candidates[i];
                string key = entry.Id + "_" + i;
                bool infoOpen = this.expandedInfo.Contains(key);
                var row = new GenericListItem(
                    IconNames.movie,
                    candidate.TitleResult.ReleaseYear > 0
                        ? string.Format("{0} ({1})", candidate.TitleResult.Title, candidate.TitleResult.ReleaseYear)
                        : candidate.TitleResult.Title,
                    Truncate(candidate.TitleResult.Overview, 140))
                {
                    IconMode = ItemListIconMode.SmallRegular,
                    Button1 = new ButtonItem(infoOpen ? "Hide Info" : "Info")
                    {
                        Icon = IconNames.info,
                        Data1 = string.Format("Info_{0}_{1}", entry.Id, i),
                        CommandId = string.Format("Info_{0}_{1}", entry.Id, i)
                    },
                    Button2 = new ButtonItem("Select")
                    {
                        Icon = IconNames.check_circle,
                        Data1 = string.Format("Select_{0}_{1}", entry.Id, i),
                        CommandId = string.Format("Select_{0}_{1}", entry.Id, i)
                    }
                };
                if (infoOpen) row.SubItems = BuildInfoRows(candidate);
                rows.Add(row);
            }

            if (entry.Candidates.Count > MaxDefaultCandidates)
            {
                rows.Add(new GenericListItem(IconNames.search,
                    expanded ? "Showing all results" : string.Format("{0} more result(s)", entry.Candidates.Count - MaxDefaultCandidates),
                    string.Empty)
                {
                    IconMode = ItemListIconMode.SmallRegular,
                    Button1 = new ButtonItem(expanded ? "Show Less" : "Show More")
                    {
                        Icon = expanded ? IconNames.expand_less : IconNames.expand_more,
                        Data1 = (expanded ? "ShowLess_" : "ShowMore_") + entry.Id,
                        CommandId = (expanded ? "ShowLess_" : "ShowMore_") + entry.Id
                    }
                });
            }
            return rows;
        }

        private static List<GenericListItem> BuildInfoRows(UserTitleCandidate candidate)
        {
            var rows = new List<GenericListItem>();
            string url = "https://www.themoviedb.org/" + candidate.TitleResult.MediaType.TmdbPathSegment() + "/" +
                candidate.TitleResult.Id.ToString(CultureInfo.InvariantCulture);
            rows.Add(new GenericListItem(IconNames.open_in_new, "View on TMDB", url)
            {
                IconMode = ItemListIconMode.SmallRegular,
                HyperLink = url,
                HyperLinkTargetExternal = true
            });
            if (candidate.CastNames == null || candidate.CastNames.Count == 0)
                rows.Add(new GenericListItem(IconNames.person, "No cast information available", string.Empty)
                { IconMode = ItemListIconMode.SmallRegular });
            else
                rows.AddRange(candidate.CastNames.Select(name => new GenericListItem(IconNames.person, name, string.Empty)
                { IconMode = ItemListIconMode.SmallRegular }));
            return rows;
        }

        private static string BuildSecondaryText(UserTitleEntry entry)
        {
            if (entry.HasDestinationConflict) return entry.ConflictReason;
            switch (entry.State)
            {
                case UserTitleState.Searching: return "Searching TMDB...";
                case UserTitleState.MultipleMatches: return "Multiple matches found — select the correct result.";
                case UserTitleState.Ready:
                    if (entry.IsManual) return "Manual entry — ready to submit.";
                    return string.IsNullOrWhiteSpace(entry.SelectedMatch == null ? null : entry.SelectedMatch.Overview)
                        ? "TMDB match ready to submit."
                        : Truncate(entry.SelectedMatch.Overview, 140);
                case UserTitleState.NoResults: return "No TMDB result — use Manual or remove this entry.";
                case UserTitleState.SearchFailed: return "TMDB search failed: " + Truncate(entry.ErrorMessage, 110);
                case UserTitleState.Submitted: return "Submitted to the server. Use Refresh Status if this remains pending.";
                case UserTitleState.Adding: return "The server is adding this " + entry.MediaType.DisplayName().ToLowerInvariant() + ".";
                case UserTitleState.Added: return string.IsNullOrEmpty(entry.DestinationPath) ? "Added successfully." : "Added: " + entry.DestinationPath;
                case UserTitleState.AddFailed: return "Add failed: " + Truncate(entry.ErrorMessage, 110);
                default: return string.Empty;
            }
        }

        private static IconNames StateIcon(UserTitleState state)
        {
            switch (state)
            {
                case UserTitleState.Submitted: return IconNames.hourglass_empty;
                case UserTitleState.Added: return IconNames.check_circle;
                default: return IconNames.video_library;
            }
        }

        private static ItemStatus StateStatus(UserTitleEntry entry)
        {
            if (entry.HasDestinationConflict) return ItemStatus.Failed;
            switch (entry.State)
            {
                case UserTitleState.Searching:
                case UserTitleState.Submitted:
                case UserTitleState.Adding: return ItemStatus.InProgress;
                case UserTitleState.Ready:
                case UserTitleState.Added: return ItemStatus.Succeeded;
                case UserTitleState.MultipleMatches: return ItemStatus.Warning;
                case UserTitleState.NoResults:
                case UserTitleState.SearchFailed:
                case UserTitleState.AddFailed: return ItemStatus.Failed;
                default: return ItemStatus.Unavailable;
            }
        }

        private void UpdateOverallStatus()
        {
            UI.OverallStatus.StatusText = string.Empty;
            UI.OverallStatus.Status = ItemStatus.Unavailable;
        }

        private void SetStatus(string text, ItemStatus status)
        {
            this.interactionStatusSet = true;
            UI.OverallStatus.StatusText = text ?? string.Empty;
            UI.OverallStatus.Status = status;
        }

        private sealed class BulkTitleEntry
        {
            public string Name;
            public int? Year;
        }

        private static List<BulkTitleEntry> ParseBulkTitleInput(string raw, string standaloneYear)
        {
            var result = new List<BulkTitleEntry>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            foreach (string segment in raw.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(segment)) continue;
                string name = segment;
                string yearText = null;
                int semi = segment.IndexOf(';');
                if (semi >= 0)
                {
                    name = segment.Substring(0, semi);
                    yearText = segment.Substring(semi + 1);
                }
                name = name.Trim();
                if (name.Length == 0) continue;
                result.Add(new BulkTitleEntry { Name = name, Year = ParseYear(yearText) });
            }
            if (result.Count == 1 && result[0].Year == null)
                result[0].Year = ParseYear(standaloneYear);
            return result;
        }

        private static int? ParseYear(string value)
        {
            int year;
            if (!string.IsNullOrWhiteSpace(value)
                && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out year)
                && year > 1800 && year < 2200)
                return year;
            return null;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, max - 3) + "...";
        }
    }
}
