// STATUS (updated after checking dev.emby.media docs + Emby server source):
//   1. CONFIRMED — ManageComingSoonPlugin.Instance.Configuration, per the
//      actual Plugin.cs.
//   2. CONFIRMED — registration is automatic. Emby Server's own startup code
//      runs `ChannelManager.AddParts(GetExports<IChannel>())`, scanning all
//      loaded plugin assemblies for IChannel implementations. Nothing needs
//      to call AddParts from within this plugin — the commented-out
//      InitializePlugin code in Plugin.cs was unnecessary, not incomplete.
//      Same mechanism auto-discovers IScheduledTask (RadarrChannelSyncTask).
//   3. CONFIRMED — ChannelItemInfo/ChannelItemType/ChannelParentalRating live
//      in MediaBrowser.Controller.Channels; ChannelMediaType/
//      ChannelMediaContentType live in MediaBrowser.Model.Channels (both
//      already `using`'d below). ChannelParentalRating.GeneralAudience,
//      ChannelMediaType.Video, and ChannelMediaContentType.Movie all
//      confirmed against dev.emby.media's reference docs.
//   4. STILL UNCONFIRMED — ProviderIdDictionary key strings ("Tmdb"/"Imdb").
//   5. STILL UNCONFIRMED — IApplicationPaths.DataPath as the cache location;
//      swap for an existing storage convention in this codebase if one exists.
//   6. STILL OPEN — which removal strategy actually causes Emby to drop a
//      stale item is the explicit point of the RadarrRemovalStrategy flag;
//      see the chat note on why this only gates CanDelete rather than being
//      two truly distinct code paths.

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
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Providers;
    using MediaBrowser.Model.Serialization;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class RadarrComingSoonChannel : IChannel, ISupportsDelete
    {
        private const string IdPrefix = "radarr-coming-soon-";
        private const string CacheFileName = "radarr-channel-cache.json";

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

        // CONFIRMED against dev.emby.media reference docs.
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return Array.Empty<ImageType>();
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            // GetSupportedChannelImages() returns an empty list, so Emby
            // should never actually call this. Returning null rather than
            // guessing at DynamicImageResponse's real members.
            return Task.FromResult<DynamicImageResponse>(null);
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
                    sourceItems = ReadCache().Items;
                }
                else
                {
                    sourceItems = liveMovies.Select(ToCacheItem).ToList();
                }
            }
            else
            {
                sourceItems = ReadCache().Items;
            }

            var channelItems = new List<ChannelItemInfo>(sourceItems.Count);
            foreach (var item in sourceItems)
            {
                channelItems.Add(await ToChannelItemInfoAsync(item, config, cancellationToken).ConfigureAwait(false));
            }

            return new ChannelItemResult
            {
                Items = channelItems,
                TotalRecordCount = channelItems.Count
            };
        }

        // -----------------------------------------------------------------
        // ISupportsDelete — the "ExplicitDelete" removal strategy path.
        // Only reachable/meaningful when RadarrEnableDelete is on; CanDelete
        // gates it so Emby's own UI (or IChannelManager.DeleteItem) can't
        // remove an item unless the setting explicitly allows it.
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
                logger.Info("ManageComingSoon: Radarr channel item {0} removed via ExplicitDelete.", id);
            }

            return Task.CompletedTask;
        }

        // -----------------------------------------------------------------
        // Cache read/write — shared with RadarrChannelSyncTask, which is the
        // only writer in Cached mode. Kept internal-facing (not private) so
        // the scheduled task in the same assembly can read/write the same
        // file without duplicating the (de)serialization logic.
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
            // STILL UNCONFIRMED (#5) — confirm this is the right place to store plugin
            // state in this codebase; swap for an existing storage helper if
            // one is already established elsewhere.
            return Path.Combine(appPaths.DataPath, "manage-coming-soon", CacheFileName);
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

            var info = new ChannelItemInfo
            {
                Id = BuildItemId(item.TmdbId),
                Name = item.Title,
                OriginalTitle = item.OriginalTitle,
                Overview = item.Overview,
                Type = ChannelItemType.Media,
                MediaType = ChannelMediaType.Video, // CONFIRMED
                ContentType = ChannelMediaContentType.Movie, // CONFIRMED
                ProductionYear = item.Year > 0 ? item.Year : (int?)null,
                ImageUrl = posterUrl
            };

            // STILL UNCONFIRMED (#4) — confirm these key strings against the
            // MetadataProviders enum used elsewhere in this codebase.
            if (item.TmdbId > 0)
                info.ProviderIds["Tmdb"] = item.TmdbId.ToString();
            if (!string.IsNullOrEmpty(item.ImdbId))
                info.ProviderIds["Imdb"] = item.ImdbId;
            if (item.RadarrId > 0)
                info.ProviderIds["RadarrId"] = item.RadarrId.ToString();

            return info;
        }
    }
}