# Manage Coming Soon – Emby Plugin

Searches TMDB for upcoming movies, creates **Coming Soon** placeholder entries in the Emby library, and promotes them to live status later. Two tabs: **Add Movie**, **Make Live**.

This file is a context doc for Claude across chats — not user-facing setup docs. Keep it terse; cut anything that doesn't help route a bug report or feature request to the right file/behavior.

---


---

## Tabs (functional summary)

- **Add Movie**: search TMDB by name/year → confident match or candidate list → pick a library/path → add as Coming Soon (folder + stub `.mkv` + library refresh + tag). See Current Dev Status above for the live A/B pipeline details.
- **Make Live**: lists Coming Soon-tagged movies → per-movie toggle → optional move to new path / delete the stub `.mkv` → removes the tag.

---

## Architecture / File Map

└── ChannelEntryPoint.cs
└── ChannelUI.cs
└── ComingSoonEntryPoint.cs
└── ManageComingSoonPlugin.cs
📂 Model/
    └── PluginConfiguration.cs
    └── TmdbMovieResult.cs
📂 Services/
    └── AddMovieEntry.cs
    └── AddMovieTask.cs
    └── AddMovieTracker.cs
    └── EmbyLibraryAddService.cs
    └── EmbyLibraryMakeService.cs
    └── EmbyLibraryService.cs
    └── EmbyLibrarySharedService.cs
    └── MakeLiveTask.cs
    └── MakeLiveTracker.cs
    └── MigrationAnalyzer.cs
    └── TmdbService.cs
📂 Storage/
    └── AddMovieStore.cs
    └── MakeLiveStore.cs
    └── PluginConfigStore.cs
        └── Addmoviepageview.commands.cs
        └── Addmoviepageview.conflict.cs
        └── AddMoviePageView.cs
        └── Addmoviepageview.polling.cs
        └── Addmoviepageview.rebuild.cs
        └── AddMoviePageView.rowbuilders.cs
        └── AddMovieUI.cs
        └── LibraryPicker.cs
        └── ConfigurationPageView.cs
        └── ConfigurationUI.cs
        └── MakeLivePageView.commands.cs
        └── MakeLivePageView.cs
        └── MakeLivePageView.polling.cs
        └── MakeLivePageView.rebuild.cs
        └── MakeLivePageView.rowbuilders.cs
        └── MakeLiveUI.cs
📂 UI/
    📂 AddMovie/
    📂 Configuration/
    📂 MakeLive/
    └── MainPageController.cs
    └── TabPageController.cs
📂 UIBaseClasses/
    └── UIBaseClasses.cs