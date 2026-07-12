using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ManageComingSoon.Services
{
    public class MissingMovieCache
    {
        private IReadOnlyList<ChannelItemInfo> items = Array.Empty<ChannelItemInfo>();

        public IReadOnlyList<ChannelItemInfo> Items
        {
            get { return items; }
        }

        public void Replace(IEnumerable<ChannelItemInfo> newItems)
        {
            items = newItems.ToList().AsReadOnly();
        }

        public void Clear()
        {
            items = Array.Empty<ChannelItemInfo>();
        }

        public int Count => items.Count;
    }
}