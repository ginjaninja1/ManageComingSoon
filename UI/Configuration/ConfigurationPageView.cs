namespace ManageComingSoon.UI.Configuration
{
    using Emby.Web.GenericEdit.Common;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using ManageComingSoon.UIBaseClasses.Views;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;

    internal class ConfigurationPageView : PluginPageView
    {
        private static readonly string[] ValidVideoExtensions =
        {
            ".mp4", ".mkv", ".avi", ".mov"
        };

        // Must match StubResourceName in EmbyLibraryAddService — that's the
        // single place the default video is actually written from.
        private const string DefaultStubResourceName = "ManageComingSoon.comingsoon.mp4";

        private readonly ManageComingSoonPlugin plugin;
        private readonly ILibraryManager libraryManager;

        public ConfigurationPageView(
            PluginInfo pluginInfo,
            ManageComingSoonPlugin plugin,
            ILibraryManager libraryManager)
            : base(pluginInfo.Id)
        {
            this.plugin = plugin;
            this.libraryManager = libraryManager;

            var ui = new ConfigurationUI();
            this.ContentData = ui;
            this.ShowSave = false;

            var cfg = this.plugin.Configuration;

            ui.TmdbApiKey = cfg.TmdbApiKey;
            ui.MakeLiveMoveToNewLocation = cfg.MakeLiveMoveToNewLocation;
            ui.MakeLiveDeleteStubFile = cfg.MakeLiveDeleteStubFile;
            ui.MakeLiveDeleteStubFileMaxFileSize = cfg.MakeLiveDeleteStubFileMaxFileSize;
            ui.UnlockTags = cfg.UnlockTags;
            ui.ComingSoonTargetKey = cfg.ComingSoonTargetKey;
            ui.MakeLiveTargetKey = cfg.MakeLiveTargetKey;
            ui.ComingSoonStubVideoPath = cfg.ComingSoonStubVideoPath;
            ui.EmbyApiKey = cfg.EmbyApiKey;
            ui.ComingSoonTagText = string.IsNullOrEmpty(cfg.ComingSoonTagText)
                ? "Coming Soon"
                : cfg.ComingSoonTagText;

            // Reflect the persisted (already-validated) stub video state on load.
            // Config only ever holds a valid path or empty — see SaveConfiguration.
            RefreshStubVideoStatus(ui.StubVideoStatusItem, cfg.ComingSoonStubVideoPath);

            PopulateLibraryOptions(ui);
        }

        private ConfigurationUI UI => (ConfigurationUI)this.ContentData;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (commandId == "ConfigurationChanged")
            {
                SaveConfiguration();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "ClearStubVideo")
            {
                var ui = UI;
                ui.ComingSoonStubVideoPath = string.Empty;

                var cfg = this.plugin.Configuration;
                cfg.ComingSoonStubVideoPath = string.Empty;
                this.plugin.UpdateConfiguration(cfg);

                SetStubVideoStatus(ui.StubVideoStatusItem,
                    string.Format("Using default {0}", FormatDefaultStubSizeMb()),
                    ItemStatus.Unavailable);

                return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        private void SaveConfiguration()
        {
            var ui = UI;
            var cfg = this.plugin.Configuration;

            cfg.TmdbApiKey = ui.TmdbApiKey;
            cfg.MakeLiveMoveToNewLocation = ui.MakeLiveMoveToNewLocation;
            cfg.MakeLiveDeleteStubFile = ui.MakeLiveDeleteStubFile;
            cfg.MakeLiveDeleteStubFileMaxFileSize = ui.MakeLiveDeleteStubFileMaxFileSize;
            cfg.UnlockTags = ui.UnlockTags;
            cfg.ComingSoonTargetKey = ui.ComingSoonTargetKey;
            cfg.MakeLiveTargetKey = ui.MakeLiveTargetKey;
            cfg.EmbyApiKey = (ui.EmbyApiKey ?? string.Empty).Trim();

            // ---- Coming Soon tag --------------------------------------------------
            string tagText = (ui.ComingSoonTagText ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(tagText))
            {
                cfg.ComingSoonTagText = "Coming Soon";
                ui.ComingSoonTagText = "Coming Soon";
            }
            else
            {
                cfg.ComingSoonTagText = tagText;
            }

            // ---- Stub video path ---------------------------------------------------
            cfg.ComingSoonStubVideoPath = ValidateStubVideoPath(
                ui.ComingSoonStubVideoPath, ui.StubVideoStatusItem);

            this.plugin.UpdateConfiguration(cfg);
        }

        // -----------------------------------------------------------------------
        // Stub video status helpers
        // -----------------------------------------------------------------------

        private static string ValidateStubVideoPath(string rawPath, GenericListItem statusItem)
        {
            string path = (rawPath ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(path))
            {
                SetStubVideoStatus(statusItem,
                    string.Format("Using default {0}", FormatDefaultStubSizeMb()),
                    ItemStatus.Unavailable);
                return string.Empty;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (!IsValidVideoExtension(ext))
            {
                SetStubVideoStatus(statusItem,
                    "Invalid file type — must be mp4, mkv, avi or mov. Using default.",
                    ItemStatus.Failed);
                return string.Empty;
            }

            if (!File.Exists(path))
            {
                SetStubVideoStatus(statusItem,
                    "File not found. Using default.",
                    ItemStatus.Failed);
                return string.Empty;
            }

            SetStubVideoStatus(statusItem,
                string.Format(
                    "Custom Active {0} {1}. Clear the field above to change or remove it.",
                    Path.GetFileName(path),
                    FormatFileSizeMb(path)),
                ItemStatus.Succeeded);
            return path;
        }

        private static void SetStubVideoStatus(GenericListItem targetItem, string text, ItemStatus status)
        {
            if (targetItem != null)
            {
                targetItem.SecondaryText = text;
                targetItem.Status = status;
            }
        }

        private static void RefreshStubVideoStatus(GenericListItem targetItem, string savedPath)
        {
            if (string.IsNullOrEmpty(savedPath))
            {
                SetStubVideoStatus(targetItem,
                    string.Format("Using default {0}", FormatDefaultStubSizeMb()),
                    ItemStatus.Unavailable);
            }
            else
            {
                SetStubVideoStatus(targetItem,
                    string.Format(
                        "Custom Active {0} {1}",
                        Path.GetFileName(savedPath),
                        FormatFileSizeMb(savedPath)),
                    ItemStatus.Succeeded);
            }
        }

        private static string FormatFileSizeMb(string path)
        {
            try
            {
                var info = new FileInfo(path);
                double mb = info.Length / (1024.0 * 1024.0);
                return string.Format("[{0}MB]", (long)Math.Ceiling(mb));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string FormatDefaultStubSizeMb()
        {
            try
            {
                var asm = typeof(ManageComingSoonPlugin).Assembly;
                using (var stream = asm.GetManifestResourceStream(DefaultStubResourceName))
                {
                    if (stream == null) return string.Empty;
                    double mb = stream.Length / (1024.0 * 1024.0);
                    return string.Format("[{0}MB]", (long)Math.Ceiling(mb));
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsValidVideoExtension(string ext)
        {
            foreach (var valid in ValidVideoExtensions)
                if (string.Equals(ext, valid, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // -----------------------------------------------------------------------
        // Build EditorSelectOption lists from Emby virtual folders
        // Value = "LibraryName|Path" composite key
        // -----------------------------------------------------------------------

        private void PopulateLibraryOptions(ConfigurationUI ui)
        {
            var comingSoonOptions = new List<EditorSelectOption>();
            var makeLiveOptions = new List<EditorSelectOption>();

            try
            {
                var virtualFolders = this.libraryManager.GetVirtualFolders();

                foreach (var folder in virtualFolders)
                {
                    bool isMovieLib = string.IsNullOrEmpty(folder.CollectionType) ||
                        string.Equals(folder.CollectionType, "movies",
                            StringComparison.OrdinalIgnoreCase);

                    bool isMakeLiveLib = isMovieLib ||
                        string.Equals(folder.CollectionType, "tvshows",
                            StringComparison.OrdinalIgnoreCase);

                    foreach (var loc in folder.Locations)
                    {
                        string key = folder.Name + "|" + loc;
                        string name = folder.Name + " \u2192 " + loc;

                        if (isMovieLib)
                            comingSoonOptions.Add(new EditorSelectOption(key, name));

                        if (isMakeLiveLib || isMovieLib)
                            makeLiveOptions.Add(new EditorSelectOption(key, name));
                    }
                }
            }
            catch (Exception)
            {
                // Library enumeration failed; options stay empty
            }

            ui.ComingSoonLibraryOptions = comingSoonOptions;
            ui.MakeLiveLibraryOptions = makeLiveOptions;
        }

        // -----------------------------------------------------------------------
        // Helper: decode a composite key back to its path component
        // -----------------------------------------------------------------------

        public static string PathFromKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            int idx = key.IndexOf('|');
            return idx >= 0 ? key.Substring(idx + 1) : key;
        }
    }
}