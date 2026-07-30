namespace ManageComingSoon.UI
{
    using System.Threading.Tasks;
    using ManageComingSoon.Services;
    using ManageComingSoon.UI.AddMovie;
    using ManageComingSoon.UIBaseClasses;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Tasks;

    // Ordinary-user entry point: Add Coming Soon only, no Make Live / Configuration
    // tabs. Distinct PageInfo.Name from MainPageController - confirmed via ILSpy
    // that PageId = "{first 6 hex of plugin Id}:{PageInfo.Name}", so this only
    // needs a distinct Name, not any other registration change.
    //
    // Shares the same singleton services (tmdbService, addService, taskManager,
    // logger) as MainPageController's Add Coming Soon tab. This is safe: the
    // actual add/search state lives in AddMovieTracker (static, lock-protected,
    // persisted), not in these service instances or in the view itself, so
    // admins and ordinary users hitting this concurrently share one consistent
    // queue rather than each getting a divergent view of it.
    internal class UserPageController : ControllerBase
    {
        private readonly PluginInfo pluginInfo;
        private readonly ManageComingSoonPlugin plugin;
        private readonly TmdbService tmdbService;
        private readonly EmbyLibraryAddService addService;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;

        public UserPageController(
            PluginInfo pluginInfo,
            ManageComingSoonPlugin plugin,
            TmdbService tmdbService,
            EmbyLibraryAddService addService,
            ITaskManager taskManager,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.plugin = plugin;
            this.tmdbService = tmdbService;
            this.addService = addService;
            this.taskManager = taskManager;
            this.logger = logger;

            PageInfo = new PluginPageInfo
            {
                Name = "ManageComingSoonUser",
                EnableInMainMenu = false,
                EnableInUserMenu = true,
                DisplayName = "Manage Coming Soon",
                MenuIcon = "upcoming",
                IsMainConfigPage = false,
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new AddMoviePageView(
                pluginInfo, plugin, tmdbService, addService, logger, taskManager);
            return Task.FromResult(view);
        }
    }
}