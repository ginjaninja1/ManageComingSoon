// STATUS (updated after checking dev.emby.media docs + Emby server source,
// and after live testing against a real server — see chat history):
//   1. CONFIRMED — ManageComingSoonPlugin.Instance.Configuration.
//   2. CONFIRMED — IChannel/IScheduledTask registration is automatic via
//      Emby's own GetExports<T>() scanning. No manual AddParts needed.
//   3. CONFIRMED — namespaces/enum members (ChannelItemInfo, ChannelItemType,
//      ChannelParentalRating.GeneralAudience, ChannelMediaType.Video,
//      ChannelMediaContentType.Movie) all compile and behave as expected.
//   4. STILL UNCONFIRMED — ProviderIdDictionary key strings ("Tmdb"/"Imdb").
//      Structurally safe either way (plain string-keyed dictionary), just
//      unconfirmed whether Emby's own providers recognize these exact names
//      for cross-referencing.
//   5. STILL UNCONFIRMED — IApplicationPaths.DataPath as the cache/stub file
//      location; swap for an existing storage convention in this codebase
//      if one exists.
//   6. RESOLVED BY TESTING — removal is purely implicit: Emby's built-in
//      "Refresh Internet Channels" task reconciles its database against
//      whatever GetChannelItems returns, full stop. RadarrEnableDelete /
//      ISupportsDelete were never observed firing during a confirmed
//      removal — kept only as a safety toggle for the user-initiated
//      delete-from-UI path, not because normal sync depends on it.
//   9. NEW, UNCONFIRMED — playback still failed with MediaSources populated
//      directly on ChannelItemInfo alone ("No compatible streams"). Now also
//      implementing IRequiresMediaInfoCallback (an interface pasted at the
//      very start of this conversation but not used until now) — suspected
//      to be the actual mechanism Emby calls at playback time. Both paths
//      build via the same BuildMediaSource helper. Needs live testing.

namespace ManageComingSoon.Channels
{
    using ManageComingSoon.Model;
    using ManageComingSoon.Services;
    using ManageComingSoon.Services.Models;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Controller.Channels;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Channels;
    using MediaBrowser.Model.Drawing;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.MediaInfo;
    using MediaBrowser.Model.Providers;
    using MediaBrowser.Model.Serialization;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class RadarrComingSoonChannel : IChannel, ISupportsDelete, IRequiresMediaInfoCallback
    {
        private const string IdPrefix = "radarr-coming-soon-";
        private const string CacheFileName = "radarr-channel-cache.json";

        // Same embedded resource ConfigurationPageView falls back to for the
        // ComingSoon feature's own default stub — reused here rather than
        // shipping a second copy of the same placeholder video.
        private const string DefaultStubResourceName = "ManageComingSoon.comingsoon.mp4";
        private const string DefaultStubCacheFileName = "radarr-stub-default.mp4";

        private static readonly string[] ValidVideoExtensions = { ".mp4", ".mkv", ".avi", ".mov" };

        private readonly IApplicationPaths appPaths;
        private readonly RadarrClient radarrClient;
        private readonly TmdbService tmdbService;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;

        public RadarrComingSoonChannel(
            IApplicationPaths appPaths,
            RadarrClient radarrClient,
            TmdbService tmdbService,
            IJsonSerializer json,
            ILogger logger)
        {
            this.appPaths = appPaths;
            this.radarrClient = radarrClient;
            this.tmdbService = tmdbService;
            this.json = json;
            this.logger = logger;
        }

        public string Name => "Radarr Coming Soon";

        public string Description => "Movies currently monitored in Radarr that have not yet been downloaded.";

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return Array.Empty<ImageType>();
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            // GetSupportedChannelImages() returns an empty list, so Emby
            // should never actually call this.
            return Task.FromResult<DynamicImageResponse>(null);
        }

        // Suspected actual mechanism for channel playback: Emby's
        // PlaybackInfo request appears to call this at request time rather
        // than reading ChannelItemInfo.MediaSources populated up front in
        // GetChannelItems. UNCONFIRMED until tested — this is the first
        // thing to check if "No compatible streams" persists.
        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        {
            var config = ManageComingSoonPlugin.Instance.Configuration;

            string stubVideoPath = config.RadarrSyncMode == RadarrSyncMode.Cached
                ? ReadCache().StubVideoPath
                : ResolveStubVideoPath(config, appPaths, logger);

            logger.Info(
                "ManageComingSoon: GetChannelItemMediaInfo called for Id='{0}'. Resolved stubVideoPath='{1}', Exists={2}.",
                id, stubVideoPath, !string.IsNullOrEmpty(stubVideoPath) && File.Exists(stubVideoPath));

            if (string.IsNullOrEmpty(stubVideoPath) || !File.Exists(stubVideoPath))
            {
                logger.Warn("ManageComingSoon: GetChannelItemMediaInfo returning empty list for Id='{0}' — no valid stub path.", id);
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
            }

            var source = BuildMediaSource(id, stubVideoPath);

            logger.Info(
                "ManageComingSoon: GetChannelItemMediaInfo returning source — Id='{0}', Path='{1}', Protocol={2}, Container='{3}', SupportsDirectPlay={4}.",
                source.Id, source.Path, source.Protocol, source.Container, source.SupportsDirectPlay);

            return Task.FromResult<IEnumerable<MediaSourceInfo>>(new List<MediaSourceInfo> { source });
        }

        public async Task<ChannelItemResult> GetChannelItems(
            InternalChannelItemQuery query,
            CancellationToken cancellationToken)
        {
            var config = ManageComingSoonPlugin.Instance.Configuration;

            if (!config.RadarrEnabled)
            {
                return new ChannelItemResult
                {
                    Items = new List<ChannelItemInfo>(),
                    TotalRecordCount = 0
                };
            }

            List<RadarrChannelCacheItem> sourceItems;
            string stubVideoPath;

            if (config.RadarrSyncMode == RadarrSyncMode.Live)
            {
                var liveMovies = await radarrClient
                    .GetComingSoonMoviesAsync(config, cancellationToken)
                    .ConfigureAwait(false);

                if (liveMovies == null)
                {
                    // Radarr call failed. Per the "must get a successful
                    // response rather than no response" rule, this must NOT
                    // be treated as "nothing qualifies" — fall back to the
                    // last known-good cache instead of returning an empty list.
                    logger.Warn("ManageComingSoon: Radarr live call failed; showing last known state instead of an empty channel.");
                    var cache = ReadCache();
                    sourceItems = cache.Items;
                    stubVideoPath = cache.StubVideoPath;
                }
                else
                {
                    sourceItems = liveMovies.Select(ToCacheItem).ToList();
                    // Live mode has no scheduled task pre-resolving this, so
                    // resolve directly. Cheap in the common case (File.Exists
                    // short-circuit) — only does real I/O the first time or
                    // after the configured path changes.
                    stubVideoPath = ResolveStubVideoPath(config, appPaths, logger);
                }
            }
            else
            {
                var cache = ReadCache();
                sourceItems = cache.Items;
                stubVideoPath = cache.StubVideoPath;
            }

            var channelItems = new List<ChannelItemInfo>(sourceItems.Count);
            foreach (var item in sourceItems)
            {
                channelItems.Add(await ToChannelItemInfoAsync(item, config, stubVideoPath, cancellationToken).ConfigureAwait(false));
            }

            logger.Info("ManageComingSoon: GetChannelItems ({0} mode) returning {1} item(s). stubVideoPath='{2}'.", config.RadarrSyncMode, channelItems.Count, stubVideoPath);

            foreach (var ci in channelItems)
            {
                var sourcesDesc = ci.MediaSources == null || ci.MediaSources.Count == 0
                    ? "(none)"
                    : string.Join("; ", ci.MediaSources.Select(s => string.Format(
                        "Path='{0}', Protocol={1}, Container='{2}'", s.Path, s.Protocol, s.Container)));

                logger.Info(
                    "ManageComingSoon:   Item Id='{0}', Name='{1}', MediaSources=[{2}].",
                    ci.Id, ci.Name, sourcesDesc);
            }

            return new ChannelItemResult
            {
                Items = channelItems,
                TotalRecordCount = channelItems.Count
            };
        }

        // -----------------------------------------------------------------
        // ISupportsDelete — kept as a safety toggle for user-initiated
        // deletes from Emby's own UI. Confirmed NOT required for the normal
        // Radarr sync's add/remove reconciliation, which works purely off
        // whatever GetChannelItems returns each run.
        // -----------------------------------------------------------------

        public bool CanDelete(BaseItem item)
        {
            var config = ManageComingSoonPlugin.Instance.Configuration;
            return config.RadarrEnabled && config.RadarrEnableDelete;
        }

        public Task DeleteItem(string id, CancellationToken cancellationToken)
        {
            var cache = ReadCache();
            var removed = cache.Items.RemoveAll(i => BuildItemId(i.TmdbId) == id);

            if (removed > 0)
            {
                WriteCache(cache);
                logger.Info("ManageComingSoon: Radarr channel item {0} removed via user-initiated delete.", id);
            }

            return Task.CompletedTask;
        }

        // -----------------------------------------------------------------
        // Cache read/write — shared with RadarrChannelSyncTask, which is the
        // only writer in Cached mode.
        // -----------------------------------------------------------------

        internal RadarrChannelCache ReadCache()
        {
            var path = GetCachePath();

            if (!File.Exists(path))
            {
                return new RadarrChannelCache { LastSyncSucceeded = false };
            }

            try
            {
                var text = File.ReadAllText(path);
                return json.DeserializeFromString<RadarrChannelCache>(text)
                    ?? new RadarrChannelCache { LastSyncSucceeded = false };
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to read Radarr channel cache at {0}", ex, path);
                return new RadarrChannelCache { LastSyncSucceeded = false };
            }
        }

        internal void WriteCache(RadarrChannelCache cache)
        {
            var path = GetCachePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var text = json.SerializeToString(cache);
            File.WriteAllText(path, text);
        }

        private string GetCachePath()
        {
            // STILL UNCONFIRMED (#5) — see header note.
            return Path.Combine(appPaths.DataPath, "manage-coming-soon", CacheFileName);
        }

        // -----------------------------------------------------------------
        // Stub video resolution — shared logic, called by both the
        // scheduled task (Cached mode: resolved once per run, stored in
        // cache) and this class directly (Live mode: no cache to rely on).
        // -----------------------------------------------------------------

        /// <summary>
        /// Resolves the on-disk path of the placeholder video every channel
        /// item's MediaSources should point at. Prefers the user's configured
        /// RadarrStubVideoPath if it's set and valid; otherwise extracts the
        /// plugin's embedded default stub to a persistent location once (and
        /// reuses it thereafter — no repeated extraction). Returns empty
        /// string if nothing usable could be resolved (channel items then
        /// simply have no MediaSources, matching prior behavior).
        /// </summary>
        internal static string ResolveStubVideoPath(PluginConfiguration config, IApplicationPaths appPaths, ILogger logger)
        {
            var configuredPath = (config.RadarrStubVideoPath ?? string.Empty).Trim();

            if (!string.IsNullOrEmpty(configuredPath))
            {
                var ext = Path.GetExtension(configuredPath).ToLowerInvariant();
                bool validExt = ValidVideoExtensions.Any(v => string.Equals(v, ext, StringComparison.OrdinalIgnoreCase));

                if (validExt && File.Exists(configuredPath))
                {
                    return configuredPath;
                }

                logger.Warn(
                    "ManageComingSoon: Configured RadarrStubVideoPath '{0}' is invalid or missing — falling back to the default placeholder.",
                    configuredPath);
            }

            var defaultPath = Path.Combine(appPaths.DataPath, "manage-coming-soon", DefaultStubCacheFileName);

            if (File.Exists(defaultPath))
            {
                return defaultPath;
            }

            try
            {
                var dir = Path.GetDirectoryName(defaultPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var asm = typeof(ManageComingSoonPlugin).Assembly;
                using (var resourceStream = asm.GetManifestResourceStream(DefaultStubResourceName))
                {
                    if (resourceStream == null)
                    {
                        logger.Warn("ManageComingSoon: Embedded default stub resource '{0}' not found — Radarr channel items will have no playable source.", DefaultStubResourceName);
                        return string.Empty;
                    }

                    using (var fileStream = File.Create(defaultPath))
                    {
                        resourceStream.CopyTo(fileStream);
                    }
                }

                logger.Info("ManageComingSoon: Extracted default Radarr stub video to {0}.", defaultPath);
                return defaultPath;
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to extract default Radarr stub video", ex);
                return string.Empty;
            }
        }

        // -----------------------------------------------------------------
        // Mapping helpers
        // -----------------------------------------------------------------

        private static string BuildItemId(int tmdbId) => IdPrefix + tmdbId;

        private static RadarrChannelCacheItem ToCacheItem(Services.Models.RadarrMovie movie)
        {
            string posterUrl = movie.Images?
                .FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?
                .RemoteUrl;

            return new RadarrChannelCacheItem
            {
                RadarrId = movie.Id,
                TmdbId = movie.TmdbId,
                ImdbId = movie.ImdbId,
                Title = movie.Title,
                OriginalTitle = movie.OriginalTitle,
                Year = movie.Year,
                Overview = movie.Overview,
                PosterUrl = posterUrl
            };
        }

        private async Task<ChannelItemInfo> ToChannelItemInfoAsync(
            RadarrChannelCacheItem item,
            PluginConfiguration config,
            string stubVideoPath,
            CancellationToken cancellationToken)
        {
            var posterUrl = item.PosterUrl;

            // Only fall back to TMDB when Radarr genuinely didn't give us a
            // poster — this should be the exception, not the default path,
            // since Radarr usually already provides a RemoteUrl for the
            // poster CoverType.
            if (string.IsNullOrEmpty(posterUrl) && item.TmdbId > 0 && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                var details = await tmdbService
                    .GetMovieDetailsAsync(config.TmdbApiKey, item.TmdbId, cancellationToken)
                    .ConfigureAwait(false);

                if (details != null && !string.IsNullOrEmpty(details.PosterPath))
                {
                    posterUrl = "https://image.tmdb.org/t/p/original" + details.PosterPath;
                }
            }

            var itemId = BuildItemId(item.TmdbId);

            var info = new ChannelItemInfo
            {
                Id = itemId,
                Name = item.Title,
                OriginalTitle = item.OriginalTitle,
                Overview = item.Overview,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Movie,
                ProductionYear = item.Year > 0 ? item.Year : (int?)null,
                ImageUrl = posterUrl
            };

            // STILL UNCONFIRMED (#4) — see header note.
            if (item.TmdbId > 0)
                info.ProviderIds["Tmdb"] = item.TmdbId.ToString();
            if (!string.IsNullOrEmpty(item.ImdbId))
                info.ProviderIds["Imdb"] = item.ImdbId;
            if (item.RadarrId > 0)
                info.ProviderIds["RadarrId"] = item.RadarrId.ToString();

            // ASSUMPTION #9 — MediaSourceInfo's exact member set/namespace is
            // a best guess (MediaBrowser.Model.Dto / MediaBrowser.Model.MediaInfo),
            // not confirmed against this SDK version. Populated here too as a
            // secondary hint, though GetChannelItemMediaInfo (see above) is the
            // suspected actual mechanism Emby uses at playback time.
            if (!string.IsNullOrEmpty(stubVideoPath))
            {
                info.MediaSources = new List<MediaSourceInfo> { BuildMediaSource(itemId, stubVideoPath) };
            }

            return info;
        }

        private static MediaSourceInfo BuildMediaSource(string itemId, string stubVideoPath)
        {
            return new MediaSourceInfo
            {
                Id = itemId,
                Path = stubVideoPath,
                Protocol = MediaProtocol.File,
                Container = Path.GetExtension(stubVideoPath).TrimStart('.').ToLowerInvariant(),
                IsRemote = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                Name = "Coming Soon"
            };
        }
    }
}