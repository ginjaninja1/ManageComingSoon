# Emby Evidence Log

Confirmed patterns and class behaviours for use in future Emby plugin development sessions.

## Plugin UI pages: registration, security, and concurrency

- `IHasUIPages.UIPageControllers` and `IHasTabbedUIPages.TabPageControllers` are both
  read exactly once, at plugin/server startup, by `UIPagesManager.RegisterPluginPageControllers`.
  Each controller (including every tab controller, independently of its parent) gets wrapped
  in a `PluginPageControllerHost` and stored in one process-wide
  `ConcurrentDictionary<pageId, host>`. There is no per-user or per-session instantiation.
  Practical effect: a page controller instance is shared by every concurrent user/session on
  the server for the lifetime of the process.

- `PageId` is built as `{first 6 hex chars of plugin Id}:{PageInfo.Name}`. Distinct top-level
  controllers for the same plugin just need distinct `PageInfo.Name` values — no other
  namespacing exists.

- Tab controllers registered via `IHasTabbedUIPages.TabPageControllers` are *not* just
  logically nested under their parent — they are registered as independent, directly
  addressable page controllers with their own `pageId`. Any admin/user-only gating must be
  applied to each tab controller individually, not only to the top-level/default controller,
  or the tabs remain reachable by anyone who can call `GetUIPage`/`RunUICommand` with the
  right pageId.

- `PluginPageInfo.EnableInMainMenu` / `EnableInUserMenu` are pure menu-visibility hints consumed
  by `ConfigurationPageInfo` for building dashboard/user menus. They do **not** enforce any
  server-side access control on their own. Real enforcement requires the controller (or tab
  controller) to implement `MediaBrowser.Model.Plugins.UI.IPluginPageSecurity`
  (`Task CheckIsUserAuthorised(UserDto user, IPluginUIView requestedView)`), which
  `PluginPageControllerHost.CheckUserAuthorization` will call automatically if present, on
  every view transition (`SetcurrentView`). If a controller doesn't implement this interface,
  there is no authorization check at all beyond the client hiding the menu entry — i.e. a page
  is effectively public to any authenticated caller who knows/guesses its pageId unless
  `IPluginPageSecurity` is explicitly implemented.

- `PageControllerHostBase` holds a single mutable `currentUIView` field per registered
  controller instance — not per calling user. `GetUIView`/`RunCommand` operate on this one
  shared slot: a new/different view replaces it and cancels whatever was previously "current",
  for *all* callers of that controller. This is fine when a page is only ever used by one
  admin at a time in practice, but becomes a real concurrency hazard the moment a page is
  exposed to multiple ordinary users who may open/interact with it simultaneously — one user's
  navigation can silently cancel/replace another user's in-flight view. This is a structural
  property of the SDK's page-hosting model, not something fixable from within a single plugin
  page controller; it needs to be weighed as a trade-off before opening any previously
  admin-only page up to concurrent general users.

- Tracing further: the state-mutation methods a plugin page view calls (e.g. a
  tracker class backing a search/queue list) are commonly already static,
  lock-protected, and persisted independently of any view instance. When that's
  true, the shared-`currentUIView`-slot concurrency hazard above shrinks
  considerably: swapping the current view for one user does not lose the
  other's in-progress work, since the real state was never held by the view.
  What's still lost on a swap is purely view-local cosmetic state (e.g. which
  rows are expanded) and the swapped-out view's live push-refresh wiring until
  a new request re-creates it. Worth checking whether a given page's state is
  already externalized like this before assuming a full rework is needed.

- `GenericUIApiService.Get`/`RunCommand` (the HTTP handlers backing
  `/UI/View` and `/UI/Command`) have no dedicated try/catch around the
  pages-manager call; any exception thrown from a page controller's
  `IPluginPageSecurity.CheckIsUserAuthorised` (or anywhere else in the view
  chain) propagates unhandled to the host's default exception pipeline.
  `UnauthorizedAccessException` is a reasonable, conventional choice for a
  clean "not authorised" style response, but its exact HTTP-status mapping
  was not independently confirmed via ILSpy - verify actual behaviour once
  deployed.

  Provider-id (TMDB/IMDB/etc.) library lookups
BaseItem implements IHasProviderIds, exposing a ProviderIds (ProviderIdDictionary) property. InternalItemsQuery has a matching AnyProviderIdEquals collection (ICollection<KeyValuePair<string, string>>) for querying items by external id without loading the whole library and filtering in memory.
The key string to use in that KeyValuePair is the enum member name from MediaBrowser.Model.Entities.MetadataProviders (e.g. Tmdb, Imdb, Tvdb), obtained via .ToString() — not a hand-typed literal, since the enum is the only confirmed source of the exact casing/spelling. Confirmed via ILSpy on v4.9.1.90.
This lookup is independent of any tag- or folder-based query: it finds a matching item anywhere in the library, which is useful for detecting "this external id already exists somewhere" conditions that a folder-name or tag-scoped query would miss (e.g. the same title already present under a differently-formatted folder name, or outside the folder/tag scope being searched).