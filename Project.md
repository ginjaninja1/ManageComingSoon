# Overall Project Goal

Add Radarr integration to the Manage Coming Soon plugin, surfacing monitored-but-not-yet-downloaded Radarr movies as an Emby channel ("Radarr Coming Soon"), automatically syncing adds and removes, and playing a placeholder video when a channel item is selected.

## Pre-project Functionality

Searches TMDB for upcoming movies, creates **Coming Soon** placeholder entries in the Emby library, and promotes them to live status later. Two tabs: **Add Movie**, **Make Live**. **Add Movie**: search TMDB by name/year → confident match or candidate list → pick a library/path → add as Coming Soon (folder + stub `.mkv` + library refresh + tag). **Make Live**: lists Coming Soon-tagged movies → per-movie toggle → optional move to new path / delete the stub `.mkv` → removes the tag. **Configuration**: Configure the plugin.

---

# Implementation Status

## Radarr Coming Soon Channel — DELIVERED AND LIVE-TESTED

Full end-to-end pipeline confirmed working against a real Emby 4.10 server:

**Files created or modified:**
- `Model/PluginConfiguration.cs` — added `RadarrEnabled`, `RadarrEnableDelete`, `RadarrSyncMode`, `RadarrRemovalStrategy`, `RadarrStubVideoPath`
- `Services/RadarrClient.cs` — added `GetComingSoonMoviesAsync` (same filter as existing `GetMissingMoviesAsync`: `Monitored && !HasFile`); auth uses `?apikey=` query string + `X-Api-Key` header; returns `null` (not empty list) on failure
- `Services/Models/RadarrChannelCache.cs` — new; flat cache POCO (`RadarrChannelCacheItem` list + `LastSyncSucceeded` + `LastSyncUtc` + `StubVideoPath`)
- `Services/TmdbService.cs` — added `GetMovieDetailsAsync(apiKey, tmdbId)` for direct-by-ID lookup (TMDB fallback for poster when Radarr's own `Images` list has no usable poster)
- `Channels/RadarrComingSoonChannel.cs` — new; implements `IChannel`, `ISupportsDelete`, `IRequiresMediaInfoCallback`
- `ScheduledTasks/RadarrChannelSyncTask.cs` — new; implements `IScheduledTask`
- `UI/Configuration/ConfigurationUI.cs` — added full Radarr section including stub video picker
- `UI/Configuration/ConfigurationPageView.cs` — wired Radarr section load/save/clear, generalized stub-video helpers

**What is confirmed working:**
- Channel auto-discovered by Emby at startup via `ChannelManager.AddParts(GetExports<IChannel>())` — no manual registration needed
- Scheduled task auto-discovered the same way via `GetExports<IScheduledTask>()`
- Radarr API queried with `?apikey=` query string
- `Monitored=true && HasFile=false` is the correct "coming soon" definition
- Cache written to `IApplicationPaths.DataPath/manage-coming-soon/radarr-channel-cache.json`
- Channel items appear in Emby with full metadata (TMDB, TVDB, images) after sync
- Add and remove both work correctly — removal is **purely implicit**: "Refresh Internet Channels" reconciles Emby's DB against whatever `GetChannelItems` returns; no `ISupportsDelete.DeleteItem` invocation required for normal sync
- Placeholder video plays via `IRequiresMediaInfoCallback.GetChannelItemMediaInfo` — this is the actual mechanism Emby calls at playback time, **not** `ChannelItemInfo.MediaSources` (which is populated anyway as a secondary hint but is not what resolves playback)
- Default stub video extracted once from embedded plugin resource to `IApplicationPaths.DataPath/manage-coming-soon/radarr-stub-default.mp4`; custom path configurable via UI
- Built-in "Refresh Internet Channels" task triggered by our sync task via `ITaskManager`, matched by Key `"RefreshInternetChannels"` (confirmed stable, non-localized) with Name `"Refresh Internet Channels"` as fallback
- `IChannelManager.GetChannel<T>()` correctly returns the server's own registered instance — constructing a separate instance and passing it to `RefreshChannelContent` throws `ArgumentException: The channel could not be found`

**Provider IDs confirmed working:**
- `ProviderIds["Tmdb"] = tmdbId` and `ProviderIds["Imdb"] = imdbId` both resolve and show in Emby's UI
- `ProviderIds["RadarrId"] = radarrId` stored for potential future two-way Radarr/Emby correlation; does not show in Emby UI (Radarr is not a registered provider)

---

# Project Roadmap

## What Is The Next Task

The following items are outstanding, in rough priority order:


**2. Channel tagging + orphan cleanup**
When Emby stores a channel it keys it by `Name` in its database (confirmed: `Channel 1358179 Radarr Coming Soon` at startup). Renaming the channel creates a new DB row and orphans the old one. Strategy: tag every channel item with a fixed plugin-identity tag (e.g. `"Created by ManageComingSoon"`) at creation time. On each sync run, probe for any channel DB entries carrying that tag whose name does not match the current configured channel name — these are stale orphans and should be cleaned. Whether Emby auto-cleans these or requires explicit action **needs a probe to confirm** before implementing the cleanup. `ILibraryManager` is likely the right interface for the orphan query.
now has a real IChannelManager.DeleteItem lead to test and the chanel has an identifyable tag.
**3. Radarr as a known Emby provider**
`RadarrId` is stored in `ProviderIds` but Emby does not recognise `"RadarrId"` as a known provider so it does not surface in the metadata UI. **Needs a probe**: investigate whether Emby allows plugins to register custom provider names (likely via some registration interface or config at startup). If possible, registering `"Radarr"` as a known provider would make the Radarr ID visible in the metadata editor. If not possible, document it as a known limitation.



## Future improvements ignore for now

### "Coming soon" rules surface in config**
Currently the only rule is `Monitored=true && HasFile=false` (hardcoded in `RadarrClient.GetComingSoonMoviesAsync`). In future, users may want to filter by genre, release window, minimum rating, etc. Design a configurable rules surface in the UI and a corresponding filter layer in the Radarr client. Out of scope for current iteration.

### Live sync mode**
`RadarrSyncMode.Live` is implemented (channel calls Radarr directly on every `GetChannelItems` request) but has not been tested. Cached mode is confirmed working and is the default. Live mode is lower priority; test when the above items are stable.


---

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




---

# AI Directives

- Don't guess classes — ask for class inspection or create probes if necessary.
- Ensure copious debug logging to be clear of what is going on. Info level for outcomes and counts; Debug level for per-item detail and raw payloads.
- Don't display code blocks unless the intention is for the operator to implement them.
- Don't code immediately — clarify understanding, ask questions, propose approach and await approval to code.
- Propose before coding is a firm discipline. Never start writing code until the approach has been explicitly approved.
- When a Radarr call fails (null result), leave cache untouched — never treat failure as "zero movies".