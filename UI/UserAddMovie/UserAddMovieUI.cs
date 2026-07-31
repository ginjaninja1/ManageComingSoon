namespace ManageComingSoon.UI.UserAddMovie
{
    using System.ComponentModel;
    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;

    public sealed class UserAddMovieUI : EditableOptionsBase
    {
        public override string EditorTitle => "Add Coming Soon Movies";

        public override string EditorDescription =>
            "Enter one movie, or several separated by |. Add ;Year after a title when useful, " +
            "for example Dune Part Two;2024|Gladiator II. TMDB searches and normal page actions " +
            "update immediately. Server additions normally finish quickly, but background completion " +
            "cannot be pushed to ordinary-user pages; use Refresh Status when a submitted item still shows as pending.";

        [DisplayName("Movie name")]
        [Description("or Movie1;Year|Movie2|Movie3;Year...")]
        public string MovieName { get; set; } = string.Empty;

        [DisplayName("Year (optional)")]
        public string ReleaseYear { get; set; } = string.Empty;

        public ButtonItem AddViaTmdbButton { get; set; } =
            new ButtonItem("Add via TMDB Match")
            {
                Icon = IconNames.search,
                Data1 = "AddViaTmdb",
                CommandId = "AddViaTmdb"
            };

        public ButtonItem AddManualButton { get; set; } =
            new ButtonItem("Add Manual")
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