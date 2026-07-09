namespace ManageComingSoon.Model
{
    using System.Runtime.Serialization;

    // TMDB JSON field names (confirmed from working PowerShell script):
    //   id, title, original_title, overview, release_date, poster_path, popularity
    //
    // ServiceStack.Text maps JSON keys to C# properties case-insensitively,
    // stripping underscores, so release_date -> ReleaseDate etc.
    // We add [DataMember(Name=...)] as an explicit fallback hint.
    [DataContract]
    public class TmdbMovieResult
    {
        [DataMember(Name = "id")]
        public int Id { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; } = string.Empty;

        [DataMember(Name = "original_title")]
        public string OriginalTitle { get; set; } = string.Empty;

        [DataMember(Name = "overview")]
        public string Overview { get; set; } = string.Empty;

        // Search endpoint returns "release_date" (confirmed by PowerShell script)
        [DataMember(Name = "release_date")]
        public string ReleaseDate { get; set; } = string.Empty;

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
            => string.IsNullOrEmpty(PosterPath) ? string.Empty : baseUrl + PosterPath;
    }
}
