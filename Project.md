# Overall Project Goal

Add Radarr integration to the Manage Coming Soon plugin, surfacing monitored-but-not-yet-downloaded Radarr movies as an Emby channel ("Radarr Coming Soon"), automatically syncing adds and removes, and playing a placeholder video when a channel item is selected.

## Pre-project Functionality

Searches TMDB for upcoming movies, creates **Coming Soon** placeholder entries in the Emby library, and promotes them to live status later.
Three tabs: **Add Movie**, **Make Live**, **Config**. **Add Movie**: search TMDB by name/year → confident match or candidate list → pick a library/path → add as Coming Soon (folder + stub `.mkv` + library refresh + tag). **Make Live**: lists Coming Soon-tagged movies → per-movie toggle → optional move to new path / delete the stub `.mkv` → removes the tag. **Configuration**: Configure the plugin.

---

# Implementation Status



---

# Project Roadmap

Separate out integration with Radarr into a new plugin called "Channel Sync".
All Radarr code inc. configuration settings on the config and the rules pages, and associated task, channels, providers, services, models, ui will be moved to the new plugin.
take the opportunity to instantiate a tidier fit for purpose project structure, with a more logical separation of concerns and a more consistent naming convention.
Show your proposed layout for the new project structure in a diagram, and explain your reasoning for the layout.
This new plugin will have be entirely self contained, with no dependencies on the Manage Coming Soon plugin, and will be able to be installed and uninstalled independently.

Future work
will be to add support for Sonarr, and other similar services, in a similar manner.



---

# AI Directives

- Don't guess classes — ask for class inspection, ilspy decompilation, or create probes if necessary.
- Ensure copious debug logging to be clear of what is going on. Info level for outcomes and counts; Debug level for per-item detail and raw payloads.
- Don't display code blocks unless the intention is for the operator to implement them.
- Keep the display of your inner monologue to a minimum, and only when it is relevant to the operator's understanding of the situation.
- Generic learning confirmed by human testing and feedback (do not assume success, ask for confirmation) about the emby api specifically (classes, patterns, interfaces) that might be useful in future projects should be recorded in evidence.md 
- Propose before coding is a firm discipline. Never start writing code until the approach has been explicitly approved.
- unknown SDK calls or patterns (unknowable from peeking alone)  should be developed and verified in an eg. DiagnosticsTask.cs.