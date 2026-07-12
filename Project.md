# Overall project goal

Add the following functionality to the Manage Coming Soon plugin.
Gather "coming soon movies" from radarr via API
As Raddar movies come into scope they should be added by delivering silently to add movie engine**
As Raddar movies come out of sope they should be removed from coming soon library. (their movie folder and sub contents deleted). Exterme care should be taken during development that deletes are properly targetted (no more than X files on approved file list Z and Y folder should be deleted) - where x (default 2) and y (default 1) and z (default poster.jpg, movie folder name.ext (needs some sort of placeholder like {moviefile}),) are configuration elements. Radaar will needs its own configuration section. Also Radar apikeu, Radararr URL, enable radarr y/n, enable delete y/n.
As a users adds movies to radarr they should be added to coming soon, as movies are fulfilled on radarr and no longer 'awaiting' they should be removed from coming soon.



## Preproject functionality
Searches TMDB for upcoming movies, creates **Coming Soon** placeholder entries in the Emby library, and promotes them to live status later. Two tabs: **Add Movie**, **Make Live**.
**Add Movie**: search TMDB by name/year → confident match or candidate list → pick a library/path → add as Coming Soon (folder + stub `.mkv` + library refresh + tag). See Current Dev Status above for the live A/B pipeline details.
**Make Live**: lists Coming Soon-tagged movies → per-movie toggle → optional move to new path / delete the stub `.mkv` → removes the tag.
**Configuration**: Configure the plugin.

# Project Roadmap
## What is the next task
Build out the design and project roadmap
**Investigate emby ichannel framework. Ichannel can create media items and remove them. It may prove a better way of creating coming soon movies. Removing an ichannel item is inherantly less dangerous than a file system delete.
Inspect relevant classes and record in evidence log
Confirm next step.
## What is in the pipeline





# Project Log
A record of what was delivered and when




# Ai Directives
Dont guess classes, ask for class inspection and create probes if neccessary.
Ensure copius debug logging to be clear of what is going on.
Dont display code blocks unless the intention is for the operator to implement them.
Dont code immediately, clarify understanding, ask questions and propose approach and await approval to code.


# Emby Evidence Log (record learnings and class patterns to save time on this and future projects)