// ManageComingSoon - Make Live pipeline result contract
// Moved out of EmbyLibraryMakeService: this is a data contract describing the
// outcome of MakeLivePipelineAsync, not pipeline implementation, so it lives
// in Model alongside the other plugin-wide data types.

namespace ManageComingSoon.Model
{
    using System;

    // -----------------------------------------------------------------------
    // Pipeline stage enum
    // -----------------------------------------------------------------------

    public enum MakeLiveStage
    {
        ReadinessCheck = 0,
        CaptureState = 1,
        CreateTargetFolder = 2,
        EstablishTargetIds = 3,
        MoveFiles = 4,
        LibraryUpdateAndRefresh = 5,
        ConfirmTargetState = 6,
        RefreshSourceOrphans = 7,   // also deletes the now-empty source folder
        SteadyStateCheck = 8,
        UnlockTags = 9,             // reverts the ComingSoonEntryPoint Tags lock
        Complete = 10
    }

    // -----------------------------------------------------------------------
    // Pipeline result
    // -----------------------------------------------------------------------

    public class MakeLiveResult
    {
        public bool Success { get; set; }
        public MakeLiveStage? FailedAtStage { get; set; }
        public string FailureReason { get; set; }
        public Guid? FinalItemId { get; set; }

        /// <summary>
        /// True when the failure condition (e.g. insufficient disk space) affects
        /// every remaining item in the batch. MakeLiveTask uses this to revert
        /// unstarted queued items back to Pending rather than leaving them stuck.
        /// </summary>
        public bool IsHardStop { get; set; }

        /// <summary>
        /// True  = the original Movie row (same InternalId/Id) was confirmed at the new path.
        /// False = the move succeeded, but a NEW row was created (identity lost).
        /// Null  = no move was requested — identity preservation was never attempted.
        /// </summary>
        public bool? IdentityPreserved { get; set; }

        /// <summary>
        /// True = more than one item was found at the expected final path.
        /// Checked via FindAllByPath rather than FindByPath, which silently picks
        /// the newest-DateCreated match and hides duplicates.
        /// </summary>
        public bool? DuplicateDetected { get; set; }
    }
}