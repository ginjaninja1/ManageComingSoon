namespace ManageComingSoon.Providers
{
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;

    public class RadarrExternalId : IExternalId
    {
        public string Name => "Radarr";

        public string Key => "RadarrId";

        public string UrlFormatString =>
            (ManageComingSoonPlugin.Instance.Configuration.RadarrUrl ?? string.Empty).TrimEnd('/') + "/movie/{0}";

        public bool Supports(IHasProviderIds item) => true;
    }
}