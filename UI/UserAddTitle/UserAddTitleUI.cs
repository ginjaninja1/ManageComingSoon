namespace ManageComingSoon.UI.UserAddTitle
{
    using System.Collections.Generic;
    using System.ComponentModel;
    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Common;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;
    using MediaBrowser.Model.Attributes;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.LocalizationAttributes;

    public sealed class UserAddTitleUI : EditableOptionsBase
    {
        public override string EditorTitle => "Add Coming Soon Titles";

        public override string EditorDescription =>
            "Enter a title and its year.\n" +
            "Click [Identify via TMDB] (TheMovieDatabase)  to confirm name (and year) against TMDB.\n" +
            "A TMDB match is useful to confirm you have the right title and year, and to get a trailer if available.\n" +
            "Only use [Add Manual] if the title cannot be found on TMDB (possible with recently announced titles).\n" +
            "You may need to [select] from multiple near matches on TMDB. Actors and tag line can help confirm correct match.\n" +
            "Click [Add to Library].\n" +
            "A placeholder will be added to the 'Coming Soon' library with a trailer where available.";

        public SpacerItem Spacer1 { get; set; } = new SpacerItem();

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> MediaTypeOptions { get; set; } =
            new[] { new EditorSelectOption("Movie", "Movie"), new EditorSelectOption("TvShow", "TV Show") };

        [DisplayName("Content Type, choose [Movie] or [TV Show]")]
        [SelectItemsSource(nameof(MediaTypeOptions))]
        public string MediaType { get; set; } = "Movie";

        [DisplayName("Title")]
        [Description("or eg. Title1;Year|Title2|Title3;Year...to add multiple titles quickly")]
        public string TitleName { get; set; } = string.Empty;

        [DisplayName("Year (optional)")]
        public string ReleaseYear { get; set; } = string.Empty;

        public ButtonItem AddViaTmdbButton { get; set; } =
            new ButtonItem("Identify via TMDB")
            {
                Icon = IconNames.search,
                Data1 = "AddViaTmdb",
                CommandId = "AddViaTmdb"
            };

        public ButtonItem AddManualButton { get; set; } =
            new ButtonItem("Manual")
            {
                StandardIcon = StandardIcons.Add,
                Data1 = "AddManual",
                CommandId = "AddManual"
            };

        public GenericItemList ActiveList { get; set; } = new GenericItemList();
        public GenericItemList CompletedList { get; set; } = new GenericItemList();

        public ButtonItem RefreshStatusButton { get; set; } =
            new ButtonItem("Refresh Status")
            {
                StandardIcon = StandardIcons.Refresh,
                Data1 = "RefreshStatus",
                CommandId = "RefreshStatus"
            };

        public StatusItem OverallStatus { get; set; } =
            new StatusItem("Status", string.Empty, ItemStatus.Unavailable);
    }
}
