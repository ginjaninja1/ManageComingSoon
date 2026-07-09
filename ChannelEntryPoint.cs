using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ManageComingSoon.Services
{
    public class ComingSoonChannelEntryPoint : IServerEntryPoint
    {
        private readonly IChannelManager _channelManager;
        private readonly ILogger _logger;
        //private readonly ILogger _logger;
        // Hardcoded target name or pull this from your Plugin.Instance.Configuration
        private const string TargetChannelName = "ManageComingSoon";

        public ComingSoonChannelEntryPoint(IChannelManager channelManager, ILogManager logManager)
        {
            _channelManager = channelManager;
            this._logger = logManager.GetLogger("ComingSoonChannelEntryPoint");
            this._logger.Info("The Channel entry point constructor initialized.");
        }

        


        public void Run()
        {
            // Run asynchronously so you don't slow down the server startup sequence
            Task.Run(async () =>
            {
                try
                {
                    // 1. Fetch all registered channels on the system
                    var myChannel = _channelManager.GetChannel<RequestChannel>();

                    //var allChannels = _channelManager.GetChannels();

                    // 2. Isolate your specific channel by name (or unique internal ID)
                    //var myChannel = allChannels.FirstOrDefault(c =>
                        //c.Name.Equals(TargetChannelName, StringComparison.OrdinalIgnoreCase));

                    if (myChannel == null)
                    {
                        // Your channel isn't installed or active yet; abort safely
                        return;
                    }


                    // 3. Target achieved: Execute your refresh/button logic only for your channel
                    await RefreshMyChannelMembers(myChannel);
                }
                catch (Exception ex)
                {
                    // Replace 'logger' with your actual class logger instance variable if available
                    _logger.Error($"[ManageComingSoon] Error during channel refresh: {ex.Message}");
                }
            });

            //return Task.CompletedTask;
        }

        private async Task RefreshMyChannelMembers(RequestChannel channel)
        {
            // Force Emby to clear caches and rebuild the virtual keyboard matrix for your channel
            await _channelManager.RefreshChannelContent(
                channel,
                maxRefreshLevel: 1,
                restrictTopLevelFolderId: null,
                cancellationToken: CancellationToken.None
            );
        }

        public void Dispose()
        {
            // Cleanup code
        }
    }
}
