namespace ManageComingSoon.UI.UserAddMovie
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using ManageComingSoon.Model;

    internal enum UserMovieState
    {
        Searching,
        MultipleMatches,
        Ready,
        NoResults,
        SearchFailed,
        Submitted,
        Adding,
        Added,
        AddFailed
    }

    internal sealed class UserMovieCandidate
    {
        public TmdbMovieResult Movie { get; set; }
        public List<string> CastNames { get; set; }
    }

    internal sealed class UserMovieEntry
    {
        public string Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string SearchName { get; set; }
        public int? SearchYear { get; set; }
        public UserMovieState State { get; set; }
        public List<UserMovieCandidate> Candidates { get; set; }
            = new List<UserMovieCandidate>();
        public TmdbMovieResult SelectedMatch { get; set; }
        public bool IsManual { get; set; }
        public bool IncludedInBulkAdd { get; set; }
        public bool HasDestinationConflict { get; set; }
        public string ConflictReason { get; set; }
        public string DestinationPath { get; set; }
        public string GlobalTrackerId { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public string DisplayTitle
        {
            get
            {
                if (this.SelectedMatch != null && !string.IsNullOrWhiteSpace(this.SelectedMatch.Title))
                    return this.SelectedMatch.Title;
                return this.SearchName ?? string.Empty;
            }
        }

        public int DisplayYear
        {
            get
            {
                if (this.SelectedMatch != null && this.SelectedMatch.ReleaseYear > 0)
                    return this.SelectedMatch.ReleaseYear;
                return this.SearchYear ?? 0;
            }
        }
    }

    /// <summary>
    /// Separate user-page state shared by the ordinary-user page. It stores drafts, TMDB results and the mapping
    /// to the global operational queue. The admin tracker is touched only when
    /// a confirmed movie is submitted to AddMovieTask.
    /// </summary>
    internal static class UserAddMovieTracker
    {
        private static readonly object Sync = new object();
        private static readonly List<UserMovieEntry> Entries = new List<UserMovieEntry>();

        public static UserMovieEntry Add(string name, int? year)
        {
            var entry = new UserMovieEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
                SearchName = name,
                SearchYear = year,
                State = UserMovieState.Searching,
                IncludedInBulkAdd = false
            };

            lock (Sync)
            {
                Entries.Add(entry);
                return entry;
            }
        }

        public static UserMovieEntry AddManual(string name, int? year)
        {
            var entry = Add(name, year);
            lock (Sync)
            {
                entry.State = UserMovieState.Ready;
                entry.IsManual = true;
                entry.IncludedInBulkAdd = true;
                return entry;
            }
        }

        public static UserMovieEntry Get(string id)
        {
            lock (Sync)
                return Entries
                    .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }

        public static UserMovieEntry[] GetAll()
        {
            lock (Sync)
                return Entries
                    .OrderByDescending(e => e.CreatedAt)
                    .ToArray();
        }

        public static void Remove(string id)
        {
            lock (Sync)
            {
                var entry = Entries.FirstOrDefault(e => e.Id == id);
                if (entry != null) Entries.Remove(entry);
            }
        }

        public static void ClearCompleted()
        {
            lock (Sync)
                Entries.RemoveAll(e =>
                    e.State == UserMovieState.Added || e.State == UserMovieState.AddFailed);
        }

    }
}