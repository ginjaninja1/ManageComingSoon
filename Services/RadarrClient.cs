using ManageComingSoon.Services.Models;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManageComingSoon.Model;

namespace ManageComingSoon.Services
{
    public class RadarrClient
    {
        private readonly IHttpClient httpClient;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;

        public RadarrClient(
            IHttpClient httpClient,
            IJsonSerializer jsonSerializer,
            ILogger logger)
        {
            this.httpClient = httpClient;
            this.json = jsonSerializer;
            this.logger = logger;
        }

        // Returns null (not an empty list) on any failure to reach/parse
        // Radarr's response — this distinction matters. Callers must treat
        // null as "sync skipped, leave existing state untouched" and must
        // NEVER treat it the same as an empty-but-successful result, which
        // would otherwise look identical to "Radarr says nothing qualifies
        // any more" and cause everything to be wrongly removed.
        private async Task<List<RadarrMovie>> GetAllMoviesAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.RadarrApiKey))
            {
                logger.Warn("Radarr API key has not been configured.");
                return null;
            }

            var baseUrl = config.RadarrUrl.TrimEnd('/') + "/api/v3/movie";
            var url = baseUrl + "?apikey=" + Uri.EscapeDataString(config.RadarrApiKey);
            logger.Info("Querying Radarr: {0}", baseUrl);

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken
            };
            // Sent both ways — some Radarr setups/reverse proxies only
            // respect one or the other.
            options.RequestHeaders["X-Api-Key"] = config.RadarrApiKey;

            try
            {
                using (var response = await httpClient.GetResponse(options).ConfigureAwait(false))
                using (var stream = response.Content)
                using (var reader = new StreamReader(stream))
                {
                    var jsonText = await reader.ReadToEndAsync().ConfigureAwait(false);

                    // Diagnostic detail — Debug only, not needed for normal
                    // operation now that the earlier integration issues are
                    // resolved. Truncated since a movie library's JSON can be
                    // large; this is for eyeballing shape/errors, not an audit
                    // trail.
                    logger.Debug(
                        "ManageComingSoon: Radarr response — status {0}, {1} bytes. Body (first 2000 chars): {2}",
                        response.StatusCode,
                        jsonText.Length,
                        jsonText.Length > 2000 ? jsonText.Substring(0, 2000) + "...(truncated)" : jsonText);

                    var movies = json.DeserializeFromString<List<RadarrMovie>>(jsonText);

                    if (movies == null)
                    {
                        logger.Warn("ManageComingSoon: Radarr response could not be parsed into a movie list. Raw body logged above at Debug level.");
                        return null;
                    }

                    logger.Info("ManageComingSoon: Radarr returned {0} movies.", movies.Count);

                    foreach (var m in movies)
                    {
                        logger.Debug(
                            "ManageComingSoon: Radarr movie — TmdbId={0}, Title='{1}', Monitored={2}, HasFile={3}.",
                            m.TmdbId, m.Title, m.Monitored, m.HasFile);
                    }

                    return movies;
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Radarr call failed for {0}", ex, baseUrl);
                return null;
            }
        }

        // Existing behaviour, unchanged: monitored + no file yet, sorted by
        // title. Kept exactly as-is since other parts of the plugin may
        // already depend on this specific method name/behaviour.
        public async Task<IReadOnlyList<RadarrMovie>> GetMissingMoviesAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var movies = await GetAllMoviesAsync(config, cancellationToken).ConfigureAwait(false);
            if (movies == null) return Array.Empty<RadarrMovie>();

            return movies
                .Where(i => i.Monitored)
                .Where(i => !i.HasFile)
                .OrderBy(i => i.Title)
                .ToList();
        }

        // "Coming soon" for the Radarr channel: monitored, not yet
        // fulfilled by Radarr. Confirmed this is the exact same definition
        // as "missing" above — deliberately not duplicating the filter
        // logic, just giving the channel/sync code a name that matches its
        // own vocabulary. Returns null (not empty) on failure — see
        // GetAllMoviesAsync's doc comment; the scheduled task and the
        // channel's Live-mode path must both branch on null explicitly and
        // must never treat it as "zero movies qualify".
        public async Task<IReadOnlyList<RadarrMovie>> GetComingSoonMoviesAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var movies = await GetAllMoviesAsync(config, cancellationToken).ConfigureAwait(false);
            if (movies == null) return null;

            return movies
                .Where(i => i.Monitored)
                .Where(i => !i.HasFile)
                .OrderBy(i => i.Title)
                .ToList();
        }
    }
}