# Emby Evidence Log

Confirmed patterns and class behaviours for use in future Emby plugin development sessions.

## Channel Registration

`IChannel` implementations are **auto-discovered** by Emby at server startup via `ChannelManager.AddParts(GetExports<IChannel>())`. No manual `AddParts` call in plugin code is needed or correct. Same auto-discovery applies to `IScheduledTask`. Simply implement the interface as a public class in the plugin assembly.

## IChannel Interface (MediaBrowser.Controller.Channels)

```
string Name { get; }
string Description { get; }
ChannelParentalRating ParentalRating { get; }
Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
IEnumerable<ImageType> GetSupportedChannelImages()
```

## IChannelManager (MediaBrowser.Controller.Channels)

```
int ChannelCount { get; }
T GetChannel<T>() where T : IChannel   // returns the server's own registered instance — use this, never inject your own
Task RefreshChannelContent(IChannel channel, int maxRefreshLevel, string restrictTopLevelFolderId, CancellationToken)
```

**Critical**: `RefreshChannelContent` validates its argument against the server's internally registered instances. Passing a separately-constructed instance throws `ArgumentException: The channel could not be found`. Always use `GetChannel<T>()` to retrieve the instance.

**Critical**: `RefreshChannelContent` alone does NOT persist channel items into Emby's database. It signals intent but does not assign `InternalId`/`Guid` or run metadata providers. Only Emby's own built-in **"Refresh Internet Channels"** task does the actual persistence (confirmed by `ILibraryManager.ItemAdded` events firing only after that task runs).

## Triggering "Refresh Internet Channels" Programmatically

Use `ITaskManager.ScheduledTasks` to find the worker, matched by `IScheduledTaskWorker.ScheduledTask.Key`:

```csharp
var worker = taskManager.ScheduledTasks
    .FirstOrDefault(w => string.Equals(w.ScheduledTask?.Key, "RefreshInternetChannels", StringComparison.OrdinalIgnoreCase))
    ?? taskManager.ScheduledTasks
        .FirstOrDefault(w => string.Equals(w.Name, "Refresh Internet Channels", StringComparison.OrdinalIgnoreCase));

await taskManager.Execute(worker, new TaskOptions());
```

Key `"RefreshInternetChannels"` confirmed stable and non-localized on Emby 4.10. Name `"Refresh Internet Channels"` is a localization-fragile fallback.

## IScheduledTaskWorker (MediaBrowser.Model.Tasks)

```
IScheduledTask ScheduledTask { get; }   // gives access to Key
string Name { get; }
string Id { get; }
TaskState State { get; }
TaskTriggerInfo[] Triggers { get; set; }
```

## ChannelItemInfo (MediaBrowser.Controller.Channels)

Key fields confirmed working:
- `Id` — your own stable string key (e.g. `"radarr-coming-soon-{tmdbId}"`)
- `Name`, `OriginalTitle`, `Overview`, `ProductionYear`, `ImageUrl`
- `Type = ChannelItemType.Media`
- `MediaType = ChannelMediaType.Video` (MediaBrowser.Model.Channels)
- `ContentType = ChannelMediaContentType.Movie` (MediaBrowser.Model.Channels)
- `ProviderIds` — plain `Dictionary<string,string>`; keys `"Tmdb"` and `"Imdb"` are recognised by Emby and show in the metadata UI
- `MediaSources` — populated as a secondary hint but **not** what Emby actually calls at playback time (see `IRequiresMediaInfoCallback` below)

## Item Removal (Implicit Reconciliation)

Removal is **purely implicit**. When "Refresh Internet Channels" runs, it calls `GetChannelItems` and reconciles Emby's database against the returned list. Items no longer returned are deleted (`ILibraryManager.ItemRemoved` fires). `ISupportsDelete.DeleteItem` is **not** called during normal sync reconciliation — it only applies to user-initiated deletes from Emby's own UI. No explicit delete call is needed in the sync task.

## ISupportsDelete (MediaBrowser.Controller.Channels)

```
bool CanDelete(BaseItem item)
Task DeleteItem(string id, CancellationToken cancellationToken)
```

Implement to gate user-initiated deletes from Emby's UI. Not required for sync-driven removal.

## IRequiresMediaInfoCallback (MediaBrowser.Controller.Channels)

```
Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
```

**This is the actual mechanism Emby calls at playback time** (via the `PlaybackInfo` POST request). Emby does NOT use `ChannelItemInfo.MediaSources` populated in `GetChannelItems` to resolve playback. If this interface is not implemented, playback always fails with "No compatible streams". Populate `MediaSourceInfo.Path` with a real on-disk file path (`Protocol = MediaProtocol.File`) for Direct Play of a local stub video.

## Channel Items vs Library Items

Channel items do not have a folder on disk. A single shared stub file can be pointed at by all channel items' `MediaSourceInfo.Path`. Extract the stub once from the plugin's embedded resource to a persistent disk location (e.g. `IApplicationPaths.DataPath/manage-coming-soon/stub.mp4`) and reuse it. Never re-extract if the file already exists.

## Channel Persistence and Database Identity

Emby stores a `Channel` entity in its database keyed by `IChannel.Name`. The channel's `InternalId` and `Guid` are assigned when "Refresh Internet Channels" first persists it. If `IChannel.Name` changes (e.g. user renames the channel via config), Emby creates a **new** `Channel` DB row — the old one becomes a stale orphan. Tags on channel items can be used as the identity anchor for orphan detection.

## Radarr API Authentication

Query string format: `GET {RadarrUrl}/api/v3/movie?apikey={apiKey}`
Also send `X-Api-Key` header as fallback (some reverse proxies strip one or the other).
Return `null` (not empty list) on any failure — callers must treat null as "sync skipped, leave existing state untouched", never as "zero movies qualify".

## InternalChannelItemQuery (MediaBrowser.Controller.Channels)

```
string FolderId { get; set; }
long UserId { get; set; }
int? StartIndex { get; set; }
int? Limit { get; set; }
```

## ChannelParentalRating (MediaBrowser.Controller.Channels)

`ChannelParentalRating.GeneralAudience` — confirmed member name.

Roadmap item 4 (Channel image) — DONE.
Confirmed working solution, minimal form:
csharpprivate void ReapplyChannelImage(BaseItem item)
{
    if (item.HasImage(ImageType.Primary))
    {
        return; // only ever needs to run once per item
    }

    var imagePath = ResolveChannelImagePath(); // extract embedded thumb.png to disk once, cache path thereafter
    var imageSize = imageProcessor.GetImageSize(imagePath); // IImageProcessor — real Width/Height required, zero values silently fail to render

    item.SetImage(new ItemImageInfo
    {
        Path = imagePath,
        Type = ImageType.Primary,
        DateModified = DateTimeOffset.UtcNow,
        Width = (int)imageSize.Width,
        Height = (int)imageSize.Height
    }, 0);

    libraryManager.UpdateImages(item); // sufficient alone — no UpdateToRepository/UpdateItem or IProviderManager.OnRefreshComplete needed
}
Key findings for evidence log:

IChannel.GetChannelImage/GetSupportedChannelImages are called by Emby exactly once, at the moment "Refresh Internet Channels" first persists a new Channel DB row (keyed by Name). Never re-invoked for an existing row — confirmed by direct test (deleted image via UI, re-browsed, zero calls).
Renaming the channel (RadarrChannelName) forces a new DB row and orphans the old one — same mechanism, now with a live orphan example seen in testing.
ItemImageInfo.Width/Height must be real values from IImageProcessor.GetImageSize(path), not left at 0 — suspected (not fully isolated) to be why initial attempts didn't render despite the DB record looking correct in Edit Images.
Setting BaseItem.ImageInfos via SetImage alone does not make Emby's live web API serve the image — confirmed via direct test: DB/Edit-Images screen showed it correctly, but the running server didn't serve it until an unrelated full restart.
ILibraryManager.UpdateImages(BaseItem) alone is sufficient to invalidate/propagate to the live server without a restart. BaseItem.UpdateToRepository(ItemUpdateType.ImageUpdate) and IProviderManager.OnRefreshComplete(item, collectionFolders) are not required in addition — confirmed by direct isolation test.
item.HasImage(ImageType.Primary) is the correct guard to make this idempotent/cheap — avoids re-running on every sync.
Channel.CanDelete() is hardcoded false — orphaned Channel rows cannot be removed via the standard ILibraryManager.DeleteItem path. IChannelManager.DeleteItem(BaseItem) is an unexplored, more promising lead for roadmap item #2 (orphan cleanup) — not yet tested.
RadarrChannelIdentityTag (fixed tag, independent of RadarrChannelName) is implemented and confirmed working — survives renames, correctly distinguishes current channel from orphans via InternalItemsQuery.Tags + Name matching.

TitleSlug as Radarr's primary identity (this session)
Radarr's internal numeric movie.Id (Radarr's own DB primary key) does not correspond to any real Radarr URL. Radarr's own web UI and API use titleSlug for movie detail URLs (/movie/{titleSlug}). For the two real movies tested, titleSlug happened to equal tmdbId as a string — this is not guaranteed to always be true (Radarr's slugs are normally derived from title text, e.g. mission-impossible-1996); treat this as observed behavior for the tested data, not a documented Radarr guarantee. TitleSlug is now the identity used for:

ChannelItemInfo.Id (BuildItemId)
ProviderIds["RadarrId"] (via RadarrExternalId's clickable URL)
ISupportsDelete.DeleteItem matching

If TitleSlug is ever empty/null for a given Radarr movie, that item is dropped from the channel entirely (logged at Warn) rather than risk an ambiguous/duplicate identity under some fallback scheme. TmdbId was considered as a fallback but explicitly rejected — kept incidental, used only for Emby's own recognized ProviderIds["Tmdb"] key, not for our own identity logic.
ILibraryManager.UpdateItem — confirmed working for correcting stale ProviderIds
void UpdateItem(BaseItem item, BaseItem parent, ItemUpdateType updateReason);
Confirmed via live test: setting item.ProviderIds["SomeKey"] = newValue then calling this overload persists correctly and fires ILibraryManager.ItemUpdated. This is the same overload already used successfully elsewhere in this codebase for the Channel item's identity tag (ApplyIdentityTag).
IChannelManager.DeleteItem — confirmed working
Task DeleteItem(BaseItem item);
Confirmed via live test: called against an orphaned Channel BaseItem (found via tag query, name mismatch against current config), it fully removes the item from Emby's database (log evidence: "Removing item from database", associated metadata path deletions, ILibraryManager.ItemRemoved event). This was previously an unexplored lead per the roadmap; it is now a confirmed, working mechanism.
IExternalId — confirmed working
csharppublic interface IExternalId
{
    string Name { get; }
    string Key { get; }
    string UrlFormatString { get; }
    bool Supports(IHasProviderIds item);
}
Auto-discovered the same way as IChannel/IScheduledTask (via GetExports<T>()), no manual registration needed. UrlFormatString is evaluated per-render, so it can safely read live plugin configuration (ManageComingSoonPlugin.Instance.Configuration.RadarrUrl) rather than needing a static/hardcoded URL. {0} in the format string is substituted with whatever value is stored under the matching ProviderIds[Key].
Confirmed: IHasSupportedExternalIdentifiers (a separate interface, tied to IRemoteMetadataProvider) was not needed just to make the ID clickable/visible — IExternalId alone was sufficient for this plugin's narrow "surface for troubleshooting" goal. IHasSupportedExternalIdentifiers would only be relevant if the plugin needed to act as a genuine Emby metadata provider (Identify/Refresh Metadata workflows) — explicitly out of scope, confirmed with the user.
InternalItemsQuery — additional confirmed members (this session)
public BaseItem Parent { get; set; }  // setter also populates ParentIds internally
public long[] ParentIds { get; set; }
Parent is the simpler way to scope a query to children of a specific BaseItem (e.g. Movie items under a Channel) — confirmed working via the post-sync diagnostic query in this session.

