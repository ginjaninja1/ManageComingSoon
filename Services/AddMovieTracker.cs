// ManageComingSoon - Add Movie Tracker
// Static in-memory state machine for the Add Coming Soon search list.
// All state changes are immediately persisted via AddMovieStore.
//
// UI notification model:
//   Every public mutation method is decorated [UI] or [backend] in its summary.
//
//   [UI]      The mutation affects something the presentation layer renders.
//             OnStateChanged is fired after the lock is released so the page
//             view receives an immediate event-driven rebuild. Callers never
//             think about notification — it is structural, not voluntary.
//
//   [backend] Bookkeeping only. No UI notification. If a [backend] method ever
//             calls a [UI] method internally, that [UI] method fires
//             OnStateChanged automatically — no special handling needed.
//
//   OnStateChanged is wired once by AddMovieTask.SetDependencies. The page
//   view subscribes once in its constructor. No poll timer is required.
//
// DestinationConflict is not a separate state — it is HasDestinationConflict
// + ConflictReason annotating a Confident entry. SetConflict / ClearConflict
// operate on Confident entries without changing State.
//
// AddingPercent invariants:
//   SetAdding sets a per-step floor (StepWritingFiles → 0, StepAwaitingIngest
//   → 40). SetAddFailed preserves AddingPercent so the frozen progress bar
//   shows where the attempt reached. The row builder reads AddingPercent
//   directly with no step-to-percent logic.

namespace ManageComingSoon.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using ManageComingSoon.Model;
    using ManageComingSoon.Storage;
    using MediaBrowser.Model.Logging;

    public static class AddMovieTracker
    {
        // -----------------------------------------------------------------------
        // Adding step constants — internal; consumed only by SetAdding here.
        // The view reads entry.AddingPercent directly; it no longer needs these.
        // -----------------------------------------------------------------------
        internal const int StepWritingFiles = 1;
        internal const int StepAwaitingIngest = 2;

        private const int PercentFloorWritingFiles = 0;
        private const int PercentFloorAwaitingIngest = 40;

        // Minimum time an entry must have visibly been in Adding before a
        // completion (from either AddMovieTask's own pipeline or
        // ComingSoonEntryPoint's fast path) is allowed to actually apply.
        // Deliberate UX padding, not a technical necessity — real completion
        // can be as fast as ~100ms, far too short for a polling client to
        // ever reliably render. Costs no extra wall-clock time overall since
        // it's well under MinGapBetweenItemsSeconds' existing 5s floor in
        // AddMovieTask — this just spends part of that already-existing gap
        // on visible progress instead of dead time after completion.
        private static readonly TimeSpan MinVisibleAddingDuration = TimeSpan.FromMilliseconds(1200);

        // -----------------------------------------------------------------------
        // In-memory dictionary — Id is the key
        // -----------------------------------------------------------------------

        private static readonly Dictionary<string, AddMovieEntry> Entries =
            new Dictionary<string, AddMovieEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly object Lock = new object();
        private static AddMovieStore store;
        private static volatile bool isShuttingDown;

        // Diagnostic only — wired from AddMoviePageView's constructor (idempotent,
        // safe to set repeatedly). Not a hard dependency: every use is Log?.Warn(...)
        // so the tracker works fine before anything sets it.
        private static ILogger Log;

        public static void SetLogger(ILogger logger)
        {
            Log = logger;
        }

        // -----------------------------------------------------------------------
        // UI notification — single delegate wired by AddMovieTask.SetDependencies.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Fired by every [UI] mutation method after releasing Lock.
        /// The page view subscribes once and calls RebuildAndBroadcast.
        /// Null-safe: no-op until wired.
        /// </summary>
        internal static Action OnStateChanged;

        // -----------------------------------------------------------------------
        // Startup
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called once at plugin startup to inject the persistence store and
        /// load any previously saved entries.
        ///
        /// Drops Added rows older than 24 hours on load — this is a retention
        /// policy applied at server startup, not on every page navigation.
        /// All other tracker state is trusted as-is: the tracker is the single
        /// point of truth and page navigation must not alter it.
        /// </summary>
        public static void Initialise(AddMovieStore addMovieStore)
        {
            store = addMovieStore;
            lock (Lock)
            {
                Entries.Clear();
                foreach (var entry in store.Load())
                    if (!string.IsNullOrEmpty(entry.Id))
                        Entries[entry.Id] = entry;

                // Retention: drop completed rows older than 24 hours.
                var cutoff = DateTime.UtcNow.AddHours(-24);
                var toRemove = new List<string>();
                foreach (var entry in Entries.Values)
                {
                    if (entry.State == AddMovieState.Added
                        && entry.CompletedAt.HasValue
                        && entry.CompletedAt.Value < cutoff)
                        toRemove.Add(entry.Id);
                }
                foreach (var id in toRemove)
                    Entries.Remove(id);

                if (toRemove.Count > 0)
                    Persist();
            }
        }

        // -----------------------------------------------------------------------
        // Task queue API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Snapshots all Queued entries at AddMovieTask start, oldest first.
        /// Entries remain Queued in the dictionary — AddMovieTask transitions
        /// each one to Adding before calling the pipeline.
        /// </summary>
        public static AddMovieEntry[] DequeueAllQueued()
        {
            lock (Lock)
                return Entries.Values
                    .Where(e => e.State == AddMovieState.Queued)
                    .OrderBy(e => e.CreatedAt)
                    .ToArray();
        }

        // -----------------------------------------------------------------------
        // Add
        // -----------------------------------------------------------------------

        /// <summary>[UI] Creates a new entry in Searching state and persists it.</summary>
        public static AddMovieEntry Add(string searchName, int? searchYear)
        {
            var entry = new AddMovieEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                SearchName = searchName ?? string.Empty,
                SearchYear = searchYear,
                CreatedAt = DateTime.UtcNow,
                State = AddMovieState.Searching,
            };

            lock (Lock)
            {
                Entries[entry.Id] = entry;
                Persist();
            }

            OnStateChanged?.Invoke();
            return entry;
        }

        /// <summary>
        /// [UI] Creates a new manually-confirmed entry using the user's typed
        /// title/year, bypassing provider search.
        /// </summary>
        public static AddMovieEntry AddManual(string searchName, int? searchYear)
        {
            var entry = new AddMovieEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                SearchName = searchName ?? string.Empty,
                SearchYear = searchYear,
                CreatedAt = DateTime.UtcNow,
                State = AddMovieState.Confident,
                ConfirmedTmdbId = 0,
                ConfirmedTitle = searchName ?? string.Empty,
                ConfirmedYear = searchYear ?? DateTime.UtcNow.Year,
                ConfirmedOverview = string.Empty,
                ConfirmedPosterPath = string.Empty,
                Candidates = new List<AddMovieCandidate>(),
            };

            lock (Lock)
            {
                Entries[entry.Id] = entry;
                Persist();
            }

            OnStateChanged?.Invoke();
            return entry;
        }

        // -----------------------------------------------------------------------
        // Search result transitions
        // -----------------------------------------------------------------------

        /// <summary>[UI] Resets an entry to Searching state for a re-search.</summary>
        public static void SetSearching(string id)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.State = AddMovieState.Searching;
                entry.ErrorMessage = string.Empty;
                entry.Candidates = new List<AddMovieCandidate>();
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>[UI] Records a confident TMDB match and transitions to Confident state.</summary>
        public static void SetConfident(string id, TmdbMovieResult match)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.State = AddMovieState.Confident;
                entry.HasDestinationConflict = false;
                entry.ConflictReason = string.Empty;
                entry.ErrorMessage = string.Empty;
                entry.ConfirmedTmdbId = match.Id;
                entry.ConfirmedTitle = match.Title ?? string.Empty;
                entry.ConfirmedYear = match.ReleaseYear;
                entry.ConfirmedOverview = match.Overview ?? string.Empty;
                entry.ConfirmedPosterPath = match.PosterPath ?? string.Empty;
                entry.Candidates = new List<AddMovieCandidate>();
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// [UI] Manually confirms an entry whose TMDB search returned no usable match.
        /// Uses the user's typed title/year as the confirmed identity.
        /// </summary>
        public static void SetManualConfident(string id)
        {
            bool changed = false;
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                if (entry.State != AddMovieState.NoResults
                    && entry.State != AddMovieState.SearchFailed)
                    return;

                entry.State = AddMovieState.Confident;
                entry.HasDestinationConflict = false;
                entry.ConflictReason = string.Empty;
                entry.ErrorMessage = string.Empty;
                entry.ConfirmedTmdbId = 0;
                entry.ConfirmedTitle = entry.SearchName;
                entry.ConfirmedYear = entry.SearchYear ?? DateTime.UtcNow.Year;
                entry.ConfirmedOverview = string.Empty;
                entry.ConfirmedPosterPath = string.Empty;
                entry.Candidates = new List<AddMovieCandidate>();
                Persist();
                changed = true;
            }

            if (changed) OnStateChanged?.Invoke();
        }

        /// <summary>[UI] Records multiple TMDB candidates for user selection.</summary>
        public static void SetMultipleMatches(string id, List<TmdbMovieResult> candidates)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.State = AddMovieState.MultipleMatches;
                entry.ErrorMessage = string.Empty;
                entry.Candidates = candidates
                    .Take(8)
                    .Select(AddMovieCandidate.FromTmdb)
                    .ToList();
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>[UI] Marks the entry as having no TMDB results.</summary>
        public static void SetNoResults(string id)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.State = AddMovieState.NoResults;
                entry.ErrorMessage = string.Empty;
                entry.Candidates = new List<AddMovieCandidate>();
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>[UI] Marks the entry as having a failed TMDB search.</summary>
        public static void SetSearchFailed(string id, string errorMessage)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.State = AddMovieState.SearchFailed;
                entry.ErrorMessage = errorMessage ?? string.Empty;
                entry.Candidates = new List<AddMovieCandidate>();
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        // -----------------------------------------------------------------------
        // Destination conflict — annotation on Confident, not a state transition
        // -----------------------------------------------------------------------

        /// <summary>
        /// [UI] Records a destination conflict on a Confident entry.
        /// State remains Confident; HasDestinationConflict is raised.
        /// The Add button is disabled and reason shown as secondary text.
        /// No-op if the entry is not Confident.
        /// </summary>
        public static void SetConflict(string id, string reason)
        {
            bool changed = false;
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                if (entry.State != AddMovieState.Confident) return;
                entry.HasDestinationConflict = true;
                entry.ConflictReason = reason ?? string.Empty;
                Persist();
                changed = true;
            }

            if (changed) OnStateChanged?.Invoke();
        }

        /// <summary>
        /// [UI] Clears a destination conflict when the block is resolved.
        /// No-op if the entry is not Confident or has no conflict.
        /// </summary>
        public static void ClearConflict(string id)
        {
            bool changed = false;
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                if (entry.State != AddMovieState.Confident) return;
                if (!entry.HasDestinationConflict) return;
                entry.HasDestinationConflict = false;
                entry.ConflictReason = string.Empty;
                Persist();
                changed = true;
            }

            if (changed) OnStateChanged?.Invoke();
        }

        // -----------------------------------------------------------------------
        // Queue and pipeline transitions
        // -----------------------------------------------------------------------

        /// <summary>
        /// [UI] Transitions a Confident (unblocked) or AddFailed entry into the queue.
        /// Clears any destination conflict annotation — re-checked at queue time.
        /// No-op if the entry is blocked by a destination conflict.
        /// </summary>
        public static void SetQueued(string id)
        {
            bool changed = false;
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                if (entry.State != AddMovieState.Confident
                    && entry.State != AddMovieState.AddFailed)
                    return;

                // A conflict should have been resolved before queuing, but guard anyway.
                if (entry.HasDestinationConflict) return;

                entry.State = AddMovieState.Queued;
                entry.HasDestinationConflict = false;
                entry.ConflictReason = string.Empty;
                entry.ErrorMessage = string.Empty;
                Persist();
                changed = true;
            }

            if (changed) OnStateChanged?.Invoke();
        }

        /// <summary>
        /// [UI] Advances the Adding state and sets the per-step floor percent.
        ///
        /// Floor values:
        ///   StepWritingFiles    → 0   (real percent arrives quickly via SetAddingPercent)
        ///   StepAwaitingIngest  → 40  (no sub-progress during ingest wait)
        ///
        /// The floor ensures AddingPercent is always meaningful so the row
        /// builder can read it directly without any step-to-percent logic.
        /// Fires OnStateChanged after the lock is released.
        /// </summary>
        public static void SetAdding(string id, int step, string detail)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;

                // Guard against a late/out-of-order caller reverting an entry
                // that has already reached a terminal state (Added/AddFailed)
                // or is still mid-search. Only Queued (starting the pipeline)
                // and Adding (subsequent stage updates) are valid sources.
                // Without this, a stray onStatus/SetAdding call arriving after
                // SetAdded silently reverts a finished row back to "in progress".
                //
                // The source of that stray call hasn't been confirmed — ruled
                // out WaitForConditionAsync (EmbyLibrarySharedService), which
                // is a plain sequential await loop with no fire-and-forget path.
                // Logging here (with a stack trace) rather than silently
                // swallowing it means if it still happens, we get proof of
                // exactly where from, next time it fires.
                if (entry.State != AddMovieState.Queued && entry.State != AddMovieState.Adding)
                {
                    Log?.Warn(
                        "MCS-DIAG SetAdding REJECTED id={0} currentState={1} attemptedStep={2} detail='{3}'\n{4}",
                        id, entry.State, step, detail, Environment.StackTrace);
                    return;
                }

                bool firstEntry = entry.State == AddMovieState.Queued;

                entry.State = AddMovieState.Adding;
                entry.AddingStep = step;
                entry.AddingDetail = detail ?? string.Empty;
                entry.ErrorMessage = string.Empty;

                if (firstEntry)
                    entry.AddingStartedAt = DateTime.UtcNow;

                // Apply floor only when entering a step for the first time
                // (i.e. when the step changes), so SetAdding called repeatedly
                // on the same step does not reset live progress.
                if (step == StepWritingFiles)
                    entry.AddingPercent = PercentFloorWritingFiles;
                else if (step == StepAwaitingIngest && entry.AddingPercent < PercentFloorAwaitingIngest)
                    entry.AddingPercent = PercentFloorAwaitingIngest;

                Persist();
            }

            // Fire outside the lock — the handler calls RebuildAndBroadcast which
            // acquires rebuildLock; holding Lock here would risk deadlock.
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// [UI] Updates the progress percentage on the in-flight Adding entry.
        /// Called by AddMovieTask on every itemProgress callback.
        /// Fires OnStateChanged so the progress bar advances in real time.
        /// </summary>
        public static void SetAddingPercent(string id, int percent)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                if (entry.State != AddMovieState.Adding) return;
                entry.AddingPercent = percent;
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// Computes how much longer (if any) an entry must remain visibly
        /// Adding before a completion may apply. Returns null if the entry
        /// is missing, already resolved, never got an AddingStartedAt stamp,
        /// or has already been visible long enough — in every one of those
        /// cases the caller should apply its completion immediately and let
        /// that method's own guard handle any state-mismatch logging.
        /// </summary>
        private static TimeSpan? ComputeDeferral(string id)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return null;
                if (entry.State != AddMovieState.Adding) return null;
                if (!entry.AddingStartedAt.HasValue) return null;

                var elapsed = DateTime.UtcNow - entry.AddingStartedAt.Value;
                return elapsed < MinVisibleAddingDuration
                    ? MinVisibleAddingDuration - elapsed
                    : (TimeSpan?)null;
            }
        }

        /// <summary>
        /// Fire-and-forget delay before running a deferred completion. Safe to
        /// call from a synchronous event handler (ComingSoonEntryPoint) since
        /// it never blocks the caller — exceptions from the deferred action
        /// are caught and logged rather than becoming unobserved task faults.
        /// </summary>
        private static void ScheduleDeferred(TimeSpan delay, Action action)
        {
            Task.Delay(delay).ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex)
                {
                    Log?.Warn("MCS-DIAG deferred completion threw: {0}", ex);
                }
            }, TaskScheduler.Default);
        }

        /// <summary>
        /// [UI] Marks the entry as successfully added to the library.
        /// If the entry hasn't been visibly Adding for MinVisibleAddingDuration
        /// yet, the actual transition is deferred so a fast real completion
        /// (as little as ~100ms) still renders as "in progress" for a moment
        /// on a polling client, rather than never being visible at all.
        /// Fires OnStateChanged so the row moves to Completed once applied.
        /// </summary>
        public static void SetAdded(string id, string folderPath)
        {
            var deferBy = ComputeDeferral(id);
            if (deferBy.HasValue)
            {
                ScheduleDeferred(deferBy.Value, () => ApplySetAdded(id, folderPath));
                return;
            }
            ApplySetAdded(id, folderPath);
        }

        private static void ApplySetAdded(string id, string folderPath)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;

                // Guard against a redundant completion. AddMovieTask is now
                // the sole caller of SetAdded, including for the fast-path-
                // confirmed case (see NotifyPathConfirmed) — so in normal
                // operation this guard should never actually reject anything.
                // Kept as insurance, not as compensation for a known race.
                if (entry.State != AddMovieState.Adding)
                {
                    Log?.Warn(
                        "MCS-DIAG SetAdded REJECTED id={0} currentState={1} — already completed by another writer",
                        id, entry.State);
                    return;
                }

                entry.State = AddMovieState.Added;
                entry.AddedFolderPath = folderPath ?? string.Empty;
                entry.CompletedAt = DateTime.UtcNow;
                entry.ErrorMessage = string.Empty;
                entry.AddingStep = 0;
                entry.AddingDetail = string.Empty;
                entry.AddingPercent = 0;
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// [UI] Marks an entry as failed. AddingPercent is preserved so the
        /// frozen progress bar shows where the attempt reached before failing.
        /// Same minimum-visible-dwell deferral as SetAdded — a failure that
        /// happens very quickly still gets a moment of visible "in progress"
        /// rather than the row jumping straight from Queued to Failed.
        /// Fires OnStateChanged once applied.
        /// </summary>
        public static void SetAddFailed(string id, string errorMessage)
        {
            var deferBy = ComputeDeferral(id);
            if (deferBy.HasValue)
            {
                ScheduleDeferred(deferBy.Value, () => ApplySetAddFailed(id, errorMessage));
                return;
            }
            ApplySetAddFailed(id, errorMessage);
        }

        private static void ApplySetAddFailed(string id, string errorMessage)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;

                // Same reasoning as SetAdded's guard — a failure arriving
                // after the item was already completed by a different writer
                // (or vice versa) must not override the outcome that already
                // reached the user.
                if (entry.State != AddMovieState.Adding)
                {
                    Log?.Warn(
                        "MCS-DIAG SetAddFailed REJECTED id={0} currentState={1} — already resolved by another writer",
                        id, entry.State);
                    return;
                }

                entry.State = AddMovieState.AddFailed;
                entry.ErrorMessage = errorMessage ?? string.Empty;
                entry.AddingStep = 0;
                entry.AddingDetail = string.Empty;
                // AddingPercent intentionally preserved — frozen at failure point.
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        // -----------------------------------------------------------------------
        // Path tracking
        // -----------------------------------------------------------------------

        /// <summary>
        /// [signal only] Called by ComingSoonEntryPoint.OnItemAdded when Emby
        /// fires ItemAdded for a path registered by this tracker. Deliberately
        /// does NOT mutate State — AddMovieTask is the sole writer of terminal
        /// transitions (SetAdded/SetAddFailed). This only records that Emby
        /// has confirmed the path and wakes AddMovieTask's fast-path race
        /// (via OnStateChanged) so it can stop its own redundant pipeline and
        /// apply the completion itself.
        ///
        /// Single-writer by design: two components independently deciding
        /// "this item is Added" was the actual source of the races this
        /// tracker used to need guards against (see SetAdded's history).
        /// Keeping ComingSoonEntryPoint to signal-only removes that class of
        /// bug structurally rather than defending against it after the fact.
        /// </summary>
        public static void NotifyPathConfirmed(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            bool matched = false;
            lock (Lock)
            {
                foreach (var entry in Entries.Values)
                {
                    if (entry.State != AddMovieState.Adding) continue;
                    if (string.IsNullOrEmpty(entry.AddedFolderPath)) continue;
                    if (!folderPath.StartsWith(entry.AddedFolderPath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    if (!entry.PathConfirmedAt.HasValue)
                        entry.PathConfirmedAt = DateTime.UtcNow;
                    matched = true;
                    break;
                }
            }

            // Wakes any fast-path race (AddMovieTask) subscribed to this same
            // event — it will re-check IsPathConfirmed and decide for itself
            // whether to act. No state changed here, so nothing to persist.
            if (matched) OnStateChanged?.Invoke();
        }

        /// <summary>
        /// Peek-only: has Emby already confirmed this entry's path? Read by
        /// AddMovieTask's fast-path race. Does not mutate anything and does
        /// not "consume" the confirmation — AddMovieTask's own SetAdded call
        /// is what actually applies completion, exactly once, guarded as usual.
        /// </summary>
        public static bool IsPathConfirmed(string id)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                return Entries.TryGetValue(id, out entry) && entry.PathConfirmedAt.HasValue;
            }
        }

        /// <summary>
        /// [backend] Records the destination folder path without changing state.
        /// Called immediately before SetAdded so ComingSoonEntryPoint.OnItemAdded
        /// can match the path. SetAdded fires OnStateChanged — no separate
        /// notification needed here.
        /// </summary>
        public static void RecordFolderPath(string id, string folderPath)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.AddedFolderPath = folderPath ?? string.Empty;
                Persist();
            }
        }

        // -----------------------------------------------------------------------
        // Candidate cast
        // -----------------------------------------------------------------------

        /// <summary>
        /// [UI] Stores fetched cast names on a specific candidate.
        /// Fires OnStateChanged so the Info sub-row updates immediately.
        /// Persists so subsequent page loads don't re-fetch.
        /// </summary>
        public static void SetCandidateCast(string id, int candidateIndex,
            List<string> castNames)
        {
            bool changed = false;
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                if (candidateIndex < 0 || candidateIndex >= entry.Candidates.Count) return;
                entry.Candidates[candidateIndex].CastNames =
                    castNames ?? new List<string>();
                Persist();
                changed = true;
            }

            if (changed) OnStateChanged?.Invoke();
        }

        // -----------------------------------------------------------------------
        // Search term update
        // -----------------------------------------------------------------------

        /// <summary>
        /// [UI] Updates search terms before a re-search.
        /// Fires OnStateChanged so the row reflects the new terms immediately.
        /// </summary>
        public static void UpdateSearchTerms(string id, string searchName, int? searchYear)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.SearchName = searchName ?? string.Empty;
                entry.SearchYear = searchYear;
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        // -----------------------------------------------------------------------
        // Bulk-add selection
        // -----------------------------------------------------------------------

        /// <summary>
        /// [UI] Sets whether a Confident row is selected for the "Add All" bulk action.
        /// Fires OnStateChanged so the header row count updates immediately.
        /// </summary>
        public static void SetIncludedInBulkAdd(string id, bool included)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                if (!Entries.TryGetValue(id, out entry)) return;
                entry.IncludedInBulkAdd = included;
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        // -----------------------------------------------------------------------
        // Remove
        // -----------------------------------------------------------------------

        /// <summary>[UI] Removes an entry from the list. Fires OnStateChanged.</summary>
        public static void Remove(string id)
        {
            lock (Lock)
            {
                Entries.Remove(id);
                Persist();
            }

            OnStateChanged?.Invoke();
        }

        // -----------------------------------------------------------------------
        // Queries
        // -----------------------------------------------------------------------

        public static AddMovieEntry Get(string id)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                return Entries.TryGetValue(id, out entry) ? entry : null;
            }
        }

        /// <summary>
        /// Finds an Adding entry whose AddedFolderPath matches the given item path.
        /// Used by ComingSoonEntryPoint to detect ingest completion.
        /// </summary>
        public static AddMovieEntry FindAddingByFolderPath(string itemPath)
        {
            if (string.IsNullOrEmpty(itemPath)) return null;
            lock (Lock)
            {
                foreach (var entry in Entries.Values.ToArray())
                {
                    if (entry.State != AddMovieState.Adding) continue;
                    if (string.IsNullOrEmpty(entry.AddedFolderPath)) continue;
                    if (itemPath.StartsWith(entry.AddedFolderPath,
                            StringComparison.OrdinalIgnoreCase))
                        return entry;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns entries sorted for display:
        /// active rows newest-first, then Added rows newest-completed-first.
        ///
        /// Returns deep clones, not live references. AddMovieEntry objects are
        /// mutable and can be written to by AddMovieTask on a background thread
        /// at any time (State, AddingPercent, AddingDetail, ...). RebuildMovieList
        /// reads several fields off each entry across multiple statements
        /// (BuildMovieRow in particular) — without cloning, a mutation landing
        /// mid-rebuild produces a torn read: an entry bucketed as "active" on
        /// its old State, but already reset by SetAdded by the time its other
        /// fields are read. Cloning happens here, under Lock, so every caller
        /// gets a consistent point-in-time snapshot with no further locking
        /// required on their part.
        /// </summary>
        /// <summary>
        /// Lightweight state check for one entry, used by AddMovieTask to detect
        /// when ComingSoonEntryPoint's ItemAdded fast path has already resolved
        /// the item it's still independently polling for — lets it stop that
        /// redundant work immediately instead of running its own wait to
        /// completion regardless. Returns a plain enum value (not a reference),
        /// so there's nothing here that needs cloning for safe reading.
        /// </summary>
        public static AddMovieState? GetState(string id)
        {
            lock (Lock)
            {
                AddMovieEntry entry;
                return Entries.TryGetValue(id, out entry) ? (AddMovieState?)entry.State : null;
            }
        }

        public static AddMovieEntry[] GetAllSorted()
        {
            lock (Lock)
            {
                var snapshot = Entries.Values.Select(e => e.Clone()).ToArray();

                var active = snapshot
                    .Where(e => e.State != AddMovieState.Added)
                    .OrderByDescending(e => e.CreatedAt)
                    .ToList();

                var completed = snapshot
                    .Where(e => e.State == AddMovieState.Added)
                    .OrderByDescending(e => e.CompletedAt ?? DateTime.MinValue)
                    .ToList();

                return active.Concat(completed).ToArray();
            }
        }

        /// <summary>
        /// True if any row is actively working.
        /// Covers: Searching (async task), Queued (waiting for task), Adding (pipeline).
        /// </summary>
        public static bool AnyInFlight()
        {
            lock (Lock)
                return Entries.Values.ToArray().Any(e => e.IsActive);
        }

        /// <summary>
        /// Finds an earlier-created entry that would claim the same destination
        /// folder — used for in-list duplicate detection before touching disk.
        /// </summary>
        public static AddMovieEntry FindEarlierDuplicateDestination(
            string safeName, string excludeId, DateTime createdAt)
        {
            if (string.IsNullOrEmpty(safeName)) return null;

            lock (Lock)
            {
                AddMovieEntry earliest = null;

                foreach (var entry in Entries.Values.ToArray())
                {
                    if (entry.Id == excludeId) continue;
                    if (entry.CreatedAt >= createdAt) continue;
                    if (!entry.ClaimsDestination) continue;
                    if (string.IsNullOrEmpty(entry.ConfirmedTitle)) continue;

                    string otherSafeName = EmbyLibrarySharedService.BuildComingSoonFolderName(
                        entry.ConfirmedTitle, entry.ConfirmedYear);

                    if (!otherSafeName.Equals(safeName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (earliest == null || entry.CreatedAt < earliest.CreatedAt)
                        earliest = entry;
                }

                return earliest;
            }
        }

        // -----------------------------------------------------------------------
        // Shutdown
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called at server shutdown. After this point Persist() is a no-op so
        /// background threads cannot write after IFileSystem/IJsonSerializer
        /// have been disposed.
        /// </summary>
        public static void Shutdown()
        {
            isShuttingDown = true;
        }

        // -----------------------------------------------------------------------
        // Internal persistence — always called under Lock
        // -----------------------------------------------------------------------

        private static void Persist()
        {
            if (store == null || isShuttingDown) return;
            store.Save(Entries.Values.ToList());
        }
    }
}
