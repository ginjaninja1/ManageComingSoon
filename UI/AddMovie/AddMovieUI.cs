// ManageComingSoon - Add Movie UI
// Minimal UI shell for the multi-movie search page.
// All list content (including Add All row) is built dynamically in
// AddMoviePageView.RebuildMovieList(). Only the input fields, list,
// and overall status item live here.

namespace ManageComingSoon.UI.AddMovie
{
    using System.ComponentModel;
    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;

    public class AddMovieUI : EditableOptionsBase
    {
        public override string EditorTitle => "Add Coming Soon Movies";
        public override string EditorDescription =>
            "Enter a movie name and optional year, then click Add. " +
            "Each entry is searched against TMDB automatically. " +
            "To add several at once, separate them with | and optionally append " +
            ";Year to any of them, e.g. Dune Part Two;2024|Gladiator II|The Batman;2022.";

        // ---- Search inputs (tight block, no caption) ------------------------
        [DisplayName("Movie Name")]
        [Description("or Movie1;Year|Movie2|Movie3;Year...)")]
        public string MovieName { get; set; } = string.Empty;

        [DisplayName("Year (optional)")]
        public string ReleaseYear { get; set; } = string.Empty;

        public ButtonItem AddToListButton { get; set; } =
            new ButtonItem("Add via Provider Match")
            {
                StandardIcon = StandardIcons.Add,
                Data1 = "AddToList",
                CommandId = "AddToList",
            };

        public ButtonItem AddManualButton { get; set; } =
            new ButtonItem("Add Manual")
            {
                StandardIcon = StandardIcons.Add,
                Data1 = "AddManual",
                CommandId = "AddManual",
            };

        
        public GenericItemList MovieList { get; set; } = new GenericItemList();

        // ---- Overall status (diagnostic footer) -----------------------------
        public StatusItem OverallStatus { get; set; } =
            new StatusItem("Status", string.Empty, ItemStatus.Unavailable);

        
        public GenericItemList CompletedList { get; set; } = new GenericItemList();

        
    }
}
