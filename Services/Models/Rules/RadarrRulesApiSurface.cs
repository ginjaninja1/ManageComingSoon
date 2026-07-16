namespace ManageComingSoon.Services.Rules
{
    using ManageComingSoon.Model.Rules;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Controller;
    using MediaBrowser.Model.Services;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    [Route("/ManageComingSoon/RadarrRuleSets", "GET", Summary = "Gets all Radarr rule sets")]
    public class GetRadarrRuleSets : IReturn<RadarrRuleSetsFile> { }

    [Route("/ManageComingSoon/RadarrRuleSets", "POST", Summary = "Saves the full set of Radarr rule sets")]
    public class SaveRadarrRuleSets : IReturn<object>
    {
        public RadarrRuleSetsFile Payload { get; set; }
    }

    [Route("/ManageComingSoon/RadarrLastResponse", "GET", Summary = "Gets the raw JSON from the most recent Radarr sync, for live rule preview")]
    public class GetRadarrLastResponse : IReturn<object> { }

    [Route("/ManageComingSoon/RadarrRulePreview", "POST", Summary = "Evaluates a candidate rule tree against the last Radarr response")]
    public class PreviewRadarrRule : IReturn<object>
    {
        public RuleNode Rule { get; set; }
    }

    public class RadarrRulesApiSurface : IService
    {
        private readonly RadarrRuleSetStore store;
        private readonly IServerApplicationHost appHost;

        public RadarrRulesApiSurface(RadarrRuleSetStore store, IServerApplicationHost appHost)
        {
            this.store = store;
            this.appHost = appHost;
        }

        public object Get(GetRadarrRuleSets request) => store.Load();

        public object Post(SaveRadarrRuleSets request)
        {
            store.Save(request.Payload);
            return new { Success = true };
        }

        public object Get(GetRadarrLastResponse request)
        {
            var appPaths = appHost.Resolve<IApplicationPaths>();
            var path = Path.Combine(appPaths.DataPath, "manage-coming-soon", "radarr-last-response.json");
            return File.Exists(path) ? File.ReadAllText(path) : "[]";
        }

        public object Post(PreviewRadarrRule request)
        {
            var appPaths = appHost.Resolve<IApplicationPaths>();
            var path = Path.Combine(appPaths.DataPath, "manage-coming-soon", "radarr-last-response.json");

            if (!File.Exists(path))
                return new { MatchCount = 0, Titles = new List<string>() };

            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var matches = new List<string>();
                foreach (var movie in doc.RootElement.EnumerateArray())
                {
                    if (RuleEvaluator.Matches(movie, request.Rule))
                    {
                        matches.Add(movie.TryGetProperty("title", out var t) ? t.GetString() : "(unknown)");
                    }
                }
                return new { MatchCount = matches.Count, Titles = matches };
            }
        }
    }
}