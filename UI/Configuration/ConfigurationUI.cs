namespace ManageComingSoon.UI.Configuration
{
    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Common;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using MediaBrowser.Model.Attributes;
    using MediaBrowser.Model.LocalizationAttributes;
    using System.Collections.Generic;
    using System.ComponentModel;

    public class ConfigurationUI : EditableOptionsBase
    {
        public override string EditorTitle => "Configuration";
        public override string EditorDescription =>
            "Settings that rarely change. Changes are saved automatically.";

        // ---- TMDB ---------------------------------------------------------------
        public CaptionItem CaptionTmdb { get; set; } = new CaptionItem("API Keys");

        [DisplayName("TMDB API Key")]
        [Description("Your personal API key from https://www.themoviedb.org/settings/api")]
        [AutoPostBack("ConfigurationChanged", nameof(TmdbApiKey))]
        public string TmdbApiKey { get; set; } = string.Empty;

        //[DisplayName("Emby API Key")]
        [Description("Create one in Dashboard > Advanced > API Keys. Used for the refresh command")]
        [AutoPostBack("ConfigurationChanged", nameof(EmbyApiKey))]
        public string EmbyApiKey { get; set; } = string.Empty;

        //public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        // ---- Coming Soon target -------------------------------------------------
        public CaptionItem CaptionComingSoonTarget { get; set; } =
            new CaptionItem("Coming Soon Settings");

        //public LabelItem ComingSoonTargetHelp { get; set; } =
        //new LabelItem("Select the single library path where new Coming Soon placeholders will be created.");

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> ComingSoonLibraryOptions { get; set; } =
            new List<EditorSelectOption>();

        [DisplayName("Coming Soon Target Library / Path")]
        //[Description("All movie library paths available on this server.")]
        [SelectItemsSource(nameof(ComingSoonLibraryOptions))]
        [AutoPostBack("ConfigurationChanged", nameof(ComingSoonTargetKey))]
        public string ComingSoonTargetKey { get; set; } = string.Empty;

        //public SpacerItem Spacer2 { get; set; } = new SpacerItem();

        [DisplayName("Choose your own placeholder video file")]
        //[Description(
        //"Path to a video file (mp4, mkv, avi, mov) to use instead of the plugin default. " +
        //"Leave blank to use the default. To change an existing selection, clear this field first.")]
        [AutoPostBack("ConfigurationChanged", nameof(ComingSoonStubVideoPath))]
        [EditFilePicker]
        public string ComingSoonStubVideoPath { get; set; } = string.Empty;

        // After ComingSoonStubVideoPath field, before StubVideoStatus
        //[DisplayName("Optional - choose a custom placeholder filename")]



        // Kept as a named property (not just an inline collection entry) so callers
        // can reference it directly instead of searching the list by PrimaryText.
        [Browsable(false)]
        public GenericListItem StubVideoStatusItem { get; set; } = new GenericListItem
        {
            PrimaryText = "Placeholder Video File",
            Status = ItemStatus.Unavailable,
            Icon = IconNames.video_library,
            Button1 = new ButtonItem("Clear Selection")
            {
                StandardIcon = StandardIcons.Remove,
                Data1 = "ClearStubVideo",
                CommandId = "ClearStubVideo",
            }
        };

        // The item must live inside a collection to be rendered on screen,
        // but StubVideoStatusItem above is the single source of truth for its content.
        public GenericItemList StubVideoStatusList => new GenericItemList { StubVideoStatusItem };

        public SpacerItem Spacer3 { get; set; } = new SpacerItem();

        //public CaptionItem CaptionTag { get; set; } =
        //new CaptionItem("Coming Soon Tag");

        [DisplayName("Tag for coming soon movies")]
        [AutoPostBack("ConfigurationChanged", nameof(ComingSoonTagText))]
        public string ComingSoonTagText { get; set; } = "Coming Soon";


        //public SpacerItem Spacer3 { get; set; } = new SpacerItem();

        // ---- Make Live target ---------------------------------------------------
        public CaptionItem CaptionMakeLiveTarget { get; set; } =
            new CaptionItem("Make Live Settings");



        [Browsable(false)]
        public IEnumerable<EditorSelectOption> MakeLiveLibraryOptions { get; set; } =
            new List<EditorSelectOption>();

        [DisplayName("Make Live Target Library / Path")]
        //[Description("Where to move the folder when making a movie live. Only applicable if 'Move' is enabled.")]
        [SelectItemsSource(nameof(MakeLiveLibraryOptions))]
        [EnabledCondition(nameof(MakeLiveMoveToNewLocation), SimpleCondition.IsTrue)]
        [AutoPostBack("ConfigurationChanged", nameof(MakeLiveTargetKey))]
        public string MakeLiveTargetKey { get; set; } = string.Empty;

        [DisplayName("Move movie to a new library/path when made live")]
        //[Description("Enable to move the Coming Soon folder to a different location on the Make Live action.")]
        [AutoPostBack("ConfigurationChanged", nameof(MakeLiveMoveToNewLocation))]
        public bool MakeLiveMoveToNewLocation { get; set; } = false;

        //public SpacerItem Spacer4 { get; set; } = new SpacerItem();

        [DisplayName("Delete placeholder video file when made live")]
        [Description("So eg AutoOrganise doesnt need to be given overwrite permission")]
        [AutoPostBack("ConfigurationChanged", nameof(MakeLiveDeleteStubFile))]
        public bool MakeLiveDeleteStubFile { get; set; } = false;

        [DisplayName("Max file size to be considered a placeholder (MB)")]
        [Description("Only files smaller than this can be deleted")]
        [Required]
        [AutoPostBack("ConfigurationChanged", nameof(MakeLiveDeleteStubFileMaxFileSize))]
        [EnabledCondition(nameof(MakeLiveDeleteStubFile), SimpleCondition.IsTrue)]
        public int MakeLiveDeleteStubFileMaxFileSize { get; set; } = 100;

        [DisplayName("Unlock Tags on Movie in Metadata Editor")]
        [Description("Revert the Tags lock instantiated by Add Movie")]
        [AutoPostBack("ConfigurationChanged", nameof(UnlockTags))]
        public bool UnlockTags { get; set; } = true;




        public SpacerItem Spacer5 { get; set; } = new SpacerItem();

        public GenericItemList ForumLink { get; set; } = new GenericItemList
        {
            new GenericListItem
            {
                PrimaryText = "Manage Coming Soon Plugin Page on Community Forum",
                SecondaryText = "Issues, Suggestions and Updates",
                Icon = IconNames.link,
                Status = ItemStatus.Succeeded,
                HyperLink = "https://emby.media/community/topic/148340-plugin-manage-coming-soon/",
                HyperLinkTargetExternal = true
            }
        };
    }
}