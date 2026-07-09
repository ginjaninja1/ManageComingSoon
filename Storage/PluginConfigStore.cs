namespace ManageComingSoon.Storage
{
    using System;
    using System.IO;
    using ManageComingSoon.Model;
    using MediaBrowser.Common;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Serialization;

    /// <summary>
    /// Simple JSON-backed store for PluginConfiguration.
    /// Follows the SimpleFileStore pattern from the SDK demo.
    /// </summary>
    public class PluginConfigStore
    {
        private readonly ILogger logger;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;
        private readonly string configFilePath;
        private readonly object lockObj = new object();
        private PluginConfiguration cached;

        public PluginConfigStore(IApplicationHost applicationHost, ILogger logger, string pluginName)
        {
            this.logger = logger;
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.fileSystem     = applicationHost.Resolve<IFileSystem>();

            var appPaths = applicationHost.Resolve<IApplicationPaths>();
            string dir = appPaths.PluginConfigurationsPath;
            if (!this.fileSystem.DirectoryExists(dir))
                this.fileSystem.CreateDirectory(dir);

            this.configFilePath = Path.Combine(dir, pluginName + ".json");
        }

        public PluginConfiguration GetConfig()
        {
            lock (this.lockObj)
            {
                if (this.cached != null) return this.cached;
                return Load();
            }
        }

        public void SaveConfig(PluginConfiguration cfg)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            lock (this.lockObj)
            {
                using (var stream = this.fileSystem.GetFileStream(
                    this.configFilePath, FileOpenMode.Create, FileAccessMode.Write))
                {
                    this.jsonSerializer.SerializeToStream(cfg, stream,
                        new JsonSerializerOptions { Indent = true });
                }
                this.cached = cfg;
            }
        }

        private PluginConfiguration Load()
        {
            try
            {
                if (!this.fileSystem.FileExists(this.configFilePath))
                    return this.cached = new PluginConfiguration();

                using (var stream = this.fileSystem.OpenRead(this.configFilePath))
                {
                    this.cached = this.jsonSerializer.DeserializeFromStream<PluginConfiguration>(stream)
                                  ?? new PluginConfiguration();
                    return this.cached;
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: Failed to load config", ex);
                return this.cached = new PluginConfiguration();
            }
        }
    }
}
