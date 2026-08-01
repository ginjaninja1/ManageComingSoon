namespace ManageComingSoon.Model
{
    public enum ComingSoonMediaType
    {
        Movie = 0,
        TvShow = 1
    }

    public static class ComingSoonMediaTypeExtensions
    {
        public static string DisplayName(this ComingSoonMediaType mediaType)
            => mediaType == ComingSoonMediaType.TvShow ? "TV Show" : "Movie";

        public static string TmdbPathSegment(this ComingSoonMediaType mediaType)
            => mediaType == ComingSoonMediaType.TvShow ? "tv" : "movie";
    }
}
