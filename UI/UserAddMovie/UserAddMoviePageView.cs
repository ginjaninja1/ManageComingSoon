namespace ManageComingSoon.UI.UserAddMovie
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
    /// Stand-alone ordinary-user page.
    ///
    /// Important design rule:
    /// - no RaiseUIViewInfoChanged
    /// - no tracker/task event subscriptions
    /// - no timers or polling
    /// - no detached Task.Run work
    ///
    /// Every command completes its own work, rebuilds ContentData, and returns
    /// this view directly to the calling browser.
    /// </summary>
    internal sealed class UserAddMoviePageView : PluginPageView
    {
        private const int MaxResults = 10;

        private readonly ManageComingSoonPlugin plugin;
        private readonly TmdbService tmdbService;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;

        private List<TmdbMovieResult> currentResults = new List<TmdbMovieResult>();

        public UserAddMoviePageView(
            PluginInfo pluginInfo,
            ManageComingSoonPlugin plugin,
            TmdbService tmdbService,
            ITaskManager taskManager,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.plugin = plugin;
            this.tmdbService = tmdbService;
            this.taskManager = taskManager;
            this.logger = logger;

            this.ContentData = new UserAddMovieUI();
            this.ShowSave = false;

            RebuildResults();
        }

        private UserAddMovieUI UI => (UserAddMovieUI)this.ContentData;

        public override async Task<IPluginUIView> RunCommand(
            string itemId,
            string commandId,
            string data)
        {
            try
            {
                if (commandId == "Search")
                {
                    await SearchAsync().ConfigureAwait(false);
                    return this;
                }

                if (commandId == "SubmitManual")
                {
                    SubmitManual();
                    return this;
                }

                if (commandId != null
                    && commandId.StartsWith("SubmitMatch_", StringComparison.Ordinal))
                {
                    SubmitMatched(commandId.Substring("SubmitMatch_".Length));
                    return this;
                }

                if (commandId == "Clear")
                {
                    ResetForm();
                    SetStatus(string.Empty, ItemStatus.Unavailable);
                    RebuildResults();
                    return this;
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("User Add Movie command failed", ex);
                SetStatus("The request failed: " + ex.Message, ItemStatus.Failed);
                RebuildResults();
                return this;
            }

            return await base.RunCommand(itemId, commandId, data).ConfigureAwait(false);
        }

        private async Task SearchAsync()
        {
            string name = (UI.MovieName ?? string.Empty).Trim();
            int? year = ParseYear(UI.ReleaseYear);

            if (string.IsNullOrEmpty(name))
            {
                this.currentResults.Clear();
                SetStatus("Enter a movie title first.", ItemStatus.Failed);
                RebuildResults();
                return;
            }

            string apiKey = this.plugin.Configuration.TmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                this.currentResults.Clear();
                SetStatus(
                    "TMDB matching is unavailable because the administrator has not configured an API key. You can still submit the title manually.",
                    ItemStatus.Warning);
                RebuildResults();
                return;
            }

            SetStatus("Searching TMDB...", ItemStatus.InProgress);

            var results = await this.tmdbService
                .SearchAsync(apiKey, name, year, CancellationToken.None)
                .ConfigureAwait(false);

            this.currentResults = results
                .Take(MaxResults)
                .ToList();

            if (this.currentResults.Count == 0)
            {
                SetStatus(
                    "No TMDB match was found. Check the title/year or submit it without matching.",
                    ItemStatus.Warning);
            }
            else
            {
                SetStatus(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Select the correct match below ({0} result{1}).",
                        this.currentResults.Count,
                        this.currentResults.Count == 1 ? string.Empty : "s"),
                    ItemStatus.Succeeded);
            }

            RebuildResults();
        }

        private void SubmitManual()
        {
            string name = (UI.MovieName ?? string.Empty).Trim();
            int? year = ParseYear(UI.ReleaseYear);

            if (string.IsNullOrEmpty(name))
            {
                SetStatus("Enter a movie title first.", ItemStatus.Failed);
                RebuildResults();
                return;
            }

            var entry = AddMovieTracker.AddManual(name, year);
            SubmitEntry(entry.Id);
        }

        private void SubmitMatched(string tmdbIdText)
        {
            int tmdbId;
            if (!int.TryParse(tmdbIdText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out tmdbId))
            {
                SetStatus("The selected TMDB result was invalid.", ItemStatus.Failed);
                RebuildResults();
                return;
            }

            var selected = this.currentResults.FirstOrDefault(r => r.Id == tmdbId);
            if (selected == null)
            {
                SetStatus("That result is no longer available. Search again.", ItemStatus.Failed);
                RebuildResults();
                return;
            }

            var entry = AddMovieTracker.Add(selected.Title, selected.ReleaseYear);
            AddMovieTracker.SetConfident(entry.Id, selected);
            SubmitEntry(entry.Id);
        }

        private void SubmitEntry(string entryId)
        {
            var entry = AddMovieTracker.Get(entryId);
            if (entry == null)
            {
                SetStatus("The movie could not be prepared for submission.", ItemStatus.Failed);
                RebuildResults();
                return;
            }

            string targetPath = ConfigurationPageView.PathFromKey(
                this.plugin.Configuration.ComingSoonTargetKey);

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                AddMovieTracker.Remove(entryId);
                SetStatus(
                    "The administrator has not configured a Coming Soon target library.",
                    ItemStatus.Failed);
                RebuildResults();
                return;
            }

            string folderName = EmbyLibrarySharedService.BuildComingSoonFolderName(
                entry.ConfirmedTitle,
                entry.ConfirmedYear);
            string destination = Path.Combine(targetPath, folderName);

            if (Directory.Exists(destination))
            {
                AddMovieTracker.Remove(entryId);
                SetStatus(
                    string.Format("This movie already has a destination folder: {0}", destination),
                    ItemStatus.Warning);
                RebuildResults();
                return;
            }

            AddMovieTracker.SetQueued(entryId);
            AddMovieTracker.RecordFolderPath(entryId, destination);

            var worker = this.taskManager.ScheduledTasks
                .FirstOrDefault(t => t.ScheduledTask is AddMovieTask);

            if (worker == null)
            {
                AddMovieTracker.SetAddFailed(entryId, "Add Movie scheduled task was not found.");
                SetStatus("The server could not start the add operation.", ItemStatus.Failed);
                RebuildResults();
                return;
            }

            this.taskManager.Execute(worker, new TaskOptions());

            string submittedTitle = entry.ConfirmedYear > 0
                ? string.Format("{0} ({1})", entry.ConfirmedTitle, entry.ConfirmedYear)
                : entry.ConfirmedTitle;

            ResetForm();
            SetStatus(
                string.Format("{0} was submitted to the server for addition.", submittedTitle),
                ItemStatus.Succeeded);
            RebuildResults();
        }

        private void ResetForm()
        {
            UI.MovieName = string.Empty;
            UI.ReleaseYear = string.Empty;
            this.currentResults.Clear();
        }

        private void RebuildResults()
        {
            var list = new GenericItemList();

            foreach (var result in this.currentResults)
            {
                string year = result.ReleaseYear > 0
                    ? result.ReleaseYear.ToString(CultureInfo.InvariantCulture)
                    : "Unknown year";

                string overview = string.IsNullOrWhiteSpace(result.Overview)
                    ? "No overview available."
                    : Truncate(result.Overview, 180);

                list.Add(new GenericListItem(
                    IconNames.movie,
                    string.Format("{0} ({1})", result.Title, year),
                    overview)
                {
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Unavailable,
                    Button1 = new ButtonItem("Submit This Match")
                    {
                        Icon = IconNames.check_circle,
                        Data1 = "SubmitMatch_" + result.Id.ToString(CultureInfo.InvariantCulture),
                        CommandId = "SubmitMatch_" + result.Id.ToString(CultureInfo.InvariantCulture),
                    },
                });
            }

            if (this.currentResults.Count > 0)
            {
                list.Add(new GenericListItem(
                    IconNames.clear,
                    "Clear search results",
                    string.Empty)
                {
                    IconMode = ItemListIconMode.SmallRegular,
                    Button1 = new ButtonItem("Clear")
                    {
                        StandardIcon = StandardIcons.Remove,
                        Data1 = "Clear",
                        CommandId = "Clear",
                    },
                });
            }

            UI.Results = list;
        }

        private void SetStatus(string message, ItemStatus status)
        {
            UI.Status.StatusText = message ?? string.Empty;
            UI.Status.Status = status;
        }

        private static int? ParseYear(string text)
        {
            int year;
            if (!string.IsNullOrWhiteSpace(text)
                && int.TryParse(text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out year)
                && year > 1800
                && year < 2200)
            {
                return year;
            }

            return null;
        }

        private static string Truncate(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maximumLength - 3) + "...";
        }
    }
}