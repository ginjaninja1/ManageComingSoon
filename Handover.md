PLAYLIST PROTECTION PLUGIN - AI HANDOVER DOCUMENT
=====================================================

PURPOSE

This document is the single source of truth for:

  Current implementation state
  Verified Emby behaviour
  Architectural assumptions
  Target architecture
  Development ordering
  Task completion rules

The project state must always be recoverable from this document alone.

Any discrepancy between code and this document means the document is
outdated and must be updated.


CRITICAL IMPLEMENTATION RULES
=====================================================

IMPLEMENTATION IS NOT IMPLIED BY CODE EXISTENCE

The following DO NOT mean a feature exists:

  A class exists
  A file exists
  A service is registered
  A name suggests functionality
  A partial implementation exists

A feature is ONLY considered implemented when ALL of the following are true:

  Code exists
  Code compiles
  Behaviour is tested
  Behaviour is validated
  Result is recorded in this document

Until then, it is UNIMPLEMENTED.

PROVISIONAL vs PROVEN

When recording findings, always state:
  PROVEN     — empirically confirmed in Emby runtime
  AGREED     — design decision confirmed by discussion, not yet runtime-tested
  ASSUMED    — working hypothesis, not yet tested
  DEFERRED   — intentionally left open pending more knowledge


CRITICAL AI DEVELOPMENT DIRECTIVES
=====================================================

MATCH EXISTING PATTERNS
  Before writing any constructor, store, or class — find the closest
  existing equivalent in the codebase and mirror it exactly.
  Deviation requires explicit justification and approval before writing code.
  This applies to: constructor signatures, store patterns, file path resolution,
  dependency resolution, and serialisation.

NO GUESSING
  Never assume APIs exist, events exist, payload formats exist,
  behaviour exists. Verify everything.

SMALL STEPS ONLY
  One architectural step at a time.
  Must be tested before next step.

REALITY OVERRIDES DESIGN
  If code contradicts this document:
    Code is correct
    Document must be updated
    Assumptions must be corrected

NO PHASE SKIPPING
  Do not implement future stages early.

PROBE BEFORE PARSE
  When a new runtime value arrives (event payload, command args, etc.)
  log it raw first. Verify it matches assumptions before acting on it.

PROVEN vs PROVISIONAL
  Always distinguish empirically confirmed behaviour from agreed design
  decisions from untested assumptions. Working code is not proven design.

DO NOT MODIFY UIBaseClasses
  ControllerBase, PluginViewBase, PluginPageView, SimpleFileStore,
  SimpleContentStore, and related event args are project SDK wrappers.
  They are not to be modified.

FULL FILES ALWAYS
  Always provide complete files when making changes.
  Partial diffs cause integration errors.


CURRENT IMPLEMENTATION STATE (AUTHORITATIVE)
=====================================================

IMPLEMENTED AND VERIFIED
  Plugin loads successfully into Emby 4.9.1.90
  Plugin startup executes via IServerEntryPoint
  Event observability verified (see VERIFIED EMBY BEHAVIOURAL LEARNINGS)
  PlaylistEventProbe entry point (probe only — not production code)
  Three-tab UI structure renders in Emby                PROVEN (Task 5)
  PlaylistRow.cs
  PlaylistManagementUI.cs       (DxDataGrid with cell editing + onChangeCommand)
  PlaylistManagementStore.cs    (Pattern B — HashSet<string>)
  PlaylistManagementPageView.cs (RunCommand parses full ContentData, saves state)
  MainController.cs             (ILibraryManager injected)
  Plugin.cs                     (stores constructed as singletons)
  Playlist grid renders in Emby UI                      PROVEN
  RunCommand fires on cell edit                         PROVEN
  Protection toggle persists across navigation          PROVEN
  Protection toggle persists across server restart      PROVEN
  Store file written to PluginConfigurationsPath        PROVEN
  GroundTruthStore              (Pattern B — Dictionary<string, GroundTruthEntry>)
  GroundTruthEntry              (PlaylistName, CapturedAt, IsActive, Members)
  GroundTruthMember             (InternalId, Id, Name, Path, ListItemEntryId)
  ReconcileGroundTruth          (called from RunCommand after every protected set save)
  CaptureMembers                (queries via ListIds — matches Playlist.SetQueryOptions)
  Ground truth file written on protection               PROVEN
  PlaylistName present in ground truth file             PROVEN
  ListItemEntryId populated at capture time             PROVEN
  PlaylistMaintenanceService    (IServerEntryPoint — ground truth maintenance)
  Plugin.Instance               (static singleton accessor for stores)
  Add event → member added to ground truth              PROVEN (2026-06-26)
  Remove event → member removed from ground truth       PROVEN (2026-06-26)
  ListItemEntryId round-trip confirmed (add=68, remove=68)  PROVEN (2026-06-26)

  ── TASK 5 — ALL PROVEN (2026-06-27) ──────────────────────────────────

  MissingMemberEntry.cs         (flat record: PlaylistId, PlaylistName, DetectedAt, Member)
  MissingMembersStore.cs        (Pattern B — List<MissingMemberEntry>)
  MissingMemberDetectionService.cs  (IServerEntryPoint — 60-min timer + ItemRemoved fast path)
  MissingMemberRow.cs           (grid row for Tab 2)
  MissingMembersUI.cs           (DxDataGrid — grouping, header filter, filter row)
  MissingMembersPageView.cs     (RunCommand = ForgetMember, synthetic placeholder rows)
  Plugin.cs                     (MissingMembersStore singleton added)
  MainController.cs             (Tab 2 = Missing Members, Tab 3 = Config)

  Detection timer fires at 2-minute initial delay       PROVEN (2026-06-27)
  Live readback correctly reflects externally deleted member as absent  PROVEN (2026-06-27)
  Missing member detected and written to MissingMembersStore            PROVEN (2026-06-27)
  Tab 2 renders missing member grouped under playlist                   PROVEN (2026-06-27)
  Forget updates UI, GroundTruthStore and MissingMembersStore           PROVEN (2026-06-27)
  ILibraryManager.ItemRemoved payload confirmed (see BEHAVIOURAL LEARNINGS)  PROVEN (2026-06-27)

  ── TASK 5a — ALL PROVEN (2026-06-27) ─────────────────────────────────

  MissingMemberDetector.cs      (static class — shared RunDetection logic)
  MissingMemberDetectionService.cs  (updated — fast path active, delegates to Detector)
  DetectMissingMembersTask.cs   (IScheduledTask — manual run button in Emby dashboard)

  RunDetection extracted to MissingMemberDetector static class          PROVEN (2026-06-27)
  Scheduled task appears in Emby dashboard under "List Protection"      PROVEN (2026-06-27)
  Manual Run button triggers detection via MissingMemberDetector        PROVEN (2026-06-27)
  Manual run deduplicates correctly (already-known members skipped)     PROVEN (2026-06-27)
  ItemRemoved fast path active — fires targeted detection immediately    PROVEN (2026-06-27)
  Fast path detects Swan Song (InternalId=7279) before timer fires      PROVEN (2026-06-27)
  Fast path detection correctly recorded in MissingMembersStore         PROVEN (2026-06-27)
  Subsequent manual run deduplicates fast-path result correctly         PROVEN (2026-06-27)

  ── TASK 6 — ALL PROVEN (2026-07-12) ──────────────────────────────────

  CandidateDiscoveryProbeTask.cs  (Tasks/ — Step 1 field probe, now superseded — DELETE)
  CandidateEntry.cs               (Storage/ — Pattern B model)
  CandidateStore.cs               (Storage/ — Pattern B store)
  CandidateDiscoverer.cs          (EntryPoints/ — static scoring logic)
  CandidateDiscoveryTask.cs       (Tasks/ — IScheduledTask, namespace ListProtection.Tasks)
  Plugin.cs                       (CandidateStore singleton added)

  CandidateDiscoveryTask run manually from Emby dashboard               PROVEN (2026-07-12)
  69,714 Audio items queried from library in ~0.9s                      PROVEN (2026-07-12)
  137 candidates written across 11 missing members                      PROVEN (2026-07-12)
  List Protection.Candidates.json written to PluginConfigurationsPath   PROVEN (2026-07-12)
  Deduplication correct — 2 pre-existing entries skipped on re-run      PROVEN (2026-07-12)
  Score=180 candidate always correct for each missing member:
    New Heights    InternalId=1254313  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Blow Away      InternalId=1254315  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Happier        InternalId=1254316  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Swan Song      InternalId=1254317  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Think of You   InternalId=1254304  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Elements       InternalId=1254318  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    The World Without InternalId=1254319 Score=180 [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Bird of the Summer InternalId=1254320 Score=180 [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Stood Up       InternalId=1254321  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    The Beacon     InternalId=1254322  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]
    Pinesong       InternalId=1254329  Score=180  [FilenameStemExact:100, NameExact:60, ParentFolderMatch:20]

  SCORING BEHAVIOUR NOTES (PROVEN 2026-07-12):
    ParentFolderMatch:20 noise observed — sibling tracks from same album all score 20.
    This is expected and by design. Score=180 candidate is unambiguous in every case.
    Score < 100 candidates are noise. UI must filter to Score >= 100 by default.
    FilenameStemNormalized:70 fires on tracks with no "NN. " prefix in other libraries
      (Swan Song InternalId=1297968, 1300189 each Score=130 — correct secondary matches).

NOT IMPLEMENTED
  IScheduledTask post-library-scan trigger — DEFERRED (future task)
  Repair workflow
  Candidates UI (Tab 3)

PROTOTYPE / UNVALIDATED CODE
=====================================================

Present but UNVALIDATED
  ConfidenceEngine
  SimulationService
  MissingItem model (old — superseded by MissingMemberEntry)
  CandidateItem model
  Confidence rules (FilenameMatchRule, PathMatchRule)
  CandidateDiscoveryProbeTask.cs — superseded by CandidateDiscoveryTask; DELETE THIS FILE
  CandidateRefreshTask.cs — unknown content, not reviewed or integrated

RULE: These components are NOT integrated, NOT verified, NOT functional
as a system. They are design scaffolding only.


PROJECT STRUCTURE (ACTUAL)
=====================================================

NAMESPACE
  ListProtection

PLUGIN CLASS
  public class ListProtectionPlugin : BasePlugin, IHasThumbImage, IHasUIPages

  BasePlugin (not generic)
  IHasUIPages — exposes UIPageControllers (IReadOnlyCollection)
  Single entry: MainController

PLUGIN DEPENDENCIES (constructor-injected)
  IServerApplicationHost
  ILogManager
  ILibraryManager

STORES (constructed in Plugin.cs as singletons, passed by constructor injection
        or accessed via ListProtectionPlugin.Instance)
  PlaylistManagementStore  — file: List Protection.Playlist.json
  GroundTruthStore         — file: List Protection.GroundTruth.json
  ConfigStore              — file: List Protection.Configuration.json
  MissingMembersStore      — file: List Protection.MissingMembers.json
  CandidateStore           — file: List Protection.Candidates.json

  Stores are singletons — constructed once in Plugin.cs, shared across all
  navigations via MainController and IServerEntryPoint implementations.
  This ensures consistent reads and writes regardless of navigation timing.
  Do not construct stores inside lambdas or per-view — they must outlive
  individual view instances.

  IServerEntryPoint implementations are DI-resolved and cannot receive stores
  via constructor injection. Access stores via ListProtectionPlugin.Instance
  (set at end of Plugin constructor — safe to read from Run() onwards).

FILE LAYOUT (ACTUAL)
  Plugin.cs
  UIBaseClasses/                    <- project SDK wrappers — DO NOT MODIFY
    ControllerBase.cs
    PluginViewBase.cs
    PluginPageView.cs
    PluginDialogueView.cs
    Store/
      SimpleFileStore.cs
      SimpleContentStore.cs
      FileSavingEventArgs.cs
      FileSavedEventArgs.cs
  EntryPoints/
    PlaylistEventProbe.cs           <- probe only, not production code
    PlaylistMaintenanceService.cs   <- production ground truth maintenance
    MissingMemberDetectionService.cs <- production missing member detection + fast path
    MissingMemberDetector.cs        <- static shared detection logic
    CandidateDiscoverer.cs          <- static shared candidate scoring logic
  Tasks/
    DetectMissingMembersTask.cs     <- IScheduledTask — manual run, namespace ListProtection.Tasks
    CandidateDiscoveryTask.cs       <- IScheduledTask — manual run, namespace ListProtection.Tasks
    CandidateDiscoveryProbeTask.cs  <- DELETE — superseded, probe only
    CandidateRefreshTask.cs         <- unknown, unreviewed
  UI/
    MainController.cs
    TabPageController.cs
    PlaylistManagement/
      PlaylistRow.cs
      PlaylistManagementUI.cs
      PlaylistManagementPageView.cs
    MissingMembers/
      MissingMemberRow.cs
      MissingMembersUI.cs
      MissingMembersPageView.cs
    Candidates/                     <- Task 7 — TO BE CREATED
      CandidateRow.cs
      CandidatesUI.cs
      CandidatesPageView.cs
    Config/
      ConfigUI.cs                   <- placeholder, acceptable
      ConfigPageView.cs             <- placeholder, acceptable
  Storage/
    PlaylistManagementStore.cs      <- Pattern B
    GroundTruthStore.cs             <- Pattern B
    GroundTruthEntry.cs
    GroundTruthMember.cs
    ConfigStore.cs                  <- Pattern A, acceptable
    MissingMembersStore.cs          <- Pattern B
    MissingMemberEntry.cs
    CandidateStore.cs               <- Pattern B
    CandidateEntry.cs


EMBY PLUGIN UI FRAMEWORK (FULLY AUDITED)
=====================================================

--- PLUGIN SIGNATURE (ACTUAL) ---

  public class ListProtectionPlugin : BasePlugin, IHasThumbImage, IHasUIPages

  IHasUIPages exposes UIPageControllers (IReadOnlyCollection<IPluginUIPageController>)
  MainController is the single entry in UIPageControllers.
  MainController also implements IHasTabbedUIPages — this is how tabs are achieved.
  The outer IHasUIPages + inner IHasTabbedUIPages pattern is PROVEN working.

--- TAB ARCHITECTURE ---

  MainController : ControllerBase, IHasTabbedUIPages
    Owns TabPageControllers (IReadOnlyList<IPluginUIPageController>)
    Each tab is a TabPageController — a generic factory taking a view factory func
    CreateDefaultPageView() on MainController returns Tab 1 view directly

  TabPageController : ControllerBase
    Constructor: (PluginInfo, name, displayName, Func<PluginInfo, IPluginUIView>)
    CreateDefaultPageView() invokes the factory func
    Stateless — view is constructed fresh on each navigation

--- UIBaseClasses (PROJECT SDK WRAPPERS — DO NOT MODIFY) ---

  ControllerBase
    abstract — requires PageInfo and CreateDefaultPageView()
    PluginId passed to base constructor

  PluginViewBase
    implements IPluginUIView, IPluginViewWithOptions
    ContentData (IEditableObject) — the EditableOptionsBase subclass
    RunCommand(itemId, commandId, data) — virtual, returns null by default
    RaiseUIViewInfoChanged() — triggers Emby re-render

  PluginPageView : PluginViewBase
    adds ShowSave, ShowBack, AllowSave, AllowBack
    OnSaveCommand(itemId, commandId, data) — virtual

  SimpleFileStore<T> where T : EditableOptionsBase, new()
    GetOptions() — loads from JSON on first call, cached thereafter
    SetOptions(T) — writes to JSON, fires FileSaving/FileSaved events
    ReloadOptions() — force reload from disk
    File path: PluginConfigurationsPath/{pluginFullName}.json

--- CLIENT-SERVER INTERACTION MODEL ---

  Emby UI is round-trip stateless:
  1. CreateDefaultPageView() called -> build view from store (pure projection)
  2. User interacts with UI element -> command fires
  3. Emby calls RunCommand(itemId, commandId, data) on the view
  4. RunCommand -> parse data -> update store -> rebuild ContentData -> return this
  5. Emby re-renders from returned view

  STABILITY RULE: Store is always source of truth.
  View is always a pure projection of store + live Emby data.
  Never hold mutable state on the view between round-trips.

--- DxDataGrid (PROVEN FROM DLL) ---

  Namespace: Emby.Web.GenericEdit.Elements
  Constructor: DxDataGrid(DxGridOptions options)
  No default constructor — DxGridOptions is always required.

  DxGridOptions constructor:
    DxGridOptions(object editObject, string keyExpr, bool multiSelect,
                  bool disableColumnChooser, bool showFilterRow, bool showHeaderFilter)

    editObject         — instance of row class; DxColumnBuilder derives columns from it
    keyExpr            — property name used as row key
    showFilterRow      — when true, adds a per-column text filter row
    showHeaderFilter   — when true, adds a multi-select popup filter on column headers
    Columns are auto-built from editObject's properties via DxColumnBuilder.

  DxGridOptions.onChangeCommand — property (lowercase camelCase)
    Type: DxGridOnChangeCommand
    Namespace: Emby.Web.GenericEdit.Elements.DxGrid

  DxGridOnChangeCommand properties (lowercase camelCase):
    commandId  string  — arrives as commandId in RunCommand

  DxGridOptions.editing — NULL by default; constructor never sets it.
    Without it all cells are read-only and onChangeCommand never fires.
    REQUIRED for interactive cells:
      editing = new DxGridEditing
      {
          mode = DxGridEditing.GridEditMode.cell,
          allowUpdating = true
      }

  DxGridEditing properties (from DLL):
    allowUpdating   bool?           — must be true for any editing
    allowAdding     bool?           — row add (not needed here)
    allowDeleting   bool?           — row delete (not needed here)
    mode            GridEditMode?   — row/batch/cell/form/popup
    startEditAction string          — "click" or "dblClick" (default: "click")
    refreshMode     GridEditRefreshMode?
    highlightEditableColumns bool?

  GridEditMode.cell — single click edits in place; widget fires onChangeCommand
  on value change with no Save button. Correct mode for toggle columns.

  DxGridOptions CONSTRUCTOR WIRES AUTOMATICALLY (from decompiled DLL — PROVEN):
    grouping    — DxGridGrouping { allowCollapsing=true, contextMenuEnabled=true, autoExpandAll=false }
    summary     — DxGridSummary with groupItems count badge keyed by keyExpr (free per group)
    headerFilter — DxGridHeaderFilter { visible=showHeaderFilter }
    filterRow    — DxGridFilterRow { visible=showFilterRow }
    sorting      — DxGridSorting { mode=multiple, showSortIndexes=true }
    scrolling    — virtual mode
    export       — enabled

  DxGridColumn — SETTABLE PROPERTIES RELEVANT TO THIS PROJECT (from DLL — PROVEN):
    dataField       string    — matches property name exactly (PascalCase) — PROVEN Tab 2
    groupIndex      double?   — set to 0 to group by this column initially — PROVEN Tab 2
    showWhenGrouped bool?     — set false to hide column when used for grouping
    autoExpandGroup bool?     — per-column group expand default
    allowEditing    bool?     — overrides editing.allowUpdating for this column
    allowGrouping   bool?     — set false to prevent user grouping by this column
    allowHeaderFiltering bool? — overrides headerFilter.visible for this column
    visible         bool?     — set false to hide column entirely
    visibleIndex    int?      — column order
    sortIndex       double?   — initial sort
    sortOrder       string    — "asc" or "desc"
    caption         string    — column header display text
    width           int?      — pixels or percent

  DxGridColumn POST-PROCESSING PATTERN (Tab 2 — PROVEN):
    After DxGridOptions construction, iterate options.columns and mutate by dataField:
      "Key", "IsSynthetic" -> visible = false, allowEditing = false
      "PlaylistName"       -> groupIndex = 0, showWhenGrouped = false,
                             autoExpandGroup = false, allowEditing = false,
                             allowHeaderFiltering = true
      "DetectedAt"         -> allowEditing = false, allowHeaderFiltering = true
      "Forget"             -> leave editable, allowHeaderFiltering = false
      default              -> allowEditing = false

  CURRENT USAGE — Tab 1 (PlaylistManagementUI):
    new DxDataGrid(
        new DxGridOptions(new PlaylistRow(), "Id", false, true, false, false)
        {
            editing = new DxGridEditing
            {
                mode = DxGridEditing.GridEditMode.cell,
                allowUpdating = true
            },
            onChangeCommand = new DxGridOnChangeCommand { commandId = "ToggleProtection" }
        })

  CURRENT USAGE — Tab 2 (MissingMembersUI):
    new DxDataGrid(
        new DxGridOptions(new MissingMemberRow(), "Key", false, true, true, true)
        {
            editing = new DxGridEditing
            {
                mode = DxGridEditing.GridEditMode.cell,
                allowUpdating = true
            },
            onChangeCommand = new DxGridOnChangeCommand { commandId = "ForgetMember" }
        })
    + column post-processing (see above)

--- ILibraryManager.GetItemList (PROVEN FROM DLL) ---

  Returns: BaseItem[] (array — use .Length not .Count)
  Overloads: (InternalItemsQuery), (InternalItemsQuery, CancellationToken),
             (InternalItemsQuery, IDataContext, CancellationToken)

--- IScheduledTask (PROVEN FROM DLL — Task 5a) ---

  Interface: MediaBrowser.Model.Tasks.IScheduledTask
  DI-resolved by Emby assembly scanner — same pattern as IServerEntryPoint.

  Members (PROVEN from decompiled DLL):
    string Name { get; }
    string Key { get; }
    string Description { get; }
    string Category { get; }
    Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    IEnumerable<TaskTriggerInfo> GetDefaultTriggers()

  Appears in Emby dashboard under Scheduled Tasks — has manual Run button.
  Category groups tasks in the dashboard UI.
  GetDefaultTriggers() returning Array.Empty<TaskTriggerInfo>() = manual run only.
  Stores accessed via ListProtectionPlugin.Instance (same pattern as IServerEntryPoint).
  Constructor injection of ILibraryManager and ILogManager works normally.
  Namespace: ListProtection.Tasks   CONFIRMED (from DetectMissingMembersTask.cs)

--- IPlaylistManager (FROM DLL — Task 7) ---

  Confirmed interface members relevant to repair:

  void AddToPlaylist(long playlistId, long[] itemIds, User user)
    — fire-and-forget overload. playlistId = playlist InternalId (long).
      itemIds = array of candidate InternalIds to add.
      User parameter required — see USER RESOLUTION below.

  Task<AddToPlaylistResult> AddToPlaylist(Playlist playlist, long[] itemIds,
      bool skipDuplicates, User user, CancellationToken cancellationToken)
    — async overload with duplicate guard. Preferred for repair workflow.
      skipDuplicates = true recommended — guards against double-add.

  Task RemoveFromPlaylist(long playlistId, long[] entryIds)
  Task RemoveFromPlaylist(Playlist playlist, long[] entryIds)
    — entryIds are ListItemEntryIds (not InternalIds).

  IPlaylistManager must be constructor-injected. It is a standard DI service.
  Inject into MainController (already has ILibraryManager pattern to follow).
  Pass to CandidatesPageView via constructor.

  USER RESOLUTION (AGREED — not yet proven):
    AddToPlaylist requires a User object.
    IUserManager.GetUsers() returns all users — take first admin or first user.
    IUserManager must be constructor-injected alongside IPlaylistManager.
    Exact method signature to be confirmed from DLL before use.
    PROBE REQUIRED: log IUserManager.GetUsers() result before calling AddToPlaylist.

  PLAYLIST OBJECT RESOLUTION FOR ASYNC OVERLOAD (AGREED — not yet proven):
    Async overload takes Playlist (not long). Resolve via:
      GetItemList(IncludeItemTypes=["Playlist"]) filtered by InternalId or Guid.
      Cast result to Playlist.
    Same pattern already used in CaptureMembers and MissingMemberDetector.


STORAGE ARCHITECTURE
=====================================================

PATTERN A — SimpleFileStore<T> where T : EditableOptionsBase
  Framework-coupled. T is both stored object and UI definition.
  GetOptions() / SetOptions(T) API.
  Used for: ConfigStore (acceptable, stays as-is)

PATTERN B — Plain store (no base class)
  Stores any plain type. No framework coupling.
  You own serialisation, locking, file path.
  Constructor signature: (IApplicationHost applicationHost, ILogger logger, string pluginFullName)
  File path resolved via: applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath
  Serialiser resolved via: applicationHost.Resolve<IJsonSerializer>()
  FileSystem resolved via: applicationHost.Resolve<IFileSystem>()
  All stores use lock object for thread safety.
  Used for: PlaylistManagementStore, GroundTruthStore, MissingMembersStore, CandidateStore

  CRITICAL: Always match this exact constructor signature when adding new Pattern B stores.
  Never resolve path or serialiser differently from the existing stores.

CURRENT STORES

  ConfigStore : SimpleFileStore<ConfigUI>
    File: List Protection.Configuration.json
    Status: Pattern A — acceptable, no change needed

  PlaylistManagementStore (Pattern B)
    File: List Protection.Playlist.json
    API: Load() -> HashSet<string>, Save(HashSet<string>)
    Stores Guid "N" format strings of protected playlist IDs
    Status: PROVEN

  GroundTruthStore (Pattern B)
    File: List Protection.GroundTruth.json
    API: Load() -> Dictionary<string, GroundTruthEntry>, Save(Dictionary<string, GroundTruthEntry>)
    Key: playlist Guid "N" format string
    Value: GroundTruthEntry (PlaylistName, CapturedAt, IsActive, Members)
    Soft-delete: IsActive = false when playlist unprotected (entry retained)
    Status: PROVEN

  MissingMembersStore (Pattern B)
    File: List Protection.MissingMembers.json
    API: Load() -> List<MissingMemberEntry>, Save(List<MissingMemberEntry>)
    Structure: FLAT LIST (not keyed by playlist)
    Rationale: cross-playlist identity — repair/candidate valid for Member.Id on one
               playlist is valid on all playlists. Flat list makes this a simple
               Where clause. Per-playlist lookup is equally simple.
    Status: PROVEN (2026-06-27)

  CandidateStore (Pattern B)
    File: List Protection.Candidates.json
    API: Load() -> List<CandidateEntry>, Save(List<CandidateEntry>)
    Structure: FLAT LIST, sorted by Score descending on save
    Status: PROVEN (2026-07-12) — 137 candidates written

GROUND TRUTH ENTRY SHAPE

  GroundTruthEntry
    PlaylistName    string    — snapshot-time name, for troubleshooting only
                                not used for logic; name may change after capture
    CapturedAt      DateTime  — UTC timestamp of snapshot
    IsActive        bool      — false = soft-deleted, snapshot retained
    Members         List<GroundTruthMember>

  GroundTruthMember
    InternalId      long      — Emby internal ID, fast for in-process lookup
    Id              string    — Guid "N", durable across restarts
    Name            string    — item name at capture time
    Path            string    — file path at capture time
    ListItemEntryId long      — for correlating PlaylistItemsRemoved events
                                PROVEN populated at capture time (Task 3)
                                PROVEN round-trip: add=68, remove=68 (Task 4)

MISSING MEMBER ENTRY SHAPE

  MissingMemberEntry
    PlaylistId      string            — Guid "N", matches key in GroundTruthStore
    PlaylistName    string            — from ground truth at detection time, display only
    DetectedAt      DateTime          — UTC, when member was first identified missing
    Member          GroundTruthMember — full snapshot from ground truth at detection time
                                        Member.Id (Guid "N") is cross-playlist identity key

CANDIDATE ENTRY SHAPE

  CandidateEntry
    PlaylistId            string            — Guid "N", matches key in GroundTruthStore
    PlaylistName          string            — display only
    MissingMember         GroundTruthMember — the member this candidate was found for
    CandidateInternalId   long              — InternalId of candidate library item
    CandidateId           string            — Guid "N" of candidate library item
    CandidateName         string            — Name at discovery time
    CandidatePath         string            — Path at discovery time
    Score                 int               — composite score, higher is better
    MatchedSignals        List<string>      — human-readable signal breakdown
    DiscoveredAt          DateTime          — UTC

CANDIDATE SCORING SIGNALS (PROVEN — 2026-07-12)

  Signals are derived from GroundTruthMember.Name and .Path only.

  FilenameStemExact      100 — candidate FileNameWithoutExtension == GT path stem (case-insensitive)
  FilenameStemNormalized  70 — after stripping "NN. " track prefix, stems match
  NameExact               60 — candidate Name == GT Name (case-insensitive)
  NameNormalized          40 — after normalising whitespace/case, names match
  ParentFolderMatch       20 — immediate parent folder name matches
                               (year prefix "[YYYY] " stripped before comparison)

  Minimum score to record: > 0
  UI display threshold: Score >= 100 (filters ParentFolderMatch-only noise)
  Store sorted by Score descending on save.
  Deduplication: (PlaylistId, MissingMember.InternalId, CandidateInternalId) triple.

GROUND TRUTH RECONCILE LOGIC (called from RunCommand after every save)

  1. For every entry in store not in protected set -> set IsActive = false (soft-delete)
  2. For every id in protected set:
       If active entry exists -> no action
       If soft-deleted entry exists -> restore IsActive = true (silently for now)
       If no entry exists -> capture fresh snapshot via CaptureMembers
  3. Save updated store

  CaptureMembers queries:
    First: GetItemList(IncludeItemTypes=Playlist) to find playlist BaseItem by Guid
    Then:  GetItemList(ListIds=[playlist.InternalId]) to get members
    Same ListIds pattern used internally by Playlist.SetQueryOptions

GROUND TRUTH MAINTENANCE LOGIC (PlaylistMaintenanceService — Task 4)

  On PlaylistItemsAdded (PROVEN):
    Check if playlist is protected (load PlaylistManagementStore via Instance)
    If protected: record ListItemId(s) in _pendingAdds keyed by playlist InternalId
    Await ItemUpdated for correlation

  On ItemUpdated (Type == Playlist) (PROVEN):
    Check if pending add correlation exists (_pendingAdds.TryRemove)
    If so: readback via playlist.GetItemList(new InternalItemsQuery())
    Match InternalId -> recover ListItemEntryId
    Duplicate guard: skip if ListItemEntryId already present in ground truth
    Add new GroundTruthMember, save store

  On PlaylistItemsRemoved (PROVEN):
    Check if playlist is protected
    If protected: iterate members backwards, match ListItemEntryId, remove
    Save store if any removed

MISSING MEMBER DETECTION LOGIC (MissingMemberDetector — Task 5/5a — PROVEN)

  RunDetection(targetPlaylistIdN, libraryManager, logger):
    For each active GroundTruthEntry (filtered to target if provided):
      1. Find playlist BaseItem by Guid via GetItemList(IncludeItemTypes=Playlist)
      2. Get live members via GetItemList(ListIds=[playlist.InternalId])
      3. Build HashSet<long> of live InternalIds
      4. For each ground truth Member:
           If InternalId absent from live set -> missing candidate
           Dedup: skip if already in MissingMembersStore for same PlaylistId+InternalId
           Add MissingMemberEntry, set changed=true
      5. If changed: save MissingMembersStore

  Trigger paths:
    Timer: 60-minute interval, 2-minute initial delay after startup   PROVEN
    Fast path (ItemRemoved): active, fires targeted detection          PROVEN (Task 5a)
    Manual (IScheduledTask): DetectMissingMembersTask                 PROVEN (Task 5a)

FORGET SEMANTICS (MissingMembersPageView — PROVEN 2026-06-27)

  Forget = permanent removal from both stores.
  RunCommand finds rows where Forget==true and IsSynthetic==false.
  For each:
    Remove matching MissingMemberEntry from MissingMembersStore
    Remove matching Member (by InternalId) from GroundTruthStore entry
  Save both stores. Rebuild view.
  Member is no longer tracked, never surfaces again.

MISSING MEMBER KEY FORMAT

  Real row key:       "{PlaylistId}_{Member.InternalId}"
    PlaylistId is always 32 hex chars (Guid "N").
    Parsing: PlaylistId = Key.Substring(0, 32), InternalId = long.Parse(Key.Substring(33))

  Synthetic row key:  "synthetic_{PlaylistId}"
    RunCommand identifies these by IsSynthetic == true and skips entirely.

CANDIDATE DISCOVERY LOGIC (CandidateDiscoverer — Task 6 — PROVEN 2026-07-12)

  RunDiscovery(targetPlaylistIdN, libraryManager, logger):
    1. Load MissingMembersStore, GroundTruthStore, CandidateStore
    2. Query all Audio items once: GetItemList(IncludeItemTypes=["Audio"], Recursive=true)
    3. For each MissingMemberEntry (filtered to target if provided):
         Build exclusion set from ground truth for that playlist
         Score each Audio item not in exclusion set against the missing member
         Record candidates with score > 0
         Dedup by (PlaylistId, MissingMember.InternalId, CandidateInternalId)
    4. If any new candidates: sort store by Score desc, save CandidateStore

  Trigger paths:
    Manual (IScheduledTask): CandidateDiscoveryTask   PROVEN (2026-07-12)

REPAIR LOGIC (Task 7 — AGREED, not yet implemented)

  RepairMember command flow:
    1. Parse CandidatesUI from RunCommand data
    2. Find rows where Repair == true
    3. For each repair row (key = "{PlaylistId}_{MissingMember.InternalId}_{CandidateInternalId}"):
         a. Resolve playlist Playlist object via GetItemList(IncludeItemTypes=Playlist)
         b. Resolve User via IUserManager (see USER RESOLUTION above — PROBE FIRST)
         c. Call IPlaylistManager.AddToPlaylist(playlist, [CandidateInternalId],
                skipDuplicates: true, user, CancellationToken.None)
         d. On success:
              Remove MissingMemberEntry from MissingMembersStore (by PlaylistId+InternalId)
              Remove all CandidateEntries for same (PlaylistId, MissingMember.InternalId)
              Ground truth will self-update via PlaylistMaintenanceService
                (PlaylistItemsAdded -> ItemUpdated -> new GroundTruthMember written)
              Do NOT manually update GroundTruthStore — let the event chain handle it
    4. Save stores, rebuild view

  CRITICAL: Do NOT update GroundTruthStore manually on repair.
    The existing PlaylistMaintenanceService event chain (PlaylistItemsAdded ->
    ItemUpdated -> readback -> write) is already proven to handle this correctly.
    Manually writing to ground truth would race with the event chain.


VERIFIED EMBY BEHAVIOURAL LEARNINGS
=====================================================

These behaviours have been empirically tested and confirmed.

--- SDK / API NOTES ---

  The online Emby API docs and the actual 4.9.1.90 DLL sometimes disagree.
  Always verify against the decompiled assembly when in doubt.

  BaseItem.InternalId   — long property (docs show it, DLL confirms it)
  Folder.GetItemList()  — returns BaseItem[] (array), NOT List<BaseItem>
                          Use .Length not .Count
  ILibraryManager.GetItemList() — returns BaseItem[] (array)
                          Use .Length not .Count
  MediaBrowser.Server.Core NuGet (4.9.1.90) contains all necessary
  assemblies including Emby.Web.GenericEdit. No additional packages needed.

--- AUDIO ITEM FIELDS (PROVEN FROM PROBE — 2026-06-27, Task 6) ---

  All fields confirmed populated on Audio items returned from
  GetItemList(IncludeItemTypes=["Audio"], Recursive=true):

    BaseItem.Name                     — populated
    BaseItem.Path                     — populated
    BaseItem.FileName                 — populated (computed from Path)
    BaseItem.FileNameWithoutExtension — populated (computed from Path)
    BaseItem.Album                    — populated
    BaseItem.RunTimeTicks             — populated (long?)
    BaseItem.IndexNumber              — populated (int?, null for non-disc albums)
    BaseItem.ParentIndexNumber        — null in test library (single-disc)
    BaseItem.ProductionYear           — populated (int?)
    Audio.Artists                     — populated (lazy-load via EnsureTaggedItemsLoaded
                                        is NOT required — field populates from GetItemList)
    Audio.AlbumArtists                — populated (same)

  Cast pattern: (item as Audio) to access Audio-specific fields.
  Cast returns null if item is not Audio subtype — guard before use.

--- PLAYLIST UI REMOVAL ---

  When a user removes an item from a playlist via Emby UI:
    Playlist updates immediately
    Underlying m3u updates immediately

--- EXTERNAL FILE DELETION ---

  When media is deleted outside Emby:
    Emby detects change during library scan
    Item is removed from library
    ILibraryManager.ItemRemoved fires immediately on detection
    Playlist membership is updated (item absent from live readback)
    PROVEN: live GetItemList readback after deletion correctly excludes the item
    PROVEN: ItemRemoved fires before the PlaylistEventProbe log line appears —
            fast path detection completes before the probe even logs the event

--- MEDIA RESTORATION ---

  When deleted media is restored:
    Emby does NOT automatically restore playlist membership
    Manual repair is required

--- EVENT OBSERVABILITY (TASK 1 — FULLY VERIFIED) ---

  EVENTS THAT FIRE ON PLAYLIST MEMBERSHIP CHANGE:

    IPlaylistManager.PlaylistItemsAdded   fires on member add
    IPlaylistManager.PlaylistItemsRemoved fires on member remove
    ILibraryManager.ItemUpdated           fires after add (not after remove)

  NOTE: ILibraryManager.ItemAdded and ItemRemoved do NOT fire for
  playlist membership changes.

  PAYLOAD — PlaylistItemsAdded (PlaylistItemsAddedEventArgs):
    Playlist         — the Playlist object (Name, InternalId, Id, Path)
    ListItem[]       — one per added item
      ListItemEntryId  — always 0 at event time (not yet assigned)
      ListItemId       — InternalId of the media item being added

  PAYLOAD — PlaylistItemsRemoved (PlaylistItemsRemovedEventArgs):
    Playlist         — the Playlist object
    long[]           — ListItemEntryIds of the removed entries

  PAYLOAD — ILibraryManager.ItemUpdated (ItemChangeEventArgs):
    Item             — the Playlist as a BaseItem
      Type             — "Playlist"
      InternalId       — playlist internal ID (long)
      Id               — playlist GUID
      Path             — path to .m3u file

  PAYLOAD — ILibraryManager.ItemRemoved (ItemChangeEventArgs) — PROVEN 2026-06-27:
    Same type as ItemUpdated: ItemChangeEventArgs
    Item.Type        — "Audio" (the media item type, not "Playlist")
    Item.Name        — media item name
    Item.InternalId  — media item InternalId (long) — CONFIRMED POPULATED
    Item.Id          — media item Guid
    Item.Path        — original file path
    Example: Type=Audio | Name=What I Wouldn't Do | InternalId=7274
             Id=8c97b172-e640-df0a-f63d-471ec26d41f2
             Path=D:\Music Test\A Fine Frenzy\[2009] Bomb in a Birdcage\01. What I Wouldn't Do.flac

  IMPORTANT: ItemRemoved fires for ANY library item deletion, not just playlist members.
  Fast path must check whether removed InternalId appears in any active ground truth entry
  before triggering detection. Guard is present in OnItemRemoved.

--- LISTITERENTRYID ASSIGNMENT (CRITICAL ARCHITECTURAL FINDING) ---

  ListItemEntryId is NOT available at PlaylistItemsAdded event time (always 0).
  It is assigned by Emby during the DB write that follows.
  ILibraryManager.ItemUpdated fires after the DB write is complete.

  RESOLUTION STRATEGY (PROVEN):
    1. PlaylistItemsAdded fires -> record ListItemId (e.g. 19071)
    2. ILibraryManager.ItemUpdated fires for same playlist
    3. Call playlist.GetItemList(new InternalItemsQuery())
    4. Each returned BaseItem has InternalId and ListItemEntryId populated
    5. Match InternalId == ListItemId -> recover ListItemEntryId (e.g. 68)

--- ROUND-TRIP PROOF (Blue Christmas, 2026-06-24) ---

  Add event:    ListItemId=7285, ListItemEntryId=0
  Readback:     InternalId=7285, ListItemEntryId=68
  Remove event: ListItemEntryId=68  <- exact match

--- ROUND-TRIP PROOF (Child In Time, 2026-06-26, Task 4) ---

  Add event:    ListItemId=19071, ListItemEntryId=0 (queued in _pendingAdds)
  Readback:     InternalId=19071, ListItemEntryId=68  <- assigned after ItemUpdated
  Ground truth: member written with ListItemEntryId=68
  Remove event: ListItemEntryId=68 -> member matched and removed
  Ground truth: member absent from file
  End-to-end maintenance cycle PROVEN.

--- MISSING MEMBER DETECTION PROOF (What I Wouldn't Do, 2026-06-27, Task 5) ---

  File deleted externally: D:\Music Test\A Fine Frenzy\[2009] Bomb in a Birdcage\01. What I Wouldn't Do.flac
  Library scan detected deletion.
  ItemRemoved fired:  InternalId=7274, Type=Audio
  Detection timer fired at 2-minute initial delay.
  Live readback: 11 members returned — InternalId=7274 absent.
  Ground truth: 12 members including InternalId=7274.
  Result: MissingMemberEntry written to MissingMembersStore.
  Tab 2: missing member displayed grouped under PLaylist 1.
  Forget actioned: both MissingMembersStore and GroundTruthStore updated correctly.
  UI: row disappeared after forget.
  Full Task 5 cycle PROVEN end-to-end.

--- FAST PATH AND SCHEDULED TASK PROOF (Swan Song, 2026-06-27, Task 5a) ---

  File deleted externally: D:\Music Test\A Fine Frenzy\[2009] Bomb in a Birdcage\06. Swan Song.flac
  LibraryMonitor detected change at 05:41:46.
  Fast path fired at 05:41:56 — before ItemRemoved probe log line appeared.
  Fast path ran targeted detection for playlist e617f097c8d08fa563aa29b7118d898c.
  Live readback: 7 members — InternalId=7279 (Swan Song) absent.
  MissingMemberEntry written to MissingMembersStore.
  Subsequent manual task run at 05:42:19: Swan Song correctly deduplicated.
  Manual task also ran at 05:40:17 and 05:41:51 with correct dedup of 3 earlier entries.
  Full Task 5a cycle PROVEN end-to-end.

--- CANDIDATE DISCOVERY PROOF (New Heights et al., 2026-07-12, Task 6) ---

  CandidateDiscoveryTask run manually from Emby dashboard.
  Library: 69,714 Audio items queried in ~0.9s.
  11 missing members processed. 137 candidates written to CandidateStore.
  Every missing member found its exact match at Score=180.
  New Heights (InternalId=1254313) found in new folder location — Score=180.
  ParentFolderMatch:20 noise confirmed — sibling album tracks score 20 each.
  Score >= 100 filter will eliminate all noise candidates.
  Deduplication: 2 pre-existing entries from prior partial run skipped correctly.
  Full Task 6 cycle PROVEN end-to-end.

--- REMOVE EVENT DOES NOT FIRE ItemUpdated ---

  When an item is removed via UI, only PlaylistItemsRemoved fires.
  ILibraryManager.ItemUpdated does NOT follow a remove.
  This means no readback is needed or possible on remove —
  the ground truth system must already hold the mapping.

--- ISERVICEENTRYPOINT AND STORE ACCESS PATTERN (PROVEN — 2026-06-26) ---

  IServerEntryPoint implementations are DI-resolved by Emby's assembly scanner.
  They cannot receive stores via constructor injection (stores are not DI-registered).
  Access stores via ListProtectionPlugin.Instance (static, set at end of Plugin
  constructor — safe to read from Run() onwards, as Run() is called after all
  plugins are constructed).
  PlaylistMaintenanceService, MissingMemberDetectionService, and
  DetectMissingMembersTask all use this pattern.

--- RunCommand PAYLOAD (PROVEN — 2026-06-26) ---

  itemId   — always null when fired from DxDataGrid cell edit.
             keyExpr does NOT route to itemId. Ignore itemId entirely.

  commandId — arrives correctly as the value set in
              DxGridOnChangeCommand.commandId.

  data     — contains the ENTIRE ContentData object serialised as JSON.
             Deserialise as the full UI class and extract rows of interest.
             Do NOT attempt toggle-by-id logic — the full state is in data.

--- DxDataGrid EDITING (PROVEN FROM DLL — 2026-06-26) ---

  DxGridOptions.editing is NULL by default — the constructor never sets it.
  Without it, all cells render read-only and onChangeCommand never fires.

--- DxDataGrid GROUPING (PROVEN — 2026-06-27, Tab 2) ---

  groupIndex = 0 on PlaylistName column produces correct grouping.
  Groups collapsed by default (autoExpandAll=false in DxGridGrouping).
  Count badge per group header works automatically via DxGridSummary.
  showWhenGrouped = false correctly hides the PlaylistName data column.
  dataField values match property names exactly (PascalCase) — CONFIRMED.

--- ListItemEntryId AT CAPTURE TIME (PROVEN — 2026-06-26) ---

  ListItemEntryId is correctly populated when reading playlist members
  via ILibraryManager.GetItemList with ListIds outside of an event context.

--- CANDIDATE FIELD PROBE (PROVEN — 2026-06-27, Task 6) ---

  Probe: CandidateDiscoveryProbeTask run against 64 Audio items in library.
  Missing member target: 'New Heights' | InternalId=7275
    Path: D:\Music Test\A Fine Frenzy\[2009] Bomb in a Birdcage\02. New Heights.flac
  Ground truth exclusion: 11 items excluded (playlist members), 1 excluded (the target itself).
  5 candidates logged raw. All fields confirmed populated — see AUDIO ITEM FIELDS above.


UI DESIGN
=====================================================

TAB 1 - MANAGED PLAYLISTS (COMPLETE — PROVEN)
  View all playlists with protection status
  Toggle protection on/off per playlist (immediate, no Save button)

TAB 2 - MISSING MEMBERS (COMPLETE — PROVEN 2026-06-27)
  Grid grouped by playlist (PlaylistName column, groupIndex=0, collapsed by default)
  Header filter on PlaylistName — multi-select popup for per-playlist filtering
  Header filter on DetectedAt — date-based filtering
  Filter row on MemberName/Path — text search
  Forget column — cell-edit checkbox, immediate, no Save button
  Synthetic placeholder row per protected playlist with no missing members
    ("No missing members" in MemberName column, IsSynthetic=true, Forget ignored)

TAB 3 - CANDIDATES / REPAIR (Task 7 — TO BE IMPLEMENTED)
  Grid grouped by missing member (MissingMemberName column)
  Filtered to Score >= 100 by default (suppresses ParentFolderMatch noise)
  Columns: MissingMemberName, PlaylistName, CandidateName, CandidatePath,
           Score, MatchedSignals, Repair (checkbox)
  Repair column — cell-edit checkbox, fires RepairMember command
  On repair: add candidate to playlist via IPlaylistManager, clean stores
  Synthetic placeholder rows for missing members with no qualifying candidates

TAB 4 - CONFIGURATION (FUTURE — currently Tab 3 placeholder)
  Plugin settings
  Tuning options
  Note: Tab 3 must be repurposed to Candidates, Config becomes Tab 4

UNPROTECT BEHAVIOUR (PRODUCTION — FUTURE):
  Remove playlist from PlaylistManagementStore
  Soft-delete ground truth entry in GroundTruthStore (IsActive = false)
  Currently: soft-delete is implemented, full cleanup deferred


CURRENT TASK (AUTHORITATIVE)
=====================================================

TASK
  TASK 7 — CANDIDATES UI AND REPAIR WORKFLOW

STATUS
  Not started. All prerequisite data (CandidateStore) is proven and populated.

WHAT TO BUILD

  Step 1 — PROBE IUserManager before writing any repair code.
    Inject IUserManager into MainController.
    In a probe IServerEntryPoint or temporary log in CandidatesPageView,
    call IUserManager.GetUsers() and log:
      - number of users returned
      - each user's Name, Id, IsAdministrator (or equivalent)
    Confirm the method signature from DLL before implementing.
    Goal: establish which User object to pass to AddToPlaylist.
    DO NOT call AddToPlaylist until User resolution is confirmed.

  Step 2 — Build CandidateRow.cs
    Properties (pattern: mirror MissingMemberRow.cs):
      Key               string   — "{PlaylistId}_{MissingMember.InternalId}_{CandidateInternalId}"
      PlaylistName      string   — display only
      MissingMemberName string   — GT member Name
      MissingMemberPath string   — GT member Path (for disambiguation)
      CandidateName     string   — candidate item Name
      CandidatePath     string   — candidate item Path
      Score             int      — composite score
      Signals           string   — MatchedSignals joined as display string
      Repair            bool     — action checkbox
      IsSynthetic       bool     — for placeholder rows (hidden column)

  Step 3 — Build CandidatesUI.cs
    DxDataGrid with:
      editObject = new CandidateRow(), keyExpr = "Key"
      showFilterRow = true, showHeaderFilter = true
      editing.mode = cell, allowUpdating = true
      onChangeCommand = "RepairMember"
    Column post-processing:
      "Key", "IsSynthetic"     -> visible = false, allowEditing = false
      "MissingMemberName"      -> groupIndex = 0, showWhenGrouped = false,
                                  autoExpandGroup = false, allowEditing = false,
                                  allowHeaderFiltering = true
      "PlaylistName"           -> allowEditing = false, allowHeaderFiltering = true
      "MissingMemberPath"      -> allowEditing = false
      "CandidateName"          -> allowEditing = false
      "CandidatePath"          -> allowEditing = false
      "Score"                  -> allowEditing = false, sortIndex = 0, sortOrder = "desc"
      "Signals"                -> allowEditing = false
      "Repair"                 -> leave editable

  Step 4 — Build CandidatesPageView.cs
    Constructor: inject ILibraryManager, IPlaylistManager, IUserManager (after probe)
    BuildContentData():
      Load CandidateStore, filter to Score >= 100
      Group by (PlaylistId, MissingMember.InternalId) — take top candidate per group
        OR show all qualifying candidates per missing member (decide after seeing volume)
      Build CandidateRow per entry
      Add synthetic placeholder per missing member with no qualifying candidates
    RunCommand("RepairMember"):
      Follow REPAIR LOGIC spec above exactly.
      Log all intermediate values before acting (probe pattern).

  Step 5 — Wire into MainController
    Add Tab 3 = Candidates (new CandidatesPageView)
    Move existing Config tab to Tab 4
    Inject IPlaylistManager and IUserManager into MainController constructor

DEFINITION OF DONE
  IUserManager.GetUsers() probed and User resolution confirmed
  CandidateRow, CandidatesUI, CandidatesPageView built
  Tab 3 renders candidate grid in Emby UI
  Repair checkbox fires RepairMember command
  RepairMember calls AddToPlaylist successfully
  Repaired member reappears in playlist (verified in Emby UI)
  MissingMembersStore and CandidateStore cleaned up correctly
  PlaylistMaintenanceService picks up the add event and updates GroundTruth
  All results recorded in this document
  Next task defined


IMPLEMENTATION ROADMAP (ORDERED)
=====================================================

1. Verify event observability                         <- COMPLETE
2. Playlist protection UI (Tab 1)                     <- COMPLETE
3. Ground truth capture on protection                 <- COMPLETE
4. Ground truth maintenance (add/remove events)       <- COMPLETE
5. Missing member detection                           <- COMPLETE (proven 2026-06-27)
5a. Fast path activation + scheduled task             <- COMPLETE (proven 2026-06-27)
6. Candidate discovery                                <- COMPLETE (proven 2026-07-12)
7. Candidates UI + repair workflow                    <- CURRENT TASK
8. Post-library-scan trigger (deferred)
9. Configuration UI (deferred)


SESSION COMPLETION RULE
=====================================================

A session is NOT complete unless:
  Code is written
  Code is tested
  Behaviour verified
  Results recorded
  This document updated
  Next task defined

If any step is missing, work is incomplete.


FUTURE IDEAS (NOT MVP)
=====================================================

  Ground truth restore prompt — when a playlist is re-ticked and a
  soft-deleted snapshot exists, prompt user to restore old snapshot
  or start fresh. Currently silently restores. Requires investigation
  of whether Emby framework supports confirmation dialog in RunCommand
  context. Flag as improvement when implementing re-tick path.

  Playlist recovery from Emby m3u files
  Advanced matching rules
  Improved confidence scoring
  Additional metadata enrichment
  Post-library-scan trigger for DetectMissingMembersTask (natural fit —
    add a TaskTriggerInfo for AfterLibraryScan once trigger type is confirmed
    from DLL. Current manual-only approach is correct until proven.)

  PlaylistMaintenanceService — _pendingAdds is never purged if ItemUpdated
  fails to fire (e.g. server crash between add and update). Low risk in
  practice: stale entries are long values that cost nothing, and the next
  protection capture will re-sync the ground truth. Consider a TTL-based
  cleanup if this becomes observable.

  MissingMemberDetectionService — timer interval (60 min) is a constant.
  Future: expose via ConfigStore so user can tune via Tab 4.

  Show all candidates vs top-only per missing member — current plan is
  Score >= 100 filter with all qualifying shown. If volume is high,
  consider top-1 default with expand option.

  CandidateRefreshTask.cs — file exists but content is unknown and
  unreviewed. Review and either integrate or delete before Task 7 ships.

These are explicitly deferred.

AI SESSION BOOTSTRAP INSTRUCTIONS
=====================================================

These instructions exist because AI assistants consistently waste time at
session start trying to read this file via web search or web fetch tools,
both of which fail. Follow this procedure exactly.

READING THIS FILE
  The only method that works is bash curl against raw.githubusercontent.com.
  The default branch is MASTER (not main) — CONFIRMED by repository owner.

  curl -sL "https://raw.githubusercontent.com/ginjaninja1/ListProtection/master/Handover.md"

  Do not attempt:
    web_search for the file — returns unrelated results
    web_fetch of github.com URLs — blocked unless URL appeared in prior search
    web_fetch of raw.githubusercontent.com — same restriction
    GitHub API (api.github.com) — rate-limited without auth token

  To discover branch name or file list if needed:
    curl -sL "https://github.com/ginjaninja1/ListProtection" \
      | grep -oP 'href="/ginjaninja1/ListProtection/blob/[^"]+'

WRITING THIS FILE
  This document cannot be pushed to GitHub from the AI session.
  Workflow:
    1. AI produces updated Handover.md as a file artifact for download
    2. User manually commits and pushes to the repository
  Do not attempt git operations or GitHub API writes.

READING SOURCE FILES
  To read any other file in the repository:
    curl -sL "https://raw.githubusercontent.com/ginjaninja1/ListProtection/master/PATH/TO/FILE"
  Replace PATH/TO/FILE with the relative path from repo root.
  Branch is master.

=====================================================
END OF DOCUMENT