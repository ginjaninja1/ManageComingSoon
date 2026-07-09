// ManageComingSoon - Make Live Store
// JSON-backed persistence for the Make Live tracker (active + history rows).
// Stores a List<MakeLiveEntry> as a single file in Emby's plugin data folder.
// Mirrors the AddMovieStore pattern exactly.

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

    public class MakeLiveStore
    {
        private readonly ILogger logger;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;
        private readonly string storeFilePath;
        private readonly object lockObj = new object();

        public MakeLiveStore(IApplicationHost applicationHost, ILogger logger)
        {
            this.logger = logger;
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.fileSystem = applicationHost.Resolve<IFileSystem>();

            var appPaths = applicationHost.Resolve<IApplicationPaths>();
            string dir = appPaths.PluginConfigurationsPath;

            if (!this.fileSystem.DirectoryExists(dir))
                this.fileSystem.CreateDirectory(dir);

            this.storeFilePath = Path.Combine(dir, "ManageComingSoon.MakeLiveList.json");
            this.logger.Debug(
                    "makelive logger found");
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>Load all persisted entries. Returns empty list if file absent or corrupt.</summary>
        public List<MakeLiveEntry> Load()
        {
            lock (this.lockObj)
            {
                return LoadInternal();
            }
        }

        /// <summary>Persist the full entry list atomically.</summary>
        public void Save(List<MakeLiveEntry> entries)
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

        private List<MakeLiveEntry> LoadInternal()
        {
            try
            {
                if (!this.fileSystem.FileExists(this.storeFilePath))
                    return new List<MakeLiveEntry>();

                using (var stream = this.fileSystem.OpenRead(this.storeFilePath))
                {
                    var result = this.jsonSerializer
                        .DeserializeFromStream<List<MakeLiveEntry>>(stream);
                    return result ?? new List<MakeLiveEntry>();
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "ManageComingSoon: MakeLiveStore failed to load – returning empty list", ex);
                return new List<MakeLiveEntry>();
            }
        }

        private void SaveInternal(List<MakeLiveEntry> entries)
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
                    "ManageComingSoon: MakeLiveStore failed to save", ex);
            }
        }
    }
}