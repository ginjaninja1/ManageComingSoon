// ManageComingSoon - Make Live Tracker
// Static in-memory state machine for in-flight and historic make-live ops.
// All state changes are immediately persisted via MakeLiveStore.
// Mirrors the AddMovieTracker pattern.

namespace ManageComingSoon.Services
{
    using ManageComingSoon.Storage;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using static ManageComingSoon.Services.EmbyLibraryMakeService;

    // -----------------------------------------------------------------------
    // State enum
    // -----------------------------------------------------------------------
    public enum MakeLiveState
    {
        // NOTE: Pending and Queued are in-memory only — these entries are never
        // persisted and are rebuilt / reset on every page-load / server restart.
        //
        // Pending  (-1) — Coming Soon snapshot; rebuilt from GetComingSoonItems().
        // Queued    (0) — Selected and handed to the task queue but not yet
        //                 Register()-ed by MakeLiveTask. Reset to Pending by
        //                 PrepareForPageLoad() on server restart (no work started,
        //                 so Pending is correct — not Failed).
        Pending = -1,
        Queued = 0,
        Moving,
        ScanPending,
        Complete,
        Failed
    }

    // -----------------------------------------------------------------------
    // Entry representing one in-progress or recently completed make-live op
    // -----------------------------------------------------------------------
    public class MakeLiveEntry
    {
        public string FolderPath { get; set; }        // original source – the key
        public string ItemName { get; set; }
        public int Year { get; set; }                 // production year; 0 if unknown
        public Guid ItemId { get; set; }              // set after the pipeline returns
        public string TargetFolderPath { get; set; }  // where ItemAdded is expected
        public MakeLiveState State { get; set; }
        public string Message { get; set; }

        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }    // set on Complete/Failed — drives history sort

        /// <summary>0-100. Drives GenericListItem.PercentComplete on the in-flight row.</summary>
        public int Percent { get; set; }

        public bool IsActive => State == MakeLiveState.Moving || State == MakeLiveState.ScanPending;
        public bool IsPending => State == MakeLiveState.Pending;
        public bool IsQueued => State == MakeLiveState.Queued;

        /// <summary>
        /// Populated for Pending entries only; null for Active / Done entries.
        /// Not serialised — rebuilt by LoadAndAnalyse on every page-load.
        /// </summary>
        public MigrationAnalysisResult Analysis { get; set; }

        // ---- Toggle state (Pending entries only) ----
        // Stored on the tracker so it survives page navigation mid-run.

        /// <summary>True when the row is selected for Make All Live.</summary>
        public bool ToggledOn { get; set; }

        /// <summary>
        /// True when the user has explicitly deselected this row.
        /// Prevents LoadAndAnalyse from auto-re-enabling it even if analysis passes.
        /// </summary>
        public bool ManuallyToggledOff { get; set; }

        /// <summary>
        /// The target library root (e.g. D:\Movies), set at SetQueued() time.
        /// Null when no move is configured (same-location mode).
        /// Used to build the fixed primary-line path display on queued/active rows.
        /// Not persisted — ephemeral display state.
        /// </summary>
        public string TargetLibraryPath { get; set; }
    }

    // -----------------------------------------------------------------------
    // Tracker – static, lives for the server lifetime
    // -----------------------------------------------------------------------
    public static class MakeLiveTracker
    {
        private static readonly Dictionary<string, MakeLiveEntry> Entries =
            new Dictionary<string, MakeLiveEntry>(StringComparer.OrdinalIgnoreCase);

        // History is intentionally separate from Entries so that Complete/Failed
        // records can never collide with or suppress live Pending entries.
        // History is append-only; the only way to clear it is ClearCompleted().
        private static readonly List<MakeLiveEntry> History =
            new List<MakeLiveEntry>();

        private static readonly object Lock = new object();

        // -----------------------------------------------------------------------
        // Current-run path set — paths handed to the pipeline since the current
        // MakeLiveTask execution started. Populated by Register(), cleared by
        // ClearCurrentRun() when the task completes. Static so it survives page
        // navigation. Not persisted — a server restart means the task is gone
        // and PrepareForPageLoad resets any active entries to Failed anyway.
        // -----------------------------------------------------------------------
        private static readonly HashSet<string> CurrentRunPathsSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Paths cancelled via SetPending() after MakeLiveTask.DequeueAll() already
        // consumed them from TaskQueue. Register() checks and clears each path so
        // the task silently skips items the user removed from the queue mid-run.
        // Cleared wholesale by ClearCurrentRun().
        private static readonly HashSet<string> PendingDequeues =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Store reference — injected once at startup by ManageComingSoonPlugin
        private static MakeLiveStore store;

        /// <summary>
        /// Called once at plugin startup to provide the persistence store and
        /// load any previously saved entries.
        /// </summary>
        public static void Initialise(MakeLiveStore makeLiveStore)
        {
            store = makeLiveStore;
            lock (Lock)
            {
                Entries.Clear();
                History.Clear();
                var loaded = store.Load();
                foreach (var entry in loaded)
                {
                    if (string.IsNullOrEmpty(entry.FolderPath)) continue;
                    if (entry.State == MakeLiveState.Complete || entry.State == MakeLiveState.Failed)
                        History.Add(entry);
                    else
                        Entries[entry.FolderPath] = entry;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Lifecycle: page-load hygiene
        // -----------------------------------------------------------------------

        /// <summary>
        /// Called when the Make Live page is opened.
        /// • Moves any Moving/ScanPending entries to History as Failed —
        ///   a server restart means the in-memory queue is gone and nothing
        ///   will ever advance them further.
        /// • Resets Queued entries to Pending — they never started work.
        /// </summary>
        public static void PrepareForPageLoad()
        {
            lock (Lock)
            {
                // If CurrentRunPathsSet has entries the task was started in this
                // server session and is still running — do not reset active entries.
                // PrepareForPageLoad is only for server-restart recovery, where the
                // in-memory queue is gone and nothing will ever advance active entries.
                if (CurrentRunPathsSet.Count > 0)
                    return;

                // Moving/ScanPending entries survived a server restart — the task
                // queue is gone so nothing will ever advance them. Move them to
                // History as Failed; remove from Entries so they cannot block
                // UpsertPending for a returning Coming Soon item.
                // Queued entries never started work — reset to Pending so the user
                // can re-select and retry.
                var toRemove = new List<string>();

                foreach (var entry in Entries.Values)
                {
                    if (entry.IsActive)
                    {
                        entry.State = MakeLiveState.Failed;
                        entry.Message = "Interrupted by a server restart. Please retry.";
                        entry.CompletedAt = DateTime.UtcNow;
                        entry.Percent = 0;
                        History.Add(entry);
                        toRemove.Add(entry.FolderPath);
                    }
                    else if (entry.IsQueued)
                    {
                        entry.State = MakeLiveState.Pending;
                        entry.ToggledOn = false;
                        entry.ManuallyToggledOff = false;
                    }
                }

                foreach (var key in toRemove)
                    Entries.Remove(key);

                Persist();
            }
        }

        // -----------------------------------------------------------------------
        // State transitions — all persist immediately
        // -----------------------------------------------------------------------

        /// <summary>Called by MakeLiveTask before the move starts.</summary>
        public static void Register(string folderPath, string itemName, int year)
        {
            lock (Lock)
            {
                // Skip items the user removed from the queue after DequeueAll()
                // had already consumed them. SetPending() adds the path here;
                // Remove() consumes it so this is a one-shot guard per dequeue.
                if (PendingDequeues.Remove(folderPath)) return;

                // Preserve toggle state from the Queued entry so the UI doesn't
                // show the toggle snapping off when processing starts.
                MakeLiveEntry existing;
                bool toggledOn = Entries.TryGetValue(folderPath, out existing)
                    ? existing.ToggledOn : false;
                bool manuallyToggledOff = existing != null && existing.ManuallyToggledOff;

                Entries[folderPath] = new MakeLiveEntry
                {
                    FolderPath = folderPath,
                    ItemName = itemName,
                    Year = year,
                    State = MakeLiveState.Moving,
                    Message = "Starting\u2026",
                    StartedAt = DateTime.UtcNow,
                    Percent = 0,
                    ToggledOn = toggledOn,
                    ManuallyToggledOff = manuallyToggledOff,
                };
                CurrentRunPathsSet.Add(folderPath);
                Persist();
            }
        }

        /// <summary>Called periodically by MakeLiveTask as the pipeline reports progress.</summary>
        public static void SetPercent(string folderPath, int percent)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry)) return;
                entry.Percent = Math.Max(0, Math.Min(100, percent));
                Persist();
            }
        }

        /// <summary>
        /// Updates the display message on an Active entry without changing State or Percent.
        /// Called by MakeLiveTask at each pipeline stage to drive the secondary-text
        /// status line on the in-flight row. No-op if the entry is not currently Active.
        /// Not persisted — message is ephemeral display text.
        /// </summary>
        public static void SetMessage(string folderPath, string message)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry) || !entry.IsActive) return;
                entry.Message = message;
            }
        }

        /// <summary>Called after the physical move succeeds; records item id and target path.</summary>
        public static void SetScanPending(string folderPath, Guid itemId, string targetFolderPath)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry)) return;
                entry.ItemId = itemId;
                entry.TargetFolderPath = targetFolderPath;
                entry.State = MakeLiveState.ScanPending;
                entry.Message = string.Format("Library scan in progress: {0}\u2026", entry.ItemName);
                Persist();
            }
        }

        /// <summary>Called by ComingSoonEntryPoint when ItemAdded fires for the target item.</summary>
        public static void SetComplete(string folderPath)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry)) return;
                entry.State = MakeLiveState.Complete;
                entry.Message = string.Format("Made live \u2713  {0}", entry.ItemName);
                entry.CompletedAt = DateTime.UtcNow;
                entry.Percent = 100;
                Entries.Remove(folderPath);
                History.Add(entry);
                Persist();
            }
        }

        /// <summary>Called on exception/failure during move.</summary>
        public static void SetFailed(string folderPath, string message)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry)) return;
                entry.State = MakeLiveState.Failed;
                entry.Message = message;
                entry.CompletedAt = DateTime.UtcNow;
                Entries.Remove(folderPath);
                History.Add(entry);
                Persist();
            }
        }

        /// <summary>
        /// Transitions a Pending entry to Queued and records the target library path
        /// for display. Called by HandleMakeLiveAsync after the final-analysis gate passes,
        /// before EnqueueTask(). No-op if the entry does not exist or is not Pending.
        /// Not persisted — Queued is in-memory only.
        /// </summary>
        public static void SetQueued(string folderPath, string targetLibraryPath)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry) || !entry.IsPending) return;
                entry.State = MakeLiveState.Queued;
                entry.TargetLibraryPath = targetLibraryPath; // null when no move configured
            }
        }

        // -----------------------------------------------------------------------
        // SetPending — inverse of SetQueued; backs the Remove button on queued rows
        // -----------------------------------------------------------------------

        /// <summary>
        /// Reverts a Queued entry back to Pending and arranges for Register() to
        /// skip it if MakeLiveTask.DequeueAll() has already consumed it.
        ///
        /// Two-phase approach (handles both timing windows):
        ///   Phase 1 — item still in TaskQueue: best-effort removal keeps the
        ///             task from seeing it at all.
        ///   Phase 2 — item already consumed by DequeueAll(): path is added to
        ///             PendingDequeues so Register() silently skips it instead.
        ///
        /// The two locks (Lock and QueueLock) are acquired separately — never
        /// nested — to eliminate deadlock risk.
        /// </summary>
        public static void SetPending(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry) || !entry.IsQueued) return;
                entry.State = MakeLiveState.Pending;
                entry.TargetLibraryPath = null;
                PendingDequeues.Add(folderPath);
            }
            // Falls through only when an entry was successfully reverted.

            // Best-effort: also remove from TaskQueue if not yet consumed by DequeueAll().
            // Acquired separately from Lock — never nested — to avoid deadlock risk.
            // PendingDequeues (set above) covers the window where DequeueAll() has already run.
            lock (QueueLock)
            {
                int idx = TaskQueue.FindIndex(
                    q => string.Equals(
                        q.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) TaskQueue.RemoveAt(idx);
            }
        }

        /// <summary>
        /// Reverts a Queued entry back to Pending without touching TaskQueue or
        /// PendingDequeues. Used by MakeLiveTask on a hard stop: at that point
        /// DequeueAll() has already run and no further Register() calls will come
        /// for these paths, so neither queue mechanism needs touching.
        /// No-op if the entry is not Queued.
        /// </summary>
        public static void RevertQueuedToPending(string folderPath)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry) || !entry.IsQueued) return;
                entry.State = MakeLiveState.Pending;
                entry.TargetLibraryPath = null;
                // Not persisted — Pending is in-memory only.
            }
        }

        // -----------------------------------------------------------------------
        // Remove
        // -----------------------------------------------------------------------

        public static void Remove(string folderPath)
        {
            lock (Lock)
            {
                Entries.Remove(folderPath);
                Persist();
            }
        }

        /// <summary>Removes every Complete/Failed (history) row. Backs the "Clear Completed" button.</summary>
        public static void ClearCompleted()
        {
            lock (Lock)
            {
                History.Clear();
                Persist();
            }
        }

        // -----------------------------------------------------------------------
        // Queries
        // -----------------------------------------------------------------------

        public static MakeLiveEntry Get(string folderPath)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                return Entries.TryGetValue(folderPath, out entry) ? entry : null;
            }
        }

        public static MakeLiveEntry[] GetActive()
        {
            lock (Lock)
                return Entries.Values
                    .Where(e => e.IsActive)
                    .OrderBy(e => e.StartedAt)
                    .ToArray();
        }

        /// <summary>History rows (Complete/Failed), newest first.</summary>
        public static MakeLiveEntry[] GetHistory()
        {
            lock (Lock)
                return History
                    .OrderByDescending(e => e.CompletedAt ?? DateTime.MinValue)
                    .ToArray();
        }

        /// <summary>Returns true if any row is currently Moving or ScanPending.</summary>
        public static bool AnyInFlight()
        {
            lock (Lock)
                return Entries.Values.Any(e => e.IsActive);
        }

        /// <summary>
        /// Returns all Queued entries sorted alphabetically by ItemName.
        /// Queued entries are those selected for the current run that have been
        /// handed to EnqueueTask() but not yet picked up by Register().
        /// </summary>
        public static MakeLiveEntry[] GetAllQueued()
        {
            lock (Lock)
                return Entries.Values
                    .Where(e => e.IsQueued)
                    .OrderBy(e => e.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }

        /// <summary>
        /// Returns true if the entry for folderPath is currently Queued.
        /// </summary>
        public static bool IsQueuedEntry(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return false;
            lock (Lock)
            {
                MakeLiveEntry entry;
                return Entries.TryGetValue(folderPath, out entry) && entry.IsQueued;
            }
        }

        /// <summary>
        /// Returns true if the entry for folderPath exists and is currently Moving
        /// or ScanPending. Used by RebuildMovieList to filter the Coming Soon
        /// section and by BuildMovieRow to grey the Make Live button.
        /// </summary>
        public static bool IsInFlight(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return false;
            lock (Lock)
            {
                MakeLiveEntry entry;
                return Entries.TryGetValue(folderPath, out entry) && entry.IsActive;
            }
        }

        /// <summary>
        /// Clears the current-run path set and any pending dequeue requests.
        /// Called by MakeLivePageView.OnTaskCompleted after LoadAndAnalyse has
        /// refreshed rows — at that point the real library state is reflected
        /// and the guard is no longer needed.
        /// </summary>
        public static void ClearCurrentRun()
        {
            lock (Lock)
            {
                CurrentRunPathsSet.Clear();
                PendingDequeues.Clear();
            }
        }

        /// <summary>
        /// True while a MakeLiveTask execution is in progress in this server session
        /// (i.e. Register() has been called but ClearCurrentRun() has not yet been
        /// called). Used by LoadAndAnalyse to skip Emby queries during a task run.
        /// </summary>
        public static bool IsCurrentRunActive
        {
            get { lock (Lock) return CurrentRunPathsSet.Count > 0; }
        }

        // -----------------------------------------------------------------------
        // Pending-state API
        // Pending entries are the in-memory Coming-Soon snapshot. They live in
        // the same Entries dict but are excluded from Persist() so that
        // MigrationAnalysisResult (not serialisable) never hits the store.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Inserts or updates a Pending entry. Safe to call while Active entries
        /// for other paths are present — existing Active/Done entries are not touched.
        /// When updating an existing Pending entry, ToggledOn and ManuallyToggledOff
        /// are preserved so that the user's selection survives a LoadAndAnalyse refresh.
        /// </summary>
        public static void UpsertPending(
            string folderPath, string itemName, int year, MigrationAnalysisResult analysis)
        {
            lock (Lock)
            {
                MakeLiveEntry existing;
                if (Entries.TryGetValue(folderPath, out existing))
                {
                    if (!existing.IsPending)
                        return; // In-flight or queued — do not overwrite.

                    // Update in place, preserving the user's toggle choices.
                    existing.ItemName = itemName;
                    existing.Year = year;
                    existing.Analysis = analysis;
                    return;
                }

                Entries[folderPath] = new MakeLiveEntry
                {
                    FolderPath = folderPath,
                    ItemName = itemName,
                    Year = year,
                    State = MakeLiveState.Pending,
                    Analysis = analysis,
                    ToggledOn = false,
                    ManuallyToggledOff = false,
                };
                // Pending entries are intentionally not persisted.
            }
        }

        /// <summary>
        /// Updates the toggle state on a Pending entry.
        /// No-op if the entry does not exist or is not Pending.
        /// </summary>
        public static void SetPendingToggle(string folderPath, bool toggledOn, bool manuallyToggledOff)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry) || !entry.IsPending) return;
                entry.ToggledOn = toggledOn;
                entry.ManuallyToggledOff = manuallyToggledOff;
            }
        }

        /// <summary>
        /// Updates the Analysis on an existing Pending entry in place.
        /// Called by ReanalyseRow (Retry command). No-op if the entry is not Pending.
        /// </summary>
        public static void SetPendingAnalysis(string folderPath, MigrationAnalysisResult analysis)
        {
            lock (Lock)
            {
                MakeLiveEntry entry;
                if (!Entries.TryGetValue(folderPath, out entry) || !entry.IsPending) return;
                entry.Analysis = analysis;
            }
        }

        /// <summary>
        /// Returns the Pending entry for folderPath, or null if none (or if the entry
        /// is Active / Done).
        /// </summary>
        public static MakeLiveEntry GetPending(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return null;
            lock (Lock)
            {
                MakeLiveEntry entry;
                return Entries.TryGetValue(folderPath, out entry) && entry.IsPending ? entry : null;
            }
        }

        /// <summary>Returns all Pending entries sorted alphabetically by ItemName.</summary>
        public static MakeLiveEntry[] GetAllPending()
        {
            lock (Lock)
                return Entries.Values
                    .Where(e => e.IsPending)
                    .OrderBy(e => e.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }

        /// <summary>
        /// Removes all Pending entries from the in-memory store.
        /// Called at the start of LoadAndAnalyse before upserting fresh results
        /// from GetComingSoonItems(). Queued, Active, and Done entries are not affected.
        /// </summary>
        public static void ClearAllPending()
        {
            lock (Lock)
            {
                var toRemove = Entries.Values
                    .Where(e => e.IsPending)
                    .Select(e => e.FolderPath)
                    .ToArray();
                foreach (var key in toRemove)
                    Entries.Remove(key);
            }
        }

        /// <summary>
        /// Finds a ScanPending entry whose TargetFolderPath matches the given item path.
        /// Used by ComingSoonEntryPoint.OnItemAdded to detect make-live completions.
        /// </summary>
        public static MakeLiveEntry FindScanPendingByTargetPath(string itemPath)
        {
            if (string.IsNullOrEmpty(itemPath)) return null;
            lock (Lock)
            {
                foreach (var entry in Entries.Values)
                {
                    if (entry.State != MakeLiveState.ScanPending) continue;
                    if (!string.IsNullOrEmpty(entry.TargetFolderPath) &&
                        itemPath.StartsWith(entry.TargetFolderPath, StringComparison.OrdinalIgnoreCase))
                        return entry;
                }
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // Static task queue – paths handed off to MakeLiveTask. Deliberately NOT
        // persisted: an item sitting here hasn't started Moving yet, so a server
        // restart losing it is equivalent to the user never having clicked Make
        // Live — there's no partial work to recover.
        // -----------------------------------------------------------------------
        private static readonly List<PendingMakeLiveItem> TaskQueue =
            new List<PendingMakeLiveItem>();

        private static readonly object QueueLock = new object();

        public static void EnqueueTask(string folderPath, string itemName, int year,
            string targetPath, bool deleteStub, EmbyLibraryMakeService.MakeLiveMode mode,
            string customStubPath)
        {
            lock (QueueLock)
                TaskQueue.Add(new PendingMakeLiveItem
                {
                    FolderPath = folderPath,
                    ItemName = itemName,
                    Year = year,
                    TargetPath = targetPath,
                    DeleteStub = deleteStub,
                    Mode = mode,
                    CustomStubPath = customStubPath,
                });
        }

        public static PendingMakeLiveItem[] DequeueAll()
        {
            lock (QueueLock)
            {
                var items = TaskQueue.ToArray();
                TaskQueue.Clear();
                return items;
            }
        }

        public static bool AnyQueued()
        {
            lock (QueueLock)
                return TaskQueue.Count > 0;
        }

        // -----------------------------------------------------------------------
        // Shutdown — after this point Persist() is a no-op so background threads
        // cannot call into IFileSystem/IJsonSerializer after those services are disposed.
        // -----------------------------------------------------------------------

        private static volatile bool isShuttingDown;

        public static void Shutdown()
        {
            isShuttingDown = true;
        }

        // -----------------------------------------------------------------------
        // Internal persistence helper — always called under Lock
        // -----------------------------------------------------------------------

        private static void Persist()
        {
            if (store == null) return;
            if (isShuttingDown) return;
            // Pending and Queued are in-memory only — excluded from the snapshot.
            // Active entries (Moving/ScanPending) are persisted for crash recovery
            // so PrepareForPageLoad can move them to History as Failed on restart.
            var snapshot = Entries.Values
                .Where(e => e.IsActive)
                .Concat(History)
                .ToList();
            store.Save(snapshot);
        }
    }

    // -----------------------------------------------------------------------
    // DTO for the task queue
    // -----------------------------------------------------------------------
    public class PendingMakeLiveItem
    {
        public string FolderPath { get; set; }
        public string ItemName { get; set; }
        public int Year { get; set; }
        public string TargetPath { get; set; }
        public bool DeleteStub { get; set; }
        public EmbyLibraryMakeService.MakeLiveMode Mode { get; set; }
        public string CustomStubPath { get; set; }
    }
}