namespace ManageComingSoon.UI.Configuration
{
    using Emby.Web.GenericEdit.Common;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using ManageComingSoon.UIBaseClasses.Views;
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
        // single place the default video is actually written from. Also
        // reused by RadarrComingSoonChannel.ResolveStubVideoPath for the
        // Radarr channel's own default placeholder.
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

            // ---- Radarr integration ----
            ui.RadarrEnabled = cfg.RadarrEnabled;
            ui.RadarrChannelName = string.IsNullOrEmpty(cfg.RadarrChannelName)
                ? "Radarr Coming Soon"
                : cfg.RadarrChannelName;
            ui.RadarrChannelIdentityTag = string.IsNullOrEmpty(cfg.RadarrChannelIdentityTag)
                ? "ManageComingSoon:RadarrChannel"
                : cfg.RadarrChannelIdentityTag;
            ui.RadarrUrl = cfg.RadarrUrl;
            ui.RadarrApiKey = cfg.RadarrApiKey;
            ui.RadarrRefreshMinutes = cfg.RadarrRefreshMinutes;
            ui.RadarrSyncMode = cfg.RadarrSyncMode;
            ui.RadarrStubVideoPath = cfg.RadarrStubVideoPath;

            // Reflect the persisted (already-validated) stub video state on load.
            // Config only ever holds a valid path or empty — see SaveConfiguration.
            RefreshStubVideoStatus(ui.StubVideoStatusItem, cfg.ComingSoonStubVideoPath);
            RefreshStubVideoStatus(ui.RadarrStubVideoStatusItem, cfg.RadarrStubVideoPath);

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

            if (commandId == "ClearRadarrStubVideo")
            {
                var ui = UI;
                ui.RadarrStubVideoPath = string.Empty;

                var cfg = this.plugin.Configuration;
                cfg.RadarrStubVideoPath = string.Empty;
                this.plugin.UpdateConfiguration(cfg);

                SetStubVideoStatus(ui.RadarrStubVideoStatusItem,
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

            // ---- Standard fields -----------------------------------------------
            cfg.TmdbApiKey = ui.TmdbApiKey;
            cfg.MakeLiveMoveToNewLocation = ui.MakeLiveMoveToNewLocation;
            cfg.MakeLiveDeleteStubFile = ui.MakeLiveDeleteStubFile;
            cfg.MakeLiveDeleteStubFileMaxFileSize = ui.MakeLiveDeleteStubFileMaxFileSize;
            cfg.UnlockTags = ui.UnlockTags;
            cfg.ComingSoonTargetKey = ui.ComingSoonTargetKey;
            cfg.MakeLiveTargetKey = ui.MakeLiveTargetKey;
            cfg.EmbyApiKey = (ui.EmbyApiKey ?? string.Empty).Trim();

            // ---- Radarr integration ----------------------------------------------
            cfg.RadarrEnabled = ui.RadarrEnabled;

            string channelName = (ui.RadarrChannelName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(channelName))
            {
                cfg.RadarrChannelName = "Radarr Coming Soon";
                ui.RadarrChannelName = "Radarr Coming Soon";
            }
            else
            {
                cfg.RadarrChannelName = channelName;
            }

            string identityTag = (ui.RadarrChannelIdentityTag ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(identityTag))
            {
                cfg.RadarrChannelIdentityTag = "ManageComingSoon:RadarrChannel";
                ui.RadarrChannelIdentityTag = "ManageComingSoon:RadarrChannel";
            }
            else
            {
                cfg.RadarrChannelIdentityTag = identityTag;
            }

            cfg.RadarrUrl = (ui.RadarrUrl ?? string.Empty).Trim();
            cfg.RadarrApiKey = (ui.RadarrApiKey ?? string.Empty).Trim();
            cfg.RadarrRefreshMinutes = ui.RadarrRefreshMinutes > 0 ? ui.RadarrRefreshMinutes : 15;
            cfg.RadarrSyncMode = ui.RadarrSyncMode;

            // ---- Coming Soon tag --------------------------------------------------
            // Tag text is always required. If the user clears the field, fall back
            // to the default and write it back into the UI model so the field
            // visibly reverts to "Coming Soon" in the postback response rather than
            // silently saving the default while leaving the field appearing blank.
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

            // ---- Stub video paths -------------------------------------------------
            // Single source of truth: the path itself. No separate enable toggle.
            // The UI field is ALWAYS left exactly as the user typed/picked it —
            // never cleared or overwritten by this method — so the user can always
            // see, edit, or clear their own input regardless of validity. Only the
            // CONFIG value is gated on validity. Same validation logic applied to
            // both the ComingSoon and Radarr stub fields.
            cfg.ComingSoonStubVideoPath = ValidateStubVideoPath(
                ui.ComingSoonStubVideoPath, ui.StubVideoStatusItem);

            cfg.RadarrStubVideoPath = ValidateStubVideoPath(
                ui.RadarrStubVideoPath, ui.RadarrStubVideoStatusItem);

            this.plugin.UpdateConfiguration(cfg);
        }

        // -----------------------------------------------------------------------
        // Stub video status helpers — shared by ComingSoon and Radarr fields
        // -----------------------------------------------------------------------

        /// <summary>
        /// Validates a stub video path field, updates the corresponding status
        /// item to reflect the result, and returns what should actually be
        /// saved into config (empty string if invalid/missing — falls back to
        /// the default rather than persisting a broken path).
        /// </summary>
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

        /// <summary>
        /// Sets a status item on page load to reflect the currently saved
        /// (already-validated) config path, without re-running validation.
        /// </summary>
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

        /// <summary>
        /// Returns the size of a file on disk formatted as "[NMB]", or empty
        /// string if it can't be determined (e.g. file missing or inaccessible).
        /// </summary>
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

        /// <summary>
        /// Returns the size of the embedded default stub video (the one
        /// EmbyLibraryAddService/RadarrComingSoonChannel fall back to via
        /// GetManifestResourceStream), formatted as "[NMB]", or empty string
        /// if it can't be read.
        /// </summary>
        private static string FormatDefaultStubSizeMb()
        {
            try
            {
                // Use the plugin's own assembly, not Assembly.GetExecutingAssembly() —
                // if ConfigurationPageView lives in a separate UI project from
                // EmbyLibraryAddService, GetExecutingAssembly() here would return the
                // wrong assembly and the resource lookup would silently return null.
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