namespace ManageComingSoon.UI.UserAddMovie
{
    using System.ComponentModel;
    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;

    /// <summary>
    /// Deliberately small user-facing surface.
    /// All visible changes are returned by RunCommand in the same HTTP response.
    /// </summary>
    public sealed class UserAddMovieUI : EditableOptionsBase
    {
        public override string EditorTitle => "Add a Coming Soon Movie";

        public override string EditorDescription =>
            "Enter a title and optional year. Search TMDB to normalise the title, " +
            "or submit it manually without a provider match.";

        [DisplayName("Movie title")]
        public string MovieName { get; set; } = string.Empty;

        [DisplayName("Year (optional)")]
        public string ReleaseYear { get; set; } = string.Empty;

        public ButtonItem SearchButton { get; set; } =
            new ButtonItem("Search TMDB")
            {
                Icon = IconNames.search,
                Data1 = "Search",
                CommandId = "Search",
            };

        public ButtonItem SubmitManualButton { get; set; } =
            new ButtonItem("Submit Without Matching")
            {
                StandardIcon = StandardIcons.Add,
                Data1 = "SubmitManual",
                CommandId = "SubmitManual",
            };

        public GenericItemList Results { get; set; } = new GenericItemList();

        public StatusItem Status { get; set; } =
            new StatusItem("Status", string.Empty, ItemStatus.Unavailable);
    }
}