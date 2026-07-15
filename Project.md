# Overall Project Goal

Add Radarr integration to the Manage Coming Soon plugin, surfacing monitored-but-not-yet-downloaded Radarr movies as an Emby channel ("Radarr Coming Soon"), automatically syncing adds and removes, and playing a placeholder video when a channel item is selected.

## Pre-project Functionality

Searches TMDB for upcoming movies, creates **Coming Soon** placeholder entries in the Emby library, and promotes them to live status later.
Three tabs: **Add Movie**, **Make Live**, **Config**. **Add Movie**: search TMDB by name/year → confident match or candidate list → pick a library/path → add as Coming Soon (folder + stub `.mkv` + library refresh + tag). **Make Live**: lists Coming Soon-tagged movies → per-movie toggle → optional move to new path / delete the stub `.mkv` → removes the tag. **Configuration**: Configure the plugin.

---

# Implementation Status



---

# Project Roadmap



### "Coming soon" rules surface in config**
Currently the only rule is `Monitored=true && HasFile=false` (hardcoded in `RadarrClient.GetComingSoonMoviesAsync`). In future, users may want to filter by genre, release window, minimum rating, etc. Design a configurable rules surface in the UI and a corresponding filter layer in the Radarr client.
We need to read the json respone to identify the fields that are available for filtering. The rules surface should allow users to select which fields to filter on and specify the desired values.
The generic UI does not have a control for this type of ui, will need you to build in html/js/css. The rules surface should be able to handle multiple rules and combine them with AND/OR logic. It should show available rules and implemented rule. Users should be able drag and drop to reorder rules, and remove rules. The rules surface should also allow users to save and load rule sets.

Rules engine backend (design agreed, not yet built)
Base filter behaviour: monitored && !hasFile is NOT a hardcoded floor — it ships only as the pre-populated default rule set on first install, fully editable/deletable like any other rule set.
Field library (flattened, alphabetical, from sample Radarr /api/v3/movie payload):
TypeFieldsOperatorsBooleanmonitored, hasFile, isAvailable, qualityCutoffNotMetEQ, NEQNumberyear, runtime, sizeOnDisk, tmdbId, qualityProfileId, popularity, ratings.imdb.value, ratings.tmdb.value, statistics.movieFileCountLT, LTE, GT, GTE, EQ, NEQDateinCinemas, physicalRelease, digitalRelease, releaseDate, addedLT, LTE, GT, GTE, EQ, NEQString (enum-like)status, minimumAvailability, certification, studio, rootFolderPathEQ, NEQ, CONTAINS, NOTCONTAINSString (freeform)title, overview, pathCONTAINS, NOTCONTAINS, EQ, NEQList of primitivesgenres, keywords, tagsCONTAINS, NOTCONTAINS (any-element match)List of objectsalternateTitles (hardcoded to .title), images (hardcoded to .coverType)CONTAINS, NOTCONTAINS (any-element match)
Nested objects flattened to dotted-path field names (e.g. ratings.imdb.value) rather than exposed as pickable sub-trees.
Expression tree model: Group nodes (AND/OR) containing Conditions or nested Groups. Condition = field + operator + value. NOT = a toggle badge on any Condition or Group (agreed: simpler than a true draggable NOT block, avoids ambiguous drop positions).
Storage — two new JSON files under manage-coming-soon/:

radarr-rulesets.json — named rule sets (tree + metadata) + which one is active.
radarr-last-response.json — raw Radarr movie array from the most recent sync (any mode), written every run, purely to power live match preview in the UI without an extra Radarr call.

Default favourited fields on fresh install: monitored, hasFile, status, rootFolderPath, genres, year, releaseDate. Favourites pinned to top of palette, alphabetical within each group (favourites, then everything else).
Phase 3 — Rules UI (design agreed, not yet built)
Custom HTML/JS/CSS panel (generic Emby edit UI has no control for this):

Draggable palette: field library (alphabetical, favourites pinned top), the 8 operators (LT/LTE/GT/GTE/EQ/NEQ/CONTAINS/NOTCONTAINS), a typable value placeholder that becomes a bound input once dropped onto a condition.
Expression canvas: nested AND/OR groups, drag-to-reorder, add/remove, NOT toggle badge per node.
Live preview: evaluates the in-progress rule tree against radarr-last-response.json client-side, shows real-time match count/list as a guide.
Save/load/rename named rule sets, one marked active.

---

# AI Directives

- Don't guess classes — ask for class inspection or create probes if necessary.
- Ensure copious debug logging to be clear of what is going on. Info level for outcomes and counts; Debug level for per-item detail and raw payloads.
- Don't display code blocks unless the intention is for the operator to implement them.
- Generic learning confirmed by human testing and feedback (do not assume success, ask for confirmation) about the emby api specifically (classes, patterns, interfaces) that might be useful in future projects should be recorded in evidence.md 
- Propose before coding is a firm discipline. Never start writing code until the approach has been explicitly approved.
- untested/unproven SDK calls or patterns should be developed and verified in RadarrDiagnosticsTask.cs (a manual-trigger-only scheduled task) first. Only move confirmed-working code into the permanent RadarrChannelSyncTask.cs path once proven live.