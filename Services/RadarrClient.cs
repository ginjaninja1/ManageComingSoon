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

        public async Task<IReadOnlyList<RadarrMovie>> GetMissingMoviesAsync(
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.RadarrApiKey))
            {
                logger.Warn("Radarr API key has not been configured.");
                return Array.Empty<RadarrMovie>();
            }

            var url = config.RadarrUrl.TrimEnd('/') + "/api/v3/movie";

            logger.Info("Querying Radarr: {0}", url);

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken
            };

            options.RequestHeaders["X-Api-Key"] = config.RadarrApiKey;

            using (var response = await httpClient.GetResponse(options).ConfigureAwait(false))
            using (var stream = response.Content)
            using (var reader = new StreamReader(stream))
            {
                var jsonText = await reader.ReadToEndAsync().ConfigureAwait(false);

                var movies = json.DeserializeFromString<List<RadarrMovie>>(jsonText)
                             ?? new List<RadarrMovie>();

                logger.Info("Radarr returned {0} movies.", movies.Count);

                return movies
                    .Where(i => i.Monitored)
                    .Where(i => !i.HasFile)
                    .OrderBy(i => i.Title)
                    .ToList();
            }
        }
    }
}