// ManageComingSoon - TMDB Service
// Uses Emby's IHttpClient to fetch JSON, then reads the stream to a string
// before deserialising — this avoids any double-read issues with the stream.
// Property names on the POCO classes match TMDB's JSON keys exactly as
// ServiceStack.Text expects them (it maps snake_case keys to PascalCase
// properties case-insensitively, so release_date -> ReleaseDate etc).

namespace ManageComingSoon.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using ManageComingSoon.Model;
    using MediaBrowser.Common.Net;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Serialization;

    public class TmdbService
    {
        private const string BaseUrl = "https://api.themoviedb.org/3";

        private readonly IHttpClient httpClient;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;

        public TmdbService(IHttpClient httpClient, IJsonSerializer json, ILogger logger)
        {
            this.httpClient = httpClient;
            this.json = json;
            this.logger = logger;
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        public async Task<List<TmdbMovieResult>> SearchAsync(
            string apiKey,
            string movieName,
            int? year,
            CancellationToken token = default(CancellationToken))
        {
            var results = new List<TmdbMovieResult>();

            // Primary search (with year if supplied)
            var primary = await SearchMovieAsync(apiKey, movieName, year, token).ConfigureAwait(false);
            MergeInto(results, primary);

            // Also search without year so ±1 candidates aren't missed
            if (year.HasValue)
            {
                var noYear = await SearchMovieAsync(apiKey, movieName, null, token).ConfigureAwait(false);
                MergeInto(results, noYear);
            }

            // Boost results that match via alternative titles
            var altBoostIds = new HashSet<int>();
            foreach (var r in results.ToList())
            {
                var alts = await GetAltTitlesAsync(apiKey, r.Id, token).ConfigureAwait(false);
                if (alts.Any(a => string.Equals(a, movieName, StringComparison.OrdinalIgnoreCase)))
                    altBoostIds.Add(r.Id);
            }

            // Score and sort — most confident first
            int currentYear = DateTime.UtcNow.Year;
            return results
                .Select(r => new { R = r, S = Score(r, movieName, year, currentYear, altBoostIds) })
                .OrderByDescending(x => x.S)
                .Select(x => x.R)
                .ToList();
        }

        public bool IsConfidentMatch(List<TmdbMovieResult> results, string movieName, int? year)
        {
            if (results.Count == 0) return false;
            var top = results[0];

            // A candidate with no release year on TMDB is a poor candidate by
            // definition (see HandleManual's rationale) and must never be
            // treated as a year match, regardless of whether the user supplied
            // a year — it always falls through to "Multiple matches" for the
            // user to confirm by hand instead of auto-confirming.
            bool yearMatch =
                top.ReleaseYear > 0 &&
                (!year.HasValue || Math.Abs(top.ReleaseYear - year.Value) <= 1);

            // A single TMDB hit IS the match by definition — don't gate it behind
            // an exact title-string comparison. Real-world titles routinely differ
            // from what a user types by punctuation alone (e.g. "Dune Part Two"
            // vs TMDB's "Dune: Part Two"), which used to fail this check and send
            // a 1-candidate result down the "Multiple matches" path instead of
            // being auto-confirmed.
            if (results.Count == 1 && yearMatch) return true;

            bool titleMatch =
                TitlesRoughlyEqual(top.Title, movieName) ||
                TitlesRoughlyEqual(top.OriginalTitle, movieName);

            if (titleMatch && year.HasValue && top.ReleaseYear == year.Value) return true;
            if (titleMatch && yearMatch && results.Count >= 2)
            {
                double ratio = results[1].Popularity > 0
                    ? top.Popularity / results[1].Popularity : 10.0;
                if (ratio > 3.0) return true;
            }
            return false;
        }

        // Compares titles ignoring case, punctuation, and whitespace so
        // "Dune Part Two" matches TMDB's "Dune: Part Two", "Spider-Man" matches
        // "Spider Man", etc. Letters/digits only, lowercased.
        private static bool TitlesRoughlyEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return NormaliseForCompare(a) == NormaliseForCompare(b);
        }

        private static string NormaliseForCompare(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// Fetches the top 3 cast member names for a movie from TMDB /movie/{id}/credits.
        /// Returns an empty list on any failure — never throws.
        /// </summary>
        public async Task<List<string>> GetCastAsync(
            string apiKey, int tmdbId,
            CancellationToken token = default(CancellationToken))
        {
            string url = string.Format("{0}/movie/{1}/credits?api_key={2}",
                BaseUrl, tmdbId, Uri.EscapeDataString(apiKey));

            string raw = await FetchStringAsync(url, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw)) return new List<string>();

            try
            {
                var wrapper = this.json.DeserializeFromString<TmdbCreditsWrapper>(raw);
                if (wrapper == null || wrapper.Cast == null) return new List<string>();

                return wrapper.Cast
                    .Where(c => !string.IsNullOrEmpty(c.Name))
                    .Take(3)
                    .Select(c => c.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                this.logger.ErrorException(
                    "ManageComingSoon: Failed to parse TMDB credits for movie {0}", ex, tmdbId);
                return new List<string>();
            }
        }

        // -----------------------------------------------------------------------
        // Private: HTTP fetch helpers
        // -----------------------------------------------------------------------

        private async Task<List<TmdbMovieResult>> SearchMovieAsync(
            string apiKey, string query, int? year, CancellationToken token)
        {
            string url = string.Format(
                "{0}/search/movie?api_key={1}&query={2}&include_adult=false",
                BaseUrl,
                Uri.EscapeDataString(apiKey),
                Uri.EscapeDataString(query));

            if (year.HasValue)
                url += "&primary_release_year=" + year.Value;

            string raw = await FetchStringAsync(url, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw)) return new List<TmdbMovieResult>();

            try
            {
                var wrapper = this.json.DeserializeFromString<TmdbSearchWrapper>(raw);
                return wrapper != null && wrapper.Results != null
                    ? wrapper.Results
                    : new List<TmdbMovieResult>();
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: Failed to parse TMDB search response", ex);
                return new List<TmdbMovieResult>();
            }
        }

        private async Task<List<string>> GetAltTitlesAsync(
            string apiKey, int movieId, CancellationToken token)
        {
            string url = string.Format("{0}/movie/{1}/alternative_titles?api_key={2}",
                BaseUrl, movieId, Uri.EscapeDataString(apiKey));

            string raw = await FetchStringAsync(url, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw)) return new List<string>();

            try
            {
                var wrapper = this.json.DeserializeFromString<TmdbAltTitlesWrapper>(raw);
                if (wrapper == null || wrapper.Titles == null) return new List<string>();
                var list = new List<string>();
                foreach (var t in wrapper.Titles)
                    if (!string.IsNullOrEmpty(t.Title))
                        list.Add(t.Title);
                return list;
            }
            catch
            {
                return new List<string>();
            }
        }

        private async Task<string> FetchStringAsync(string url, CancellationToken token)
        {
            try
            {
                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = token,
                    TimeoutMs = 15000,
                    LogErrors = true,
                    ThrowOnErrorResponse = true
                };

                using (var response = await this.httpClient.Get(options).ConfigureAwait(false))
                using (var reader = new StreamReader(response))
                {
                    return await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("ManageComingSoon: HTTP fetch failed for {0}", ex, url);
                return string.Empty;
            }
        }

        // -----------------------------------------------------------------------
        // Scoring
        // -----------------------------------------------------------------------
        // Design goals:
        //   • Title signals dominate (exact match is almost certainly right)
        //   • Year is a strong secondary signal — exact match gets a big boost
        //   • Word-level Jaccard similarity handles partial/reordered titles
        //   • Recency bonus when no year given (user likely means a new release)
        //   • Popularity (log-scaled) breaks ties without drowning title/year
        // -----------------------------------------------------------------------

        private static double Score(
            TmdbMovieResult r,
            string query,
            int? year,
            int currentYear,
            HashSet<int> altBoostIds)
        {
            double score = 0;

            string rTitle = r.Title ?? string.Empty;
            string rOrig = r.OriginalTitle ?? string.Empty;

            // ---- Title matching -----------------------------------------------

            // Exact full title
            if (string.Equals(rTitle, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rOrig, query, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else
            {
                // Title starts with query
                if (rTitle.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    score += 40;
                else if (rTitle.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 20;

                // Word-level matching on the best of title / original title
                double jaccardTitle = JaccardWordScore(rTitle, query);
                double jaccardOrig = JaccardWordScore(rOrig, query);
                double bestJaccard = Math.Max(jaccardTitle, jaccardOrig);

                // All query words present in title (any order)
                var queryWords = TokeniseWords(query);
                var titleWords = TokeniseWords(rTitle);
                bool allWordsPresent = queryWords.Count > 0
                    && queryWords.All(w => titleWords.Contains(w));

                if (allWordsPresent)
                    score += 60;
                else if (bestJaccard >= 0.5)
                    score += 30;

                score += bestJaccard * 25;
            }

            // ---- Alt title boost ---------------------------------------------
            if (altBoostIds.Contains(r.Id)) score += 30;

            // ---- Year matching -----------------------------------------------
            if (year.HasValue && r.ReleaseYear > 0)
            {
                int diff = Math.Abs(r.ReleaseYear - year.Value);
                if (diff == 0) score += 80;
                else if (diff == 1) score += 30;
                else if (diff == 2) score += 10;
                else score -= 15 * (diff - 2);
            }
            else if (!year.HasValue && r.ReleaseYear > 0)
            {
                // No year given — favour recent releases (within 2 years of now)
                int ageDiff = Math.Abs(r.ReleaseYear - currentYear);
                if (ageDiff == 0) score += 20;
                else if (ageDiff == 1) score += 15;
                else if (ageDiff == 2) score += 5;
            }

            // ---- Popularity (log-scaled tie-breaker) -------------------------
            score += Math.Log(Math.Max(r.Popularity, 1));

            return score;
        }

        // -----------------------------------------------------------------------
        // Word-level Jaccard similarity
        // = |intersection| / |union| of word sets (case-insensitive)
        // -----------------------------------------------------------------------

        private static double JaccardWordScore(string a, string b)
        {
            var wordsA = TokeniseWords(a);
            var wordsB = TokeniseWords(b);
            if (wordsA.Count == 0 || wordsB.Count == 0) return 0.0;

            int intersection = wordsA.Intersect(wordsB).Count();
            int union = wordsA.Union(wordsB).Count();
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        private static HashSet<string> TokeniseWords(string text)
        {
            if (string.IsNullOrEmpty(text)) return new HashSet<string>();
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                }
                else if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0) words.Add(current.ToString());
            return words;
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static void MergeInto(List<TmdbMovieResult> target, List<TmdbMovieResult> source)
        {
            foreach (var r in source)
                if (target.All(x => x.Id != r.Id))
                    target.Add(r);
        }

        // -----------------------------------------------------------------------
        // Private POCO types for JSON deserialisation
        // -----------------------------------------------------------------------

        private class TmdbSearchWrapper
        {
            public List<TmdbMovieResult> Results { get; set; }
        }

        private class TmdbAltTitlesWrapper
        {
            public List<TmdbAltTitle> Titles { get; set; }
        }

        private class TmdbAltTitle
        {
            public string Title { get; set; }
        }

        private class TmdbCreditsWrapper
        {
            public List<TmdbCastMember> Cast { get; set; }
        }

        private class TmdbCastMember
        {
            public string Name { get; set; }
        }
    }
}