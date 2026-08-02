// ManageComingSoon - Add Title Store
// JSON-backed persistence for the Add Coming Soon search list.
// Stores a List<AddTitleEntry> as a single file in Emby's plugin data folder.
// Mirrors the PluginConfigStore pattern exactly.

namespace ManageComingSoon.Storage
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ManageComingSoon.Services;
    using MediaBrowser.Common;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Serialization;

    public class AddTitleStore
    {
        private readonly ILogger logger;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;
        private readonly string storeFilePath;
        private readonly object lockObj = new object();

        public AddTitleStore(IApplicationHost applicationHost, ILogger logger)
        {
            this.logger = logger;
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.fileSystem = applicationHost.Resolve<IFileSystem>();

            var appPaths = applicationHost.Resolve<IApplicationPaths>();
            string dir = appPaths.PluginConfigurationsPath;

            if (!this.fileSystem.DirectoryExists(dir))
                this.fileSystem.CreateDirectory(dir);

            this.storeFilePath = Path.Combine(dir, "ManageComingSoon.AddTitleList.json");
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>Load all persisted entries. Returns empty list if file absent or corrupt.</summary>
        public List<AddTitleEntry> Load()
        {
            lock (this.lockObj)
            {
                return LoadInternal();
            }
        }

        /// <summary>Persist the full entry list atomically.</summary>
        public void Save(List<AddTitleEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            lock (this.lockObj)
            {
                SaveInternal(entries);
            }
        }

        // -----------------------------------------------------------------------
        // Internals (called under lock)
        // -----------------------------------------------------------------------

        private List<AddTitleEntry> LoadInternal()
        {
            try
            {
                if (!this.fileSystem.FileExists(this.storeFilePath))
                    return new List<AddTitleEntry>();

                using (var stream = this.fileSystem.OpenRead(this.storeFilePath))
                {
                    var result = this.jsonSerializer
                        .DeserializeFromStream<List<AddTitleEntry>>(stream);
                    return result ?? new List<AddTitleEntry>();
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "ManageComingSoon: AddTitleStore failed to load – returning empty list", ex);
                return new List<AddTitleEntry>();
            }
        }

        private void SaveInternal(List<AddTitleEntry> entries)
        {
            try
            {
                using (var stream = this.fileSystem.GetFileStream(
                    this.storeFilePath, FileOpenMode.Create, FileAccessMode.Write))
                {
                    this.jsonSerializer.SerializeToStream(entries, stream,
                        new JsonSerializerOptions { Indent = true });
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "ManageComingSoon: AddTitleStore failed to save", ex);
            }
        }
    }
}
