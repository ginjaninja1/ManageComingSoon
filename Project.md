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


---

# AI Directives

- Don't guess classes — ask for class inspection or create probes if necessary.
- Ensure copious debug logging to be clear of what is going on. Info level for outcomes and counts; Debug level for per-item detail and raw payloads.
- Don't display code blocks unless the intention is for the operator to implement them.
- Don't code immediately — clarify understanding, ask questions, propose approach and await approval to code.
- Propose before coding is a firm discipline. Never start writing code until the approach has been explicitly approved.
- When a Radarr call fails (null result), leave cache untouched — never treat failure as "zero movies".
- untested/unproven SDK calls or patterns must be developed and verified in RadarrDiagnosticsTask.cs (a manual-trigger-only scheduled task) first. Only move confirmed-working code into the permanent RadarrChannelSyncTask.cs path once proven live.