namespace ManageComingSoon.UI.MakeLive
{
    using System.ComponentModel;
    using Emby.Web.GenericEdit;
    using Emby.Web.GenericEdit.Elements;
    using Emby.Web.GenericEdit.Elements.List;

    // -----------------------------------------------------------------------
    // Minimal UI shell, matching the Add Coming Soon tab's persona.
    // Per-row state (icon, status colour, progress, buttons) is built entirely
    // in MakeLivePageView.RebuildMovieList(). Only the manual Refresh control
    // and a footer status line live here.
    // -----------------------------------------------------------------------
    public class MakeLiveUI : EditableOptionsBase
    {
        public override string EditorTitle => "Make Live";
        public override string EditorDescription =>
            "Coming Soon movies are analysed automatically. Toggle the movies you want to make " +
            "live, then click Make Live on a row or use Make All Live to process every toggled movie.";

        public ButtonItem RefreshListButton { get; set; } =
            new ButtonItem("Refresh List")
            {
                Icon = IconNames.refresh,
                Data1 = "RefreshList",
                CommandId = "RefreshList",
            };

        public GenericItemList MovieList { get; set; } = new GenericItemList();

        public StatusItem OverallStatus { get; set; } =
            new StatusItem("Status", string.Empty, ItemStatus.Unavailable);
    }
}