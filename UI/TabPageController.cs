namespace ManageComingSoon.UI
{
    using System;
    using System.Threading.Tasks;
    using ManageComingSoon.UI.Security;
    using ManageComingSoon.UIBaseClasses;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;

    /// <summary>Simple tab page controller that uses a factory function to create the view.</summary>
    internal class TabPageController : ControllerBase, IPluginPageSecurity
    {
        private readonly PluginInfo pluginInfo;
        private readonly Func<PluginInfo, IPluginUIView> factoryFunc;
        private readonly bool adminOnly;

        public TabPageController(
            PluginInfo pluginInfo,
            string name,
            string displayName,
            Func<PluginInfo, IPluginUIView> factoryFunc,
            bool adminOnly = false)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.factoryFunc = factoryFunc;
            this.adminOnly = adminOnly;
            PageInfo = new PluginPageInfo { Name = name, DisplayName = displayName };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            var view = this.factoryFunc(this.pluginInfo);
            return Task.FromResult(view);
        }

        // IPluginPageSecurity - PluginPageControllerHost calls this on every
        // view transition, but ONLY because this class now implements the
        // interface. Tab controllers are registered independently of their
        // parent MainPageController (confirmed via ILSpy), so this must be
        // applied here directly - gating only the parent leaves the tab
        // reachable by anyone who knows/guesses its pageId.
        public Task CheckIsUserAuthorised(UserDto user, IPluginUIView requestedView)
        {
            return this.adminOnly
                ? AdminOnlyPageSecurity.CheckIsAdmin(user, requestedView)
                : Task.CompletedTask;
        }
    }
}