// ManageComingSoon - Add Movie Entry & State
// Model for a single row in the Add Coming Soon movie search list.
// Persisted to disk via AddMovieStore so the list survives page reloads.

namespace ManageComingSoon.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using ManageComingSoon.Model;

    // -----------------------------------------------------------------------
    // State enum — drives row rendering and available actions
    // -----------------------------------------------------------------------
    public enum AddMovieState
    {
        /// <summary>TMDB search in progress.</summary>
        Searching,

        /// <summary>Single confident TMDB match found; ready to add.</summary>
        Confident,

        /// <summary>Multiple TMDB candidates found; user must select one.</summary>
        MultipleMatches,

        /// <summary>TMDB returned no results; user can edit and re-search.</summary>
        NoResults,

        /// <summary>TMDB search failed (e.g. API offline); user can retry.</summary>
        SearchFailed,

        /// <summary>
        /// Confirmed and enqueued for the Add All batch operation. Set when "Add All"
        /// is pressed on selected Confident entries, or when a single-add row's Adding
        /// operation is interrupted by a page navigation.
        ///
        /// Persists across page reloads so the queue is visible on return. "Add All"
        /// (which doubles as "Resume Queue") picks up all Queued entries on each press,
        /// so reloading and pressing Add All resumes exactly where things left off.
        ///
        /// The poll timer treats Queued as active (see IsActive) and continues
        /// destination-conflict checks while entries wait their turn.
        /// </summary>
        Queued,

        /// <summary>AddComingSoonAsync in progress; sub-step tracked by AddingStep/AddingDetail.</summary>
        Adding,

        /// <summary>Successfully added and tagged in Emby library.</summary>
        Added,

        /// <summary>Add-to-library operation failed.</summary>
        AddFailed,
    }

    // -----------------------------------------------------------------------
    // Persisted candidate — lightweight subset of TmdbMovieResult
    // -----------------------------------------------------------------------
    public class AddMovieCandidate
    {
        public int TmdbId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;
        public string Overview { get; set; } = string.Empty;
        public string PosterPath { get; set; } = string.Empty;


        public double Popularity { get; set; }

        /// <summary>
        /// Top 3 cast member names fetched from TMDB /movie/{id}/credits.
        /// Null until first Info button click; empty list if fetch returned no cast.
        /// Persisted so subsequent page loads don't need to re-fetch.
        /// </summary>
        public List<string> CastNames { get; set; }   // null = not yet fetched

        public int ReleaseYear
        {
            get
            {
                if (string.IsNullOrEmpty(ReleaseDate) || ReleaseDate.Length < 4) return 0;
                int y;
                return int.TryParse(ReleaseDate.Substring(0, 4), out y) ? y : 0;
            }
        }

        public string PosterUrl()
            => string.IsNullOrEmpty(PosterPath)
                ? string.Empty
                : "https://image.tmdb.org/t/p/w342" + PosterPath;

        public string TmdbUrl()
            => string.Format("https://www.themoviedb.org/movie/{0}", TmdbId);

        /// <summary>Build from a full TmdbMovieResult (used when storing candidates).</summary>
        public static AddMovieCandidate FromTmdb(TmdbMovieResult r)
            => new AddMovieCandidate
            {
                TmdbId = r.Id,
                Title = r.Title ?? string.Empty,
                ReleaseDate = r.ReleaseDate ?? string.Empty,
                Overview = r.Overview ?? string.Empty,
                PosterPath = r.PosterPath ?? string.Empty,
                Popularity = r.Popularity,
                CastNames = null,
            };

        /// <summary>
        /// Deep copy. CastNames is copied into a new list rather than shared,
        /// since SetCandidateCast can replace it on the live tracker object
        /// at any time — a snapshot must not hold a reference that could be
        /// swapped out from under a caller mid-read.
        /// </summary>
        public AddMovieCandidate Clone()
            => new AddMovieCandidate
            {
                TmdbId = TmdbId,
                Title = Title,
                ReleaseDate = ReleaseDate,
                Overview = Overview,
                PosterPath = PosterPath,
                Popularity = Popularity,
                CastNames = CastNames == null ? null : new List<string>(CastNames),
            };
    }

    // -----------------------------------------------------------------------
    // Main entry — one row per movie the user has added to the search list
    // -----------------------------------------------------------------------
    public class AddMovieEntry
    {
        // ---- Identity -------------------------------------------------------
        public string Id { get; set; } = string.Empty;

        // ---- What the user typed -------------------------------------------
        public string SearchName { get; set; } = string.Empty;
        public int? SearchYear { get; set; }

        // ---- Lifecycle timestamps ------------------------------------------
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }

        // ---- Current state -------------------------------------------------
        public AddMovieState State { get; set; } = AddMovieState.Searching;
        public string ErrorMessage { get; set; } = string.Empty;

        // ---- Destination conflict annotation (replaces DestinationConflict state)
        // True while Confident and the computed destination folder is already taken.
        // State remains Confident; the Add button is disabled and ConflictReason
        // is shown as secondary text. Cleared when the conflict resolves.
        public bool HasDestinationConflict { get; set; }
        public string ConflictReason { get; set; } = string.Empty;

        // ---- Adding sub-step (used while State == Adding) ------------------
        public int AddingStep { get; set; }
        public string AddingDetail { get; set; } = string.Empty;

        /// <summary>
        /// Set once, the first time an entry transitions Queued -> Adding.
        /// Used by AddMovieTracker to enforce a minimum visible dwell time in
        /// the Adding state before allowing a completion (from either
        /// AddMovieTask's own pipeline or ComingSoonEntryPoint's fast path)
        /// to actually apply — otherwise a sub-200ms real completion never
        /// renders on a polling client. Null until first entering Adding;
        /// not reset by subsequent SetAdding calls for the same attempt.
        /// </summary>
        public DateTime? AddingStartedAt { get; set; }

        /// <summary>
        /// Set once by AddMovieTracker.NotifyPathConfirmed when Emby's
        /// ItemAdded fires for this entry's registered path. Signal only —
        /// never mutates State. AddMovieTask remains the sole writer of
        /// terminal transitions; this just tells its fast-path race that
        /// ComingSoonEntryPoint has already confirmed the file landed, so it
        /// can stop its own redundant pipeline and apply completion itself.
        /// </summary>
        public DateTime? PathConfirmedAt { get; set; }

        /// <summary>
        /// Pipeline progress percentage (0-100) while State == Adding.
        /// Written by AddMovieTask via AddMovieTracker.SetAddingPercent() on
        /// every itemProgress callback. Read by BuildPollSignature so every
        /// percentage change triggers a UI push. Reset to 0 on state transitions.
        /// </summary>
        public int AddingPercent { get; set; }

        // ---- Confirmed TMDB match (set when Confident or after Select) ------
        public int ConfirmedTmdbId { get; set; }
        public string ConfirmedTitle { get; set; } = string.Empty;
        public int ConfirmedYear { get; set; }
        public string ConfirmedOverview { get; set; } = string.Empty;
        public string ConfirmedPosterPath { get; set; } = string.Empty;

        // ---- Candidate list (stored when MultipleMatches, up to 10) --------
        public List<AddMovieCandidate> Candidates { get; set; } = new List<AddMovieCandidate>();

        // ---- Result ---------------------------------------------------------
        public string AddedFolderPath { get; set; } = string.Empty;

        // ---- Bulk-add scope ("Select" toggle on the Confident root row) -----
        // Default true so existing behavior is unchanged for legacy persisted
        // entries (JSON without this field) and for newly Confident rows —
        // every match starts in scope for "Add All" until explicitly excluded.
        public bool IncludedInBulkAdd { get; set; } = true;

        // ---- Helpers --------------------------------------------------------

        /// <summary>
        /// True while the entry is doing work or waiting to do work.
        /// Drives the poll timer: the timer stays running while any entry is active.
        /// Queued is included so entries waiting in the batch queue keep the timer
        /// alive and their destinations are continuously conflict-checked.
        /// </summary>
        public bool IsActive
            => State == AddMovieState.Searching
            || State == AddMovieState.Queued
            || State == AddMovieState.Adding;

        public bool IsTerminal
            => State == AddMovieState.Added;

        public string DisplayTitle
            => !string.IsNullOrEmpty(ConfirmedTitle) ? ConfirmedTitle : SearchName;

        public int DisplayYear
            => ConfirmedYear > 0 ? ConfirmedYear : (SearchYear ?? 0);

        /// <summary>
        /// Parent directory of AddedFolderPath for display (e.g. "D:\Media\Movies").
        /// Empty string when no path is recorded yet.
        /// </summary>
        public string DisplayFolderPath
        {
            get
            {
                if (string.IsNullOrEmpty(AddedFolderPath)) return string.Empty;
                return Path.GetDirectoryName(AddedFolderPath) ?? AddedFolderPath;
            }
        }

        /// <summary>
        /// True while the row should display a progress bar.
        /// Adding: live progress. AddFailed: frozen at the point of failure.
        /// </summary>
        public bool ShowProgress
            => State == AddMovieState.Adding || State == AddMovieState.AddFailed;

        /// <summary>
        /// True when the entry is Confident but the destination is known to conflict.
        /// Add button is disabled; row shows ConflictReason as secondary text.
        /// </summary>
        public bool IsAddBlocked
            => State == AddMovieState.Confident && HasDestinationConflict;

        /// <summary>
        /// True for states where this entry holds an active destination path claim
        /// (used by duplicate-destination detection).
        /// </summary>
        public bool ClaimsDestination
            => State == AddMovieState.Confident
            || State == AddMovieState.Queued
            || State == AddMovieState.Adding
            || State == AddMovieState.AddFailed;

        public string ConfirmedPosterUrl()
            => string.IsNullOrEmpty(ConfirmedPosterPath)
                ? string.Empty
                : "https://image.tmdb.org/t/p/w342" + ConfirmedPosterPath;

        /// <summary>
        /// Deep copy for handing to readers outside the tracker's Lock
        /// (RebuildMovieList, status counts, etc). AddMovieEntry is a plain
        /// mutable object living inside AddMovieTracker's dictionary; a
        /// background AddMovieTask thread can mutate it (State, AddingPercent,
        /// AddingDetail, ...) at any moment. Reading fields off the live
        /// object across several lines/statements — exactly what
        /// BuildMovieRow does — risks a torn read. Clone() must be called
        /// while still holding AddMovieTracker.Lock so the copy is a
        /// consistent point-in-time snapshot.
        /// </summary>
        public AddMovieEntry Clone()
            => new AddMovieEntry
            {
                Id = Id,
                SearchName = SearchName,
                SearchYear = SearchYear,
                CreatedAt = CreatedAt,
                CompletedAt = CompletedAt,
                State = State,
                ErrorMessage = ErrorMessage,
                HasDestinationConflict = HasDestinationConflict,
                ConflictReason = ConflictReason,
                AddingStep = AddingStep,
                AddingDetail = AddingDetail,
                AddingStartedAt = AddingStartedAt,
                PathConfirmedAt = PathConfirmedAt,
                AddingPercent = AddingPercent,
                ConfirmedTmdbId = ConfirmedTmdbId,
                ConfirmedTitle = ConfirmedTitle,
                ConfirmedYear = ConfirmedYear,
                ConfirmedOverview = ConfirmedOverview,
                ConfirmedPosterPath = ConfirmedPosterPath,
                Candidates = Candidates == null
                    ? new List<AddMovieCandidate>()
                    : Candidates.ConvertAll(c => c.Clone()),
                AddedFolderPath = AddedFolderPath,
                IncludedInBulkAdd = IncludedInBulkAdd,
            };
    }
}