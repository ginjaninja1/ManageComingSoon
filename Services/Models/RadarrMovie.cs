using System.Collections.Generic;

namespace ManageComingSoon.Services.Models
{
    public class RadarrMovie
    {
        public int Id { get; set; }

        public string Title { get; set; }

        public string OriginalTitle { get; set; }

        public int Year { get; set; }

        public string Overview { get; set; }

        public bool Monitored { get; set; }

        public bool HasFile { get; set; }

        public int TmdbId { get; set; }

        public string ImdbId { get; set; }

        public string Certification { get; set; }

        public long? Runtime { get; set; }

        public List<string> Genres { get; set; } = new List<string>();

        public List<RadarrImage> Images { get; set; } = new List<RadarrImage>();

        public List<RadarrStudio> Studios { get; set; } = new List<RadarrStudio>();

        public RadarrRatings Ratings { get; set; }
    }

    public class RadarrRatings
    {
        public RadarrRating Imdb { get; set; }

        public RadarrRating Tmdb { get; set; }

        public RadarrRating RottenTomatoes { get; set; }

        public RadarrRating Metacritic { get; set; }
    }

    public class RadarrRating
    {
        public float? Value { get; set; }

        public int Votes { get; set; }
    }

    public class RadarrImage
    {
        public string CoverType { get; set; }

        public string Url { get; set; }

        public string RemoteUrl { get; set; }
    }

    public class RadarrStudio
    {
        public string Name { get; set; }
    }
}