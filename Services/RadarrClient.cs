using ManageComingSoon.Services.Models;
using ManageComingSoon.Services.Rules;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private readonly RadarrRuleSetStore ruleSetStore;
        private readonly IApplicationPaths appPaths;

        public RadarrClient(
            IHttpClient httpClient,
            IJsonSerializer jsonSerializer,
            ILogger logger,
            RadarrRuleSetStore ruleSetStore,
            IApplicationPaths appPaths)
        {
            this.httpClient = httpClient;
            this.json = jsonSerializer;
            this.logger = logger;
            this.ruleSetStore = ruleSetStore;
            this.appPaths = appPaths;
        }

        private async Task<(List<RadarrMovie> Typed, string RawJson)> GetAllMoviesWithRawAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.RadarrApiKey))
            {
                logger.Warn("Radarr API key has not been configured.");
                return (null, null);
            }

            var baseUrl = config.RadarrUrl.TrimEnd('/') + "/api/v3/movie";
            var url = baseUrl + "?apikey=" + Uri.EscapeDataString(config.RadarrApiKey);
            logger.Info("Querying Radarr: {0}", baseUrl);

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken
            };
            options.RequestHeaders["X-Api-Key"] = config.RadarrApiKey;

            try
            {
                using (var response = await httpClient.GetResponse(options).ConfigureAwait(false))
                using (var stream = response.Content)
                using (var reader = new StreamReader(stream))
                {
                    var jsonText = await reader.ReadToEndAsync().ConfigureAwait(false);

                    logger.Debug(
                        "ManageComingSoon: Radarr response — status {0}, {1} bytes. Body (first 10000 chars): {2}",
                        response.StatusCode,
                        jsonText.Length,
                        jsonText.Length > 10000 ? jsonText.Substring(0, 10000) + "...(truncated)" : jsonText);

                    var movies = json.DeserializeFromString<List<RadarrMovie>>(jsonText);

                    if (movies == null)
                    {
                        logger.Warn("ManageComingSoon: Radarr response could not be parsed into a movie list. Raw body logged above at Debug level.");
                        return (null, jsonText);
                    }

                    logger.Info("ManageComingSoon: Radarr returned {0} movies.", movies.Count);

                    foreach (var m in movies)
                    {
                        logger.Debug(
                            "ManageComingSoon: Radarr movie — TmdbId={0}, TitleSlug={1}, Title='{2}', Monitored={3}, HasFile={4}.",
                            m.TmdbId, m.TitleSlug, m.Title, m.Monitored, m.HasFile);
                    }

                    return (movies, jsonText);
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Radarr call failed for {0}", ex, baseUrl);
                return (null, null);
            }
        }

        public async Task<IReadOnlyList<RadarrMovie>> GetMissingMoviesAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var (movies, _) = await GetAllMoviesWithRawAsync(config, cancellationToken).ConfigureAwait(false);
            if (movies == null) return Array.Empty<RadarrMovie>();

            return movies
                .Where(i => i.Monitored)
                .Where(i => !i.HasFile)
                .OrderBy(i => i.Title)
                .ToList();
        }

        public async Task<IReadOnlyList<RadarrMovie>> GetComingSoonMoviesAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var (typed, rawJson) = await GetAllMoviesWithRawAsync(config, cancellationToken).ConfigureAwait(false);
            if (typed == null || rawJson == null) return null;

            WriteLastResponseSnapshot(rawJson);

            var ruleRoot = ruleSetStore.GetActiveRuleRoot();

            try
            {
                using (var doc = JsonDocument.Parse(rawJson))
                {
                    var matchedIds = new HashSet<int>(
                        doc.RootElement.EnumerateArray()
                            .Where(el => RuleEvaluator.Matches(el, ruleRoot))
                            .Select(el => el.GetProperty("id").GetInt32()));

                    logger.Info(
                        "ManageComingSoon: Rule evaluation matched {0} of {1} Radarr movies.",
                        matchedIds.Count, typed.Count);

                    return typed
                        .Where(m => matchedIds.Contains(m.Id))
                        .OrderBy(m => m.Title)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Rule evaluation failed against Radarr response — falling back to no items rather than an unfiltered list", ex);
                return Array.Empty<RadarrMovie>();
            }
        }

        private void WriteLastResponseSnapshot(string rawJson)
        {
            try
            {
                var path = Path.Combine(appPaths.DataPath, "manage-coming-soon", "radarr-last-response.json");
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, rawJson);
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to write radarr-last-response.json snapshot", ex);
            }
        }
    }
}