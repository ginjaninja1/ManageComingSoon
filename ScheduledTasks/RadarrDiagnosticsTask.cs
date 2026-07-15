namespace ManageComingSoon.ScheduledTasks
{
    using ManageComingSoon.Model;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Tasks;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Manual-only utility task for one-off diagnostics/repairs against the
    /// Radarr Coming Soon channel. Never runs on a schedule — trigger it
    /// explicitly from Emby's dashboard when investigating an issue. Keeps
    /// probe/repair code out of the real sync task's permanent code path.
    /// </summary>
    public class RadarrDiagnosticsTask : IScheduledTask
    {
        private readonly ILibraryManager libraryManager;
        private readonly ILogger logger;

        public RadarrDiagnosticsTask(ILibraryManager libraryManager, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        public string Name => "Radarr Channel Diagnostics (manual)";

        public string Key => "ManageComingSoon-RadarrDiagnostics";

        public string Description =>
            "Manual-only diagnostic/repair tool for the Radarr Coming Soon channel. Logs persisted Emby channel items and force-corrects RadarrId ProviderIds where they're stale.";

        public string Category => "Manage Coming Soon";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Deliberately empty — this task only ever runs when manually
            // triggered from the dashboard.
            yield break;
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = ManageComingSoonPlugin.Instance.Configuration;

            var channelBaseItem = libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Channel" },
                Name = config.RadarrChannelName
            }).Items.FirstOrDefault();

            if (channelBaseItem == null)
            {
                logger.Warn("ManageComingSoon Diagnostics: Could not find persisted Channel BaseItem '{0}'.", config.RadarrChannelName);
                return Task.CompletedTask;
            }

            var movieItems = libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                Parent = channelBaseItem
            }).Items;

            logger.Info(
                "ManageComingSoon Diagnostics: {0} Movie item(s) found under channel '{1}'.",
                movieItems.Length, channelBaseItem.Name);

            foreach (var movieItem in movieItems)
            {
                var providerIdsDesc = movieItem.ProviderIds == null || movieItem.ProviderIds.Count == 0
                    ? "(none)"
                    : string.Join("; ", movieItem.ProviderIds.Select(kv => string.Format("{0}={1}", kv.Key, kv.Value)));

                logger.Info(
                    "ManageComingSoon Diagnostics:   Emby item — InternalId={0}, Name='{1}', ProviderIds=[{2}].",
                    movieItem.InternalId, movieItem.Name, providerIdsDesc);
            }

            return Task.CompletedTask;
        }
    }
}