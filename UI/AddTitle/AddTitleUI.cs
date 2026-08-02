// ManageComingSoon - Add Title UI
// Minimal UI shell for the multi-title search page.
// All list content (including Add All row) is built dynamically in
// AddTitlePageView.RebuildTitleList(). Only the input fields, list,
// and overall status item live here.

namespace ManageComingSoon.UI.AddTitle
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

    public class AddTitleUI : EditableOptionsBase
    {
        public override string EditorTitle => "Add Coming Soon Titles";
        public override string EditorDescription =>
            "Choose Movie or TV Show, enter a title and optional year, then click Add. " +
            "Each entry is searched against TMDB automatically. " +
            "To add several at once, separate them with | and optionally append " +
            ";Year to any of them, e.g. Dune Part Two;2024|Gladiator II|The Batman;2022.";

        // ---- Search inputs (tight block, no caption) ------------------------
        [Browsable(false)]
        public IEnumerable<EditorSelectOption> MediaTypeOptions { get; set; } =
            new[] { new EditorSelectOption("Movie", "Movie"), new EditorSelectOption("TvShow", "TV Show") };

        [DisplayName("Content Type")]
        [SelectItemsSource(nameof(MediaTypeOptions))]
        public string MediaType { get; set; } = "Movie";

        [DisplayName("Title")]
        [Description("or Title1;Year|Title2|Title3;Year...)")]
        public string TitleName { get; set; } = string.Empty;

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

        
        public GenericItemList TitleList { get; set; } = new GenericItemList();

        // ---- Overall status (diagnostic footer) -----------------------------
        public StatusItem OverallStatus { get; set; } =
            new StatusItem("Status", string.Empty, ItemStatus.Unavailable);

        
        public GenericItemList CompletedList { get; set; } = new GenericItemList();

        
    }
}
