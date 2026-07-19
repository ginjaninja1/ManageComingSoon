namespace ManageComingSoon.UI
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using ManageComingSoon.Services;
    using ManageComingSoon.UI.AddMovie;
    using ManageComingSoon.UI.Configuration;
    using ManageComingSoon.UI.MakeLive;
    using ManageComingSoon.UIBaseClasses;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Tasks;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal class MainPageController : ControllerBase, IHasTabbedUIPages
    {
        private readonly PluginInfo pluginInfo;
        private readonly ManageComingSoonPlugin plugin;
        private readonly TmdbService tmdbService;
        private readonly EmbyLibraryAddService addService;
        private readonly EmbyLibraryMakeService makeService;
        private readonly ILibraryManager libraryManager;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;
        private readonly List<IPluginUIPageController> tabPages;

        public MainPageController(
            PluginInfo pluginInfo,
            ManageComingSoonPlugin plugin,
            TmdbService tmdbService,
            EmbyLibraryAddService addService,
            EmbyLibraryMakeService makeService,
            ILibraryManager libraryManager,
            ITaskManager taskManager,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.plugin = plugin;
            this.tmdbService = tmdbService;
            this.addService = addService;
            this.makeService = makeService;
            this.libraryManager = libraryManager;
            this.taskManager = taskManager;
            this.logger = logger;

            PageInfo = new PluginPageInfo
            {
                Name = "ManageComingSoon",
                EnableInMainMenu = true,
                DisplayName = "Manage Coming Soon",
                MenuIcon = "upcoming",
                IsMainConfigPage = true,
            };

            this.tabPages = new List<IPluginUIPageController>
            {
                // Tab 2: Make Live
                new TabPageController(
                    pluginInfo,
                    nameof(MakeLivePageView),
                    "Make Live",
                    _ => new MakeLivePageView(pluginInfo, plugin, makeService, taskManager, logger)),

                // Tab 3: Configuration
                new TabPageController(
                    pluginInfo,
                    nameof(ConfigurationPageView),
                    "Configuration",
                    _ => new ConfigurationPageView(
                        pluginInfo,
                        plugin,
                        libraryManager)),
            };
        }

        public override PluginPageInfo PageInfo { get; }

        // Tab 1 (default): Add Coming Soon
        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new AddMoviePageView(
                pluginInfo, plugin, tmdbService, addService, logger, taskManager);
            return Task.FromResult(view);
        }

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => tabPages.AsReadOnly();
    }
}