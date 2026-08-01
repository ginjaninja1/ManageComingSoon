namespace ManageComingSoon.UI.UserAddMovie
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

    public sealed class UserAddMovieUI : EditableOptionsBase
    {
        public override string EditorTitle => "Add Coming Soon Titles";

        public override string EditorDescription =>
            "Enter one movie and its year or \n[Advanced Usage] Multiple movies in one go via a ;,| split eg. Dune Part Two;2024|Gladiator II\n" +
            "[Recomended]Click 'Identify via TMDB match' (TheMovieDatabase)  to confirm name [and year] against TMDB.\n" +
            "You may need to select from multiple near matches.\n" +
            "Click 'Add to Library'.\n" +
            "A placeholder will be added to the 'coming soon' library [optionally with its trailer], pending addition to Emby when the bluray is released.";

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> MediaTypeOptions { get; set; } =
            new[] { new EditorSelectOption("Movie", "Movie"), new EditorSelectOption("TvShow", "TV Show") };

        [DisplayName("Content Type")]
        [SelectItemsSource(nameof(MediaTypeOptions))]
        public string MediaType { get; set; } = "Movie";

        [DisplayName("Title")]
        [Description("or Movie1;Year|Movie2|Movie3;Year...")]
        public string MovieName { get; set; } = string.Empty;

        [DisplayName("Year (optional)")]
        public string ReleaseYear { get; set; } = string.Empty;

        public ButtonItem AddViaTmdbButton { get; set; } =
            new ButtonItem("Identify via TMDB Match")
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
