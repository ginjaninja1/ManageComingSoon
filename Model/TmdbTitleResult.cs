namespace ManageComingSoon.Model
{
    using System;
    using System.Runtime.Serialization;

    // TMDB JSON field names (confirmed from working PowerShell script):
    //   id, title, original_title, overview, release_date, poster_path, popularity
    //
    // ServiceStack.Text maps JSON keys to C# properties case-insensitively,
    // stripping underscores, so release_date -> ReleaseDate etc.
    // We add [DataMember(Name=...)] as an explicit fallback hint.
    //
    // NOTE: when populated via TmdbService.MapResult() (Emby's built-in TMDB
    // provider path) rather than the direct TMDB HTTP /search path, PosterPath
    // holds a full absolute URL (Emby's RemoteSearchResult.ImageUrl is already
    // baseImageUrl + poster_path — confirmed via decompiled MovieDbSearch.cs),
    // not a bare TMDB path like the direct-HTTP path returns. PosterUrl()
    // below handles both shapes.
    [DataContract]
    public class TmdbTitleResult
    {
        public ComingSoonMediaType MediaType { get; set; }
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; } = string.Empty;

        [DataMember(Name = "original_title")]
        public string OriginalTitle { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "original_name")]
        public string OriginalName { get; set; } = string.Empty;

        [DataMember(Name = "overview")]
        public string Overview { get; set; } = string.Empty;

        // Search endpoint returns "release_date" (confirmed by PowerShell script)
        [DataMember(Name = "release_date")]
        public string ReleaseDate { get; set; } = string.Empty;

        [DataMember(Name = "first_air_date")]
        public string FirstAirDate { get; set; } = string.Empty;

        [DataMember(Name = "poster_path")]
        public string PosterPath { get; set; } = string.Empty;

        [DataMember(Name = "popularity")]
        public double Popularity { get; set; }

        public int ReleaseYear
        {
            get
            {
                if (string.IsNullOrEmpty(ReleaseDate) || ReleaseDate.Length < 4)
                    return 0;
                int y;
                return int.TryParse(ReleaseDate.Substring(0, 4), out y) ? y : 0;
            }
        }

        public string PosterUrl(string baseUrl = "https://image.tmdb.org/t/p/w342")
        {
            if (string.IsNullOrEmpty(PosterPath)) return string.Empty;

            // Emby's built-in-provider path (TmdbService.MapResult) already
            // supplies a full absolute URL — pass it through as-is rather
            // than double-prefixing. The direct-HTTP path still gives a bare
            // TMDB path (e.g. "/abc123.jpg") that needs baseUrl prepended.
            return PosterPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? PosterPath
                : baseUrl + PosterPath;
        }

        public void Normalize(ComingSoonMediaType mediaType)
        {
            MediaType = mediaType;
            if (mediaType != ComingSoonMediaType.TvShow) return;
            Title = Name ?? string.Empty;
            OriginalTitle = OriginalName ?? string.Empty;
            ReleaseDate = FirstAirDate ?? string.Empty;
        }
    }
}