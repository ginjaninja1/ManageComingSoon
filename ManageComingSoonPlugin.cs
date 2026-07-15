namespace ManageComingSoon
{
    using ManageComingSoon.Model;
    using ManageComingSoon.Services;
    using ManageComingSoon.UI;
    using ManageComingSoon.UI.Configuration;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Common.Net;
    using MediaBrowser.Common.Plugins;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Channels;
    using MediaBrowser.Controller.Drawing;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Persistence;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Drawing;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Serialization;
    using MediaBrowser.Model.Tasks;
    using System;
    using System.Collections.Generic;
    using System.IO;

    public class ManageComingSoonPlugin : BasePlugin<PluginConfiguration>, IHasThumbImage, IHasUIPages
    {
        private static readonly Guid PluginId = new Guid("3A1F9C82-7D4E-4B5A-9F2D-1E8C6A3B0D74");

        private readonly IServerApplicationHost appHost;
        private readonly ITaskManager taskManager;
        private readonly ILogger logger;
        private List<IPluginUIPageController> pages;
        private EmbyLibraryAddService addServiceInstance;
        private EmbyLibraryMakeService makeServiceInstance;
        private RadarrChannelIdentityReconciler reconcilerInstance;

        public static ManageComingSoonPlugin Instance { get; private set; }

        public ManageComingSoonPlugin(
            IServerApplicationHost applicationHost,
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogManager logManager,
            ITaskManager taskManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            this.appHost = applicationHost;
            this.logger = logManager.GetLogger(Name);
            this.taskManager = taskManager;
        }

        public override Guid Id => PluginId;
        public override string Name => "Manage Coming Soon";
        public override string Description =>
            "Search TMDB for upcoming movies, add Coming Soon placeholders to your library, " +
            "and manage their transition to live status.";

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png")
                   ?? Stream.Null;
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (this.pages == null)
                {
                    var libraryManager = this.appHost.Resolve<ILibraryManager>();
                    var providerManager = this.appHost.Resolve<IProviderManager>();
                    var itemRepository = this.appHost.Resolve<IItemRepository>();
                    var fileSystem = this.appHost.Resolve<IFileSystem>();
                    var httpClient = this.appHost.Resolve<IHttpClient>();
                    var jsonSerializer = this.appHost.Resolve<IJsonSerializer>();
                    var libraryMonitor = this.appHost.Resolve<ILibraryMonitor>();
                    var channelManager = this.appHost.Resolve<IChannelManager>();
                    var imageProcessor = this.appHost.Resolve<IImageProcessor>();
                    var appPaths = this.appHost.Resolve<IApplicationPaths>();

                    var tmdbService = new TmdbService(httpClient, jsonSerializer, this.logger);

                    if (this.addServiceInstance == null)
                    {
                        this.addServiceInstance = new EmbyLibraryAddService(
                            this.appHost, libraryManager, itemRepository,
                            providerManager, fileSystem, libraryMonitor, this.logger);

                        AddMovieTask.SetDependencies(
                            this.addServiceInstance,
                            this.logger,
                            () => this.Configuration.EmbyApiKey,
                            () => ConfigurationPageView.PathFromKey(
                                      this.Configuration.ComingSoonTargetKey),
                            () => string.IsNullOrEmpty(this.Configuration.ComingSoonStubVideoPath)
                                      ? null
                                      : this.Configuration.ComingSoonStubVideoPath);
                    }

                    if (this.makeServiceInstance == null)
                    {
                        this.makeServiceInstance = new EmbyLibraryMakeService(
                            this.appHost, libraryManager, itemRepository,
                            providerManager, fileSystem, libraryMonitor, this.logger);

                        MakeLiveTask.SetDependencies(
    this.makeServiceInstance, this.logger,
    () => this.Configuration.EmbyApiKey,
    () => this.Configuration.MakeLiveDeleteStubFileMaxFileSize,
    () => this.Configuration.UnlockTags);
                    }

                    if (this.reconcilerInstance == null)
                    {
                        this.reconcilerInstance = new RadarrChannelIdentityReconciler(
                            channelManager, libraryManager, imageProcessor,
                            appPaths, this.logger);
                    }

                    this.pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(
                            GetPluginInfo(),
                            this,
                            tmdbService,
                            this.addServiceInstance,
                            this.makeServiceInstance,
                            libraryManager,
                            channelManager,
                            this.taskManager,
                            this.reconcilerInstance,
                            this.logger)
                    };
                }

                return this.pages.AsReadOnly();
            }
        }
    }
}