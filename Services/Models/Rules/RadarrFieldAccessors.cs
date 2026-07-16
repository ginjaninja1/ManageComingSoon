namespace ManageComingSoon.Services.Rules
{
    using ManageComingSoon.Services.Models;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public static class RadarrFieldAccessors
    {
        public static readonly Dictionary<string, Func<RadarrMovie, object>> Fields =
            new Dictionary<string, Func<RadarrMovie, object>>(StringComparer.OrdinalIgnoreCase)
            {
                ["monitored"] = m => m.Monitored,
                ["hasFile"] = m => m.HasFile,
                ["year"] = m => m.Year,
                ["runtime"] = m => m.Runtime,
                ["tmdbId"] = m => m.TmdbId,
                ["imdbId"] = m => m.ImdbId,
                ["title"] = m => m.Title,
                ["originalTitle"] = m => m.OriginalTitle,
                ["overview"] = m => m.Overview,
                ["certification"] = m => m.Certification,
                ["titleSlug"] = m => m.TitleSlug,
                ["genres"] = m => m.Genres,
                ["studios"] = m => m.Studios?.Select(s => s.Name).ToList(),
                ["images"] = m => m.Images?.Select(i => i.CoverType).ToList(),
                ["ratings.imdb.value"] = m => m.Ratings?.Imdb?.Value,
                ["ratings.tmdb.value"] = m => m.Ratings?.Tmdb?.Value,
                ["ratings.rottenTomatoes.value"] = m => m.Ratings?.RottenTomatoes?.Value,
                ["ratings.metacritic.value"] = m => m.Ratings?.Metacritic?.Value,
                ["ratings.imdb.votes"] = m => m.Ratings?.Imdb?.Votes,
                ["ratings.tmdb.votes"] = m => m.Ratings?.Tmdb?.Votes,
            };
    }
}