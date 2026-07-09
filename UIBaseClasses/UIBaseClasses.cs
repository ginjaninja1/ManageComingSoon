// ManageComingSoon - UI Base Classes
// Mirrors the EmbyPluginUiDemo SDK sample exactly, adapted for netstandard2.0.

namespace ManageComingSoon.UIBaseClasses
{
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;

    public abstract class ControllerBase : IPluginUIPageController
    {
        protected ControllerBase(string pluginId) { PluginId = pluginId; }

        public abstract PluginPageInfo PageInfo { get; }
        public string PluginId { get; }

        public virtual Task Initialize(CancellationToken token) => Task.CompletedTask;
        public abstract Task<IPluginUIView> CreateDefaultPageView();
    }
}

namespace ManageComingSoon.UIBaseClasses.Views
{
    using System;
    using System.Threading.Tasks;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Events;
    using MediaBrowser.Model.GenericEdit;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Plugins.UI.Views.Enums;
    // Explicit alias prevents ambiguity with System.ComponentModel.IEditableObject
    using IEditableObject = MediaBrowser.Model.GenericEdit.IEditableObject;

    public abstract class PluginViewBase : IPluginUIView, IPluginViewWithOptions
    {
        protected PluginViewBase(string pluginId) { PluginId = pluginId; }

        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;

        public virtual string Caption    => ContentData != null ? ContentData.EditorTitle       : string.Empty;
        public virtual string SubCaption => ContentData != null ? ContentData.EditorDescription : string.Empty;
        public string PluginId { get; }

        public IEditableObject ContentData
        {
            get => ContentDataCore;
            set => ContentDataCore = value;
        }

        public UserDto User { get; set; }
        public string RedirectViewUrl { get; set; }
        public Uri HelpUrl { get; set; }
        public QueryCloseAction QueryCloseAction { get; set; }
        public WizardHidingBehavior WizardHidingBehavior { get; set; }
        public CompactViewAppearance CompactViewAppearance { get; set; }
        public DialogSize DialogSize { get; set; }
        public string OKButtonCaption { get; set; }
        public DialogAction PrimaryDialogAction { get; set; }

        protected IEditableObject ContentDataCore { get; set; }

        public virtual bool IsCommandAllowed(string commandKey) => true;

        public virtual Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
            => Task.FromResult<IPluginUIView>(null);

        public virtual Task Cancel() => Task.CompletedTask;

        public virtual void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) { }

        protected void RaiseUIViewInfoChanged()
            => UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));

        public virtual PluginViewOptions ViewOptions => new PluginViewOptions
        {
            HelpUrl              = HelpUrl,
            CompactViewAppearance = CompactViewAppearance,
            QueryCloseAction      = QueryCloseAction,
            DialogSize            = DialogSize,
            OKButtonCaption       = OKButtonCaption,
            PrimaryDialogAction   = PrimaryDialogAction,
            WizardHidingBehavior  = WizardHidingBehavior,
        };
    }

    public abstract class PluginPageView : PluginViewBase, IPluginPageView
    {
        protected PluginPageView(string pluginId) : base(pluginId) { }

        public bool ShowSave  { get; set; } = true;
        public bool ShowBack  { get; set; } = false;
        public bool AllowSave { get; set; } = true;
        public bool AllowBack { get; set; } = true;

        public virtual Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
            => Task.FromResult<IPluginUIView>(this);
    }
}
