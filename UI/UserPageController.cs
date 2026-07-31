namespace ManageComingSoon.UI
{
    using System;
    using System.Threading.Tasks;
    using ManageComingSoon.Services;
    using ManageComingSoon.UI.UserAddMovie;
    using ManageComingSoon.UIBaseClasses;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Tasks;

    internal sealed class UserPageController : ControllerBase, IPluginPageSecurity
    {
        private readonly PluginInfo pluginInfo;
        private readonly ManageComingSoonPlugin plugin;
        private readonly TmdbService tmdbService;
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
            this.taskManager = taskManager;
            this.logger = logger;

            PageInfo = new PluginPageInfo
            {
                Name = "ManageComingSoonUser",
                DisplayName = "Manage Coming Soon",
                EnableInMainMenu = false,
                EnableInUserMenu = true,
                MenuIcon = "upcoming",
                IsMainConfigPage = false
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new UserAddMoviePageView(
                this.pluginInfo,
                this.plugin,
                this.tmdbService,
                this.taskManager,
                this.logger);
            return Task.FromResult(view);
        }

        public Task CheckIsUserAuthorised(UserDto user, IPluginUIView requestedView)
        {
            if (user == null)
                throw new UnauthorizedAccessException("You must be signed in to use this page.");
            return Task.CompletedTask;
        }
    }
}