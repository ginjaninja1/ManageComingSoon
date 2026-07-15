namespace ManageComingSoon.Services
{
    using ManageComingSoon.Model;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Controller.Channels;
    using MediaBrowser.Controller.Drawing;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Drawing;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Logging;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    /// <summary>
    /// Owns all Channel BaseItem identity bookkeeping — applying the
    /// identity tag, reapplying the channel image, and finding/deleting
    /// orphaned Channel DB rows left behind by a rename. Extracted out of
    /// RadarrChannelSyncTask so it can be triggered from more than one
    /// place (the scheduled task, and immediately on relevant config
    /// saves) rather than only healing on the task's own schedule.
    /// </summary>
    public class RadarrChannelIdentityReconciler
    {
        private const string ThumbResourceName = "ManageComingSoon.thumb.png";
        private const string ThumbCacheFileName = "radarr-channel-thumb.png";

        private readonly IChannelManager channelManager;
        private readonly ILibraryManager libraryManager;
        private readonly IImageProcessor imageProcessor;
        private readonly IApplicationPaths appPaths;
        private readonly ILogger logger;

        public RadarrChannelIdentityReconciler(
            IChannelManager channelManager,
            ILibraryManager libraryManager,
            IImageProcessor imageProcessor,
            IApplicationPaths appPaths,
            ILogger logger)
        {
            this.channelManager = channelManager;
            this.libraryManager = libraryManager;
            this.imageProcessor = imageProcessor;
            this.appPaths = appPaths;
            this.logger = logger;
        }

        /// <summary>
        /// Full reconciliation pass: tags/images the current channel entry
        /// (matched by config.RadarrChannelName) and deletes any other
        /// Channel item carrying config.RadarrChannelIdentityTag that isn't
        /// the current one.
        /// </summary>
        public void Reconcile(PluginConfiguration config)
        {
            ReconcileInternal(config, config.RadarrChannelIdentityTag, applyToCurrent: true);
        }

        /// <summary>
        /// Cleanup-only pass, keyed off an explicit tag value rather than
        /// config.RadarrChannelIdentityTag. Call this BEFORE writing a new
        /// identity tag into config, passing the OLD tag value, so any
        /// orphans still carrying the old tag get a chance to be found and
        /// deleted. Without this, changing the identity tag makes any
        /// orphan under the old tag permanently invisible to the normal
        /// Reconcile() query, since nothing ever queries for the old value
        /// again. Does not tag or image anything — that happens on the
        /// subsequent normal Reconcile() call with the new tag.
        /// </summary>
        public void CleanupOrphansForTag(PluginConfiguration config, string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return;
            }

            ReconcileInternal(config, tag, applyToCurrent: false);
        }

        private void ReconcileInternal(PluginConfiguration config, string tag, bool applyToCurrent)
        {
            List<BaseItem> taggedChannelItems;

            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Channel" },
                    Tags = new[] { tag }
                };

                taggedChannelItems = libraryManager.GetItemsResult(query).Items.ToList();
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to query tagged Channel items for identity reconciliation (tag='{0}')", ex, tag);
                return;
            }

            var current = taggedChannelItems.FirstOrDefault(i =>
                string.Equals(i.Name, config.RadarrChannelName, StringComparison.OrdinalIgnoreCase));

            if (current == null && applyToCurrent)
            {
                try
                {
                    var allChannelsQuery = new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Channel" },
                        Name = config.RadarrChannelName
                    };

                    current = libraryManager.GetItemsResult(allChannelsQuery).Items.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    logger.ErrorException("ManageComingSoon: Failed to query Channel item by name for identity reconciliation", ex);
                }
            }

            var orphans = taggedChannelItems.Where(i => i != current).ToList();

            if (applyToCurrent)
            {
                if (current != null)
                {
                    ApplyIdentityTag(current, tag, config);
                    ReapplyChannelImage(current);
                }
                else
                {
                    logger.Warn(
                        "ManageComingSoon: No Channel item found matching current name '{0}' — tag/image not applied this run. Expected on the very first sync before Emby has persisted the channel.",
                        config.RadarrChannelName);
                }
            }

            foreach (var orphan in orphans)
            {
                try
                {
                    channelManager.DeleteItem(orphan).GetAwaiter().GetResult();

                    logger.Info(
                        "ManageComingSoon: Orphaned Radarr channel entry deleted — Name='{0}', InternalId={1}, Tag='{2}'.",
                        orphan.Name, orphan.InternalId, tag);
                }
                catch (Exception ex)
                {
                    logger.ErrorException(
                        "ManageComingSoon: Failed to delete orphaned Radarr channel entry — Name='{0}', InternalId={1}.",
                        ex, orphan.Name, orphan.InternalId);
                }
            }
        }

        private void ApplyIdentityTag(BaseItem item, string identityTag, PluginConfiguration config)
        {
            if (string.IsNullOrEmpty(identityTag))
            {
                return;
            }

            var tags = item.Tags != null ? new List<string>(item.Tags) : new List<string>();
            bool changed = false;

            var lastApplied = config.RadarrChannelIdentityTagLastApplied;
            if (!string.IsNullOrEmpty(lastApplied) &&
                !string.Equals(lastApplied, identityTag, StringComparison.OrdinalIgnoreCase))
            {
                if (tags.RemoveAll(t => string.Equals(t, lastApplied, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    changed = true;
                    logger.Info("ManageComingSoon: Removed stale identity tag '{0}' from '{1}'.", lastApplied, item.Name);
                }
            }

            if (tags.FindIndex(t => string.Equals(t, identityTag, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                tags.Add(identityTag);
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            item.Tags = tags.ToArray();

            if (!item.LockedFields.Contains(MetadataFields.Tags))
            {
                item.LockedFields = item.LockedFields.Concat(new[] { MetadataFields.Tags }).ToArray();
            }

            libraryManager.UpdateItem(item, item.GetParent(), ItemUpdateType.MetadataEdit, null);

            config.RadarrChannelIdentityTagLastApplied = identityTag;
            ManageComingSoonPlugin.Instance.UpdateConfiguration(config);

            logger.Info("ManageComingSoon: Identity tag '{0}' applied to '{1}'.", identityTag, item.Name);
        }

        private void ReapplyChannelImage(BaseItem item)
        {
            try
            {
                if (item.HasImage(ImageType.Primary))
                {
                    logger.Info("ManageComingSoon: '{0}' already has a primary image. Skipping reapply.", item.Name);
                    return;
                }

                var imagePath = ResolveChannelImagePath();

                if (string.IsNullOrEmpty(imagePath))
                {
                    logger.Warn("ManageComingSoon: Could not resolve channel image file — image not reapplied.");
                    return;
                }

                var imageSize = imageProcessor.GetImageSize(imagePath);

                item.SetImage(new ItemImageInfo
                {
                    Path = imagePath,
                    Type = ImageType.Primary,
                    DateModified = DateTimeOffset.UtcNow,
                    Width = (int)imageSize.Width,
                    Height = (int)imageSize.Height
                }, 0);

                libraryManager.UpdateImages(item);

                logger.Info(
                    "ManageComingSoon: Reapplied channel image to '{0}' (InternalId={1}) from {2}.",
                    item.Name, item.InternalId, imagePath);
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to reapply channel image to '{0}'", ex, item.Name);
            }
        }

        private string ResolveChannelImagePath()
        {
            var path = Path.Combine(appPaths.DataPath, "manage-coming-soon", ThumbCacheFileName);

            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var asm = typeof(ManageComingSoonPlugin).Assembly;
                using (var resourceStream = asm.GetManifestResourceStream(ThumbResourceName))
                {
                    if (resourceStream == null)
                    {
                        logger.Warn("ManageComingSoon: Embedded thumb resource '{0}' not found.", ThumbResourceName);
                        return string.Empty;
                    }

                    using (var fileStream = File.Create(path))
                    {
                        resourceStream.CopyTo(fileStream);
                    }
                }

                logger.Info("ManageComingSoon: Extracted channel thumb image to {0}.", path);
                return path;
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to extract channel thumb image", ex);
                return string.Empty;
            }
        }
    }
}