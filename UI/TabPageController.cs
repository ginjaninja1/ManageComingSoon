namespace ManageComingSoon.UI
{
    using System;
    using System.Threading.Tasks;
    using ManageComingSoon.UIBaseClasses;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>Simple tab page controller that uses a factory function to create the view.</summary>
    internal class TabPageController : ControllerBase
    {
        private readonly PluginInfo pluginInfo;
        private readonly Func<PluginInfo, IPluginUIView> factoryFunc;

        public TabPageController(
            PluginInfo pluginInfo,
            string name,
            string displayName,
            Func<PluginInfo, IPluginUIView> factoryFunc)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.factoryFunc = factoryFunc;
            PageInfo = new PluginPageInfo { Name = name, DisplayName = displayName };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            var view = this.factoryFunc(this.pluginInfo);
            return Task.FromResult(view);
        }
    }
}
