using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ManageComingSoon
{
    public static class RequestEngine
    {
        public static void ProcessMovieRequest(string rawInput, long userId, ILogger logger)
        {
            string movieName = "Unknown Title";
            string year = "Unknown Year";

            if (!string.IsNullOrEmpty(rawInput) && rawInput.Contains("|"))
            {
                var parts = rawInput.Split('|');
                movieName = parts[0].Trim();
                if (parts.Length > 1) year = parts[1].Trim();
            }
            else if (!string.IsNullOrEmpty(rawInput))
            {
                movieName = rawInput.Trim();
            }

            logger.Info($"[ManageComingSoon] Request verified from User {userId}: {movieName} ({year})");
        }
    }

    public class RequestChannel : IChannel, IHasChangeEvent
    {
        private readonly ILogger logger;
        private const string InitialPlaceholder = "Enter Moviename|Year";

        // Static internal session state buffer to track what the user is typing
        private static string _typingBuffer = string.Empty;

        // This event tells Emby to instantly clear its local memory tables for this layout level
        public event EventHandler ContentChanged;

        public RequestChannel(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Name);
        }

        public string Name => "Request";
        public string Description => "Select characters below to construct your movie request text string.";
        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        {
            return Task.FromResult<DynamicImageResponse>(null);
        }

        public IEnumerable<ImageType> GetSupportedChannelImages()
        {
            return new List<ImageType>();
        }

        private void RefreshChannelLayout()
        {
            logger.Info("[ManageComingSoon] Forcing Emby database to drop channel UI caches.");
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        {
            string incomingFolderId = query.FolderId;
            logger.Info($"[ManageComingSoon] ---> GetChannelItems invoked. Processing Path: \"{incomingFolderId}\"");

            var result = new ChannelItemResult { Items = new List<ChannelItemInfo>() };

            // --- 1. STATE MACHINE & INTERCEPT PATTERNS ---
            if (!string.IsNullOrEmpty(incomingFolderId))
            {
                if (incomingFolderId == "SUCCESS_RESET_NODE")
                {
                    _typingBuffer = string.Empty;
                    RefreshChannelLayout();
                }
                else if (incomingFolderId == "SUBMIT_ACTION")
                {
                    logger.Info($"[ManageComingSoon] Executing submission engine logic for string: \"{_typingBuffer}\"");
                    RequestEngine.ProcessMovieRequest(_typingBuffer, query.UserId, this.logger);

                    // Render terminal success screen notification element
                    result.Items.Add(new ChannelItemInfo
                    {
                        Name = $"🎉 Success! Request Submitted: {_typingBuffer} (Select here to reset)",
                        Id = "SUCCESS_RESET_NODE",
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container // Forces Emby to show this element
                    });

                    RefreshChannelLayout();
                    return result;
                }
                else if (incomingFolderId == "RESET_ACTION")
                {
                    _typingBuffer = string.Empty;
                    RefreshChannelLayout();
                }
                else if (incomingFolderId == "DELETE_ACTION")
                {
                    if (_typingBuffer.Length > 0)
                    {
                        _typingBuffer = _typingBuffer.Substring(0, _typingBuffer.Length - 1);
                    }
                    RefreshChannelLayout();
                }
                else if (incomingFolderId.StartsWith("KEY_"))
                {
                    string pressedKey = incomingFolderId.Replace("KEY_", "");
                    _typingBuffer += pressedKey;

                    logger.Info($"[ManageComingSoon] Dynamic Input Registered. Updated buffer string: \"{_typingBuffer}\"");
                    RefreshChannelLayout();
                }
            }

            // --- 2. RENDER THE CURRENT TEXT PLACEHOLDER (Will show on root layout load!) ---
            string visualLabel = string.IsNullOrEmpty(_typingBuffer) ? InitialPlaceholder : _typingBuffer;

            result.Items.Add(new ChannelItemInfo
            {
                Name = $"📝 Current Input: [ {visualLabel} ]",
                Id = "DISPLAY_PLACEHOLDER_NODE",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container // Mandatory assignment to force row visibility
            });

            result.Items.Add(new ChannelItemInfo
            {
                Name = "Input Movie",
                Id = "DISPLAY_PLACEHOLDER_NODE2",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container
            });

            // --- 3. RENDER THE ACTION CONTROLS STRIP ---
            if (!string.IsNullOrEmpty(_typingBuffer))
            {
                result.Items.Add(new ChannelItemInfo
                {
                    Name = "➡️ SUBMIT REQUEST NOW",
                    Id = "SUBMIT_ACTION",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container
                });

                result.Items.Add(new ChannelItemInfo
                {
                    Name = "⬅️ [Delete / Backspace]",
                    Id = "DELETE_ACTION",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container
                });

                result.Items.Add(new ChannelItemInfo
                {
                    Name = "❌ Clear & Reset Keyboard",
                    Id = "RESET_ACTION",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container
                });
            }

            // --- 4. GENERATE VIRTUAL KEYBOARD GRID TILES ---
            string alphanumericKeys = "BCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 |";

            foreach (char key in alphanumericKeys)
            {
                string humanFriendlyName = key switch
                {
                    ' ' => "[Spacebar]",
                    '|' => "[ | Separator]",
                    _ => key.ToString()
                };

                result.Items.Add(new ChannelItemInfo
                {
                    Name = humanFriendlyName,
                    Id = $"KEY_{key}",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container // Ensures key button items populate grid layouts
                });
            }

            logger.Info($"[ManageComingSoon] <--- Returning {result.Items.Count} verified UI matrix items to Emby Core.");
            return result;
        }
    }
}
