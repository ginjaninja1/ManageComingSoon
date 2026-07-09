// ManageComingSoon - Emby Library Shared Service
// Base class holding every helper that both EmbyLibraryAddService and
// EmbyLibraryMakeService need. 

namespace ManageComingSoon.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using ManageComingSoon.Model;
    using MediaBrowser.Common.Net;
    using MediaBrowser.Controller;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Persistence;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Logging;

    public abstract class EmbyLibrarySharedService
    {
        // -----------------------------------------------------------------------
        // Tag text — read from config on every use so a change on the
        // Configuration tab takes effect immediately without a server restart.
        // -----------------------------------------------------------------------
        protected string ActiveTagText
        {
            get
            {
                var cfg = ManageComingSoonPlugin.Instance?.Configuration;
                if (cfg == null || string.IsNullOrEmpty(cfg.ComingSoonTagText))
                    return "Coming Soon";
                return cfg.ComingSoonTagText;
            }
        }

        // -----------------------------------------------------------------------
        // Ingest-poll presets — shared between both pipelines.
        // Add-only presets (AddPipelineIngestPass*) live in EmbyLibraryAddService.
        // -----------------------------------------------------------------------

        // DB/index-only checks: empty bootstrap folder, orphan/absence confirmation.
        protected const int NoNewContentFirstWaitSeconds = 2;
        protected const int NoNewContentSecondWaitSeconds = 7;

        // Real previously-unseen video file: needs ffprobe + metadata pass headroom.
        protected const int NewContentFirstWaitSeconds = 20;
        protected const int NewContentSecondWaitSeconds = 60;

        // -----------------------------------------------------------------------
        // Dependencies
        // -----------------------------------------------------------------------
        protected readonly IServerApplicationHost appHost;
        protected readonly ILibraryManager libraryManager;
        protected readonly IItemRepository itemRepository;
        protected readonly IProviderManager providerManager;
        protected readonly IFileSystem fileSystem;
        protected readonly ILibraryMonitor libraryMonitor;
        protected readonly ILogger logger;

        protected EmbyLibrarySharedService(
            IServerApplicationHost appHost,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILibraryMonitor libraryMonitor,
            ILogger logger)
        {
            this.appHost = appHost;
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.libraryMonitor = libraryMonitor;
            this.logger = logger;
        }

        // -----------------------------------------------------------------------
        // Query Coming Soon items
        // -----------------------------------------------------------------------

        public BaseItem[] GetComingSoonItems()
        {
            var query = new InternalItemsQuery
            {
                Tags = new[] { ActiveTagText },
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
            };
            return this.libraryManager.GetItemList(query);
        }

        /// <summary>
        /// Locates the tagged Movie item inside a Coming Soon folder by querying for
        /// the actual video file path, NOT the folder path. FindByPath(folderPath)
        /// resolves to the containing Folder item (Type=Folder, Tags=[] always) which
        /// is the wrong item. This was the root cause discovered during Make Live
        /// testing: every test that mutated the Folder item left the real tagged
        /// Movie item completely untouched.
        ///
        /// Promoted from EmbyLibraryAddService/EmbyLibraryMakeService, where this
        /// was previously duplicated verbatim in both classes — both the Add pipeline
        /// (confirming a freshly-ingested stub was tagged) and the Make Live pipeline
        /// (finding the source item before a move) need the identical lookup.
        /// </summary>
        protected BaseItem FindMovieInFolder(string folderPath)
        {
            var query = new InternalItemsQuery
            {
                Tags = new[] { ActiveTagText },
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
            };
            var candidates = this.libraryManager.GetItemList(query);

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate.Path)) continue;
                string candidateFolder = System.IO.Path.GetDirectoryName(candidate.Path);
                if (string.Equals(candidateFolder, folderPath, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        // -----------------------------------------------------------------------
        // Library lookup helpers
        // -----------------------------------------------------------------------

        protected CollectionFolder FindCollectionFolder(string libraryPath)
        {
            CollectionFolder collectionFolder = null;

            var virtualFolders = this.libraryManager.GetVirtualFolders();

            foreach (var vf in virtualFolders)
            {
                if (vf.Locations == null) continue;
                foreach (var loc in vf.Locations)
                {
                    char[] separators = new char[]
                    {
                        System.IO.Path.DirectorySeparatorChar,
                        System.IO.Path.AltDirectorySeparatorChar
                    };
                    string normLoc = loc.TrimEnd(separators);
                    string normLibPath = libraryPath.TrimEnd(separators);

                    if (string.Equals(normLoc, normLibPath, StringComparison.OrdinalIgnoreCase) ||
                        normLibPath.StartsWith(normLoc, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(vf.ItemId))
                        {
                            Guid itemGuid;
                            if (Guid.TryParse(vf.ItemId, out itemGuid))
                                collectionFolder = this.libraryManager.GetItemById(itemGuid) as CollectionFolder;

                            if (collectionFolder == null)
                            {
                                long longId;
                                if (long.TryParse(vf.ItemId, out longId))
                                    collectionFolder = this.libraryManager.GetItemById(longId) as CollectionFolder;
                            }
                        }
                        break;
                    }
                }
                if (collectionFolder != null) break;
            }

            return collectionFolder;
        }

        // -----------------------------------------------------------------------
        // HTTP helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Resolves the local Emby API URL and HttpClient in one step.
        /// Returns null and logs a warning if either precondition fails (no API key,
        /// or GetLocalApiUrl returns empty). Both derived pipelines use this before
        /// their first CallRefreshEndpointAsync call.
        /// </summary>
        protected async Task<(IHttpClient Client, string BaseUrl)?> ResolveHttpAsync(
            string apiKey, string logPrefix, CancellationToken token)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                this.logger.Warn("[{0}] No Emby API key configured — cannot call the refresh endpoint.", logPrefix);
                return null;
            }

            string baseUrl = await this.appHost.GetLocalApiUrl(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(baseUrl))
            {
                this.logger.Warn("[{0}] GetLocalApiUrl returned empty.", logPrefix);
                return null;
            }

            var httpClient = this.appHost.Resolve<IHttpClient>();
            return (httpClient, baseUrl);
        }

        /// <summary>
        /// Confirmed working: the in-process BaseItem.RefreshMetadata(...) call
        /// (tried via many variants) NEVER produced the native "ProviderManager:
        /// RefreshItem Start" log line that Emby's own UI Refresh button produces.
        /// The same operation via POST /Items/{id}/Refresh DOES produce that line
        /// immediately. This helper performs that REST call and logs the POST
        /// method, the short-form InternalId being targeted, and a clear role label
        /// so the log is unambiguous about what was refreshed.
        /// </summary>
        protected async Task CallRefreshEndpointAsync(
            IHttpClient httpClient, string baseUrl, long internalId, string roleLabel,
            string apiKey, MetadataRefreshMode mode, string stageLabel,
            CancellationToken token, string logContext = null)
        {
            string ctxTag = string.IsNullOrEmpty(logContext) ? string.Empty : logContext;
            string url = string.Format(
                "{0}/Items/{1}/Refresh?Recursive=true&MetadataRefreshMode={2}&ImageRefreshMode={2}" +
                "&ReplaceAllMetadata=false&ReplaceAllImages=false&api_key={3}",
                baseUrl.TrimEnd('/'),
                internalId,
                mode,
                Uri.EscapeDataString(apiKey));

            this.logger.Info(
                "[{0}]{1} POST /Items/{2}/Refresh — target=({3}) InternalId={2} Mode={4}",
                stageLabel, ctxTag, internalId, roleLabel, mode);

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = token,
                TimeoutMs = 15000,
                LogErrors = true,
            };

            using (var response = await httpClient.Post(options).ConfigureAwait(false))
            {
                this.logger.Info(
                    "[{0}]{1} POST /Items/{2}/Refresh ({3}) returned (2xx implied)",
                    stageLabel, ctxTag, internalId, roleLabel);
            }
        }

        /// <summary>
        /// Best-effort follow-up full-metadata refresh, fired fully detached after a
        /// pipeline has already declared success off a lighter-weight (e.g.
        /// ValidationOnly) result. Any exception (timeout, provider offline) is
        /// logged and swallowed — by design, a failure here can never turn an
        /// already-successful pipeline run into a reported failure.
        ///
        /// Promoted from EmbyLibraryAddService, generalized: the stage label, role
        /// label, and "what already succeeded" clause are now caller-supplied
        /// rather than hardcoded to Add-pipeline wording, so EmbyLibraryMakeService
        /// (or any future pipeline) can reuse this without a misleading log line
        /// that mentions an "add" that never happened.
        /// </summary>
        protected void FireAndForgetFullRefresh(
            IHttpClient httpClient, string baseUrl, long libraryInternalId,
            string apiKey, string itemLabel, string stageLabel, string roleLabel,
            string alreadySucceededContext)
        {
            Task.Run(async () =>
            {
                try
                {
                    await CallRefreshEndpointAsync(
                        httpClient, baseUrl, libraryInternalId,
                        roleLabel, apiKey, MetadataRefreshMode.FullRefresh,
                        stageLabel, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.Warn(
                        "[{0}] Best-effort full refresh failed for '{1}' " +
                        "(harmless — {2}; this only affects when/whether full " +
                        "metadata and images show up): {3}",
                        stageLabel, itemLabel, alreadySucceededContext, ex.Message);
                }
            });
        }

        // -----------------------------------------------------------------------
        // Ingest polling
        // -----------------------------------------------------------------------

        /// <summary>
        /// 2-pass overload — backwards-compatible convenience wrapper.
        /// Use NoNewContent* for DB/index-only probes; NewContent* when Emby
        /// has to ffprobe a real previously-unseen video file.
        /// </summary>
        protected Task<bool> WaitForConditionAsync(
            Func<bool> probe, CancellationToken token, string stageLabel,
            int firstWaitSeconds, int secondWaitSeconds,
            Action<string> onPass = null, string logContext = null)
        {
            return WaitForConditionAsync(
                probe, token, stageLabel,
                new[] { firstWaitSeconds, secondWaitSeconds },
                onPass, logContext);
        }

        /// <summary>
        /// N-pass version. Waits passSeconds[i], probes; advances until met or
        /// all passes exhausted. onPass fires before each wait with a human-readable
        /// "Nth Pass (Xs)..." message suitable for surfacing in UI progress text.
        /// </summary>
        protected async Task<bool> WaitForConditionAsync(
            Func<bool> probe, CancellationToken token, string stageLabel,
            int[] passSeconds, Action<string> onPass = null, string logContext = null)
        {
            string ctxTag = string.IsNullOrEmpty(logContext) ? string.Empty : logContext;
            string timings = string.Join("/", passSeconds.Select(s => s + "s"));
            int elapsed = 0;

            for (int i = 0; i < passSeconds.Length; i++)
            {
                int waitSeconds = passSeconds[i];
                string ordinal = OrdinalPass(i + 1);

                if (onPass != null)
                    onPass(string.Format(
                        "Checking for Movie Ingestion — {0} Pass ({1}s)...", ordinal, waitSeconds));

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), token).ConfigureAwait(false);
                elapsed += waitSeconds;

                if (probe())
                {
                    this.logger.Info(
                        "[{0}]{1} Condition met at ~{2}s (timings={3}, {4} pass).",
                        stageLabel, ctxTag, elapsed, timings, ordinal);
                    return true;
                }

                bool isLastPass = i == passSeconds.Length - 1;
                if (!isLastPass)
                {
                    this.logger.Info(
                        "[{0}]{1} Not met after {2} pass (timings={3}, {4}s elapsed)" +
                        " — starting {5} pass (+{6}s).",
                        stageLabel, ctxTag, ordinal, timings, elapsed,
                        OrdinalPass(i + 2), passSeconds[i + 1]);
                }
            }

            this.logger.Warn(
                "[{0}]{1} Condition NOT met after ~{2}s (timings={3}) — treating as failed.",
                stageLabel, ctxTag, elapsed, timings);
            return false;
        }

        protected static string OrdinalPass(int n)
        {
            switch (n)
            {
                case 1: return "1st";
                case 2: return "2nd";
                case 3: return "3rd";
                default: return n + "th";
            }
        }

        // -----------------------------------------------------------------------
        // Logging helper
        // -----------------------------------------------------------------------

        /// <summary>
        /// Logs full item state using the SHORT-FORM InternalId (e.g. 369781), not
        /// the long Guid Id — matching what appears in Emby's own native log lines
        /// (e.g. "ProviderManager: RefreshItem Start: 369781 CollectionFolder ...")
        /// so our log can be visually cross-referenced against Emby's own log.
        /// roleLabel should be one of: "Movie", "Movie Folder", "Movie Folder Parent",
        /// "Library" — making it unambiguous which tier of the hierarchy this is.
        /// </summary>
        protected void LogItemState(string label, string roleLabel, BaseItem item)
        {
            if (item == null)
            {
                this.logger.Info("[TEST] {0}: ({1}) item is null", label, roleLabel);
                return;
            }

            this.logger.Info(
                "[TEST] {0}: ({1}) InternalId={2} Type={3} Name='{4}' Path='{5}' Tags=[{6}]",
                label,
                roleLabel,
                item.InternalId,
                item.GetType().Name,
                item.Name,
                item.Path ?? "(null)",
                item.Tags != null ? string.Join(",", item.Tags) : "(null)");
        }

        // -----------------------------------------------------------------------
        // Name helpers — also called directly by page-view code via the
        // concrete derived types, so both are internal static.
        // -----------------------------------------------------------------------

        /// <summary>
        /// Builds the sanitized "Title (Year)" folder name used everywhere a
        /// Coming Soon folder/stub is created or looked up. This is the single
        /// choke point that guarantees a missing/unknown year (0 or negative)
        /// can never reach disk as a literal "(0)" — it falls back to the
        /// current calendar year instead. Upstream code should still avoid
        /// producing a 0 year in the first place (see AddMovieTracker.
        /// SetManualConfident and TmdbService.IsConfidentMatch), but this is
        /// the last line of defense.
        /// </summary>
        internal static string BuildComingSoonFolderName(string title, int releaseYear)
        {
            int safeYear = releaseYear > 0 ? releaseYear : DateTime.UtcNow.Year;
            return SanitizeName(string.Format("{0} ({1})", title, safeYear));
        }

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            var invalid = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars());
            var sb = new System.Text.StringBuilder(name.Length);
            bool lastWasSpace = false;

            foreach (var c in name)
            {
                if (invalid.Contains(c))
                {
                    // Drop the character entirely rather than substituting an
                    // underscore — "Dune: Part Two" should become
                    // "Dune Part Two", not "Dune_ Part Two". Collapse the gap
                    // it leaves behind into a single space (handled below).
                    if (sb.Length > 0 && !lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                    continue;
                }

                if (c == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                sb.Append(c);
            }

            return sb.ToString().Trim();
        }
    }
}