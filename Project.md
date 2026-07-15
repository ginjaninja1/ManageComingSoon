# Overall Project Goal

Add Radarr integration to the Manage Coming Soon plugin, surfacing monitored-but-not-yet-downloaded Radarr movies as an Emby channel ("Radarr Coming Soon"), automatically syncing adds and removes, and playing a placeholder video when a channel item is selected.

## Pre-project Functionality

Searches TMDB for upcoming movies, creates **Coming Soon** placeholder entries in the Emby library, and promotes them to live status later.
Three tabs: **Add Movie**, **Make Live**, **Config**. **Add Movie**: search TMDB by name/year → confident match or candidate list → pick a library/path → add as Coming Soon (folder + stub `.mkv` + library refresh + tag). **Make Live**: lists Coming Soon-tagged movies → per-movie toggle → optional move to new path / delete the stub `.mkv` → removes the tag. **Configuration**: Configure the plugin.

---

# Implementation Status



---

# Project Roadmap

### Disable Radarr should remove the Radarr channel
Enable should enable the channel, disable should remove it. This is a simple toggle in the plugin config.

### "Coming soon" rules surface in config**
Currently the only rule is `Monitored=true && HasFile=false` (hardcoded in `RadarrClient.GetComingSoonMoviesAsync`). In future, users may want to filter by genre, release window, minimum rating, etc. Design a configurable rules surface in the UI and a corresponding filter layer in the Radarr client. Out of scope for current iteration.

### Default name for Radarr channel should be "Raddarr Coming Soon"
---

# AI Directives

- Don't guess classes — ask for class inspection or create probes if necessary.
- Ensure copious debug logging to be clear of what is going on. Info level for outcomes and counts; Debug level for per-item detail and raw payloads.
- Don't display code blocks unless the intention is for the operator to implement them.
- Generic learning confirmed by human testing and feedback (do not assume success, ask for confirmation) about the emby api specifically (classes, patterns, interfaces) that might be useful in future projects should be recorded in evidence.md 
- Propose before coding is a firm discipline. Never start writing code until the approach has been explicitly approved.
- untested/unproven SDK calls or patterns should be developed and verified in RadarrDiagnosticsTask.cs (a manual-trigger-only scheduled task) first. Only move confirmed-working code into the permanent RadarrChannelSyncTask.cs path once proven live.