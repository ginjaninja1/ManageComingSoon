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
                return new { MatchCount = 0, Fields = new List<string>(), Matches = new List<object>() };

            var fields = CollectFields(request.Rule, new List<string>());

            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                int matchCount = 0;
                var rows = new List<object>();

                foreach (var movie in doc.RootElement.EnumerateArray())
                {
                    if (!RuleEvaluator.Matches(movie, request.Rule))
                        continue;

                    matchCount++;

                    // Cap detail rows at 10 — counting continues above,
                    // but the table itself only ever shows the first 10.
                    if (rows.Count < 10)
                    {
                        var values = new Dictionary<string, string>();
                        foreach (var f in fields)
                        {
                            values[f] = RuleEvaluator.ResolveDisplayValue(movie, f);
                        }

                        string title = movie.TryGetProperty("title", out var t) ? t.GetString() : "(unknown)";
                        rows.Add(new { Title = title, Values = values });
                    }
                }

                return new { MatchCount = matchCount, Fields = fields, Matches = rows };
            }
        }

        // Recursively collects the distinct Field names referenced anywhere
        // in the rule tree, in first-encountered order — these become the
        // preview table's rows.
        private static List<string> CollectFields(RuleNode node, List<string> acc)
        {
            if (node == null) return acc;

            if (node.Kind == RuleNodeKind.Condition)
            {
                if (!string.IsNullOrEmpty(node.Field) && !acc.Contains(node.Field))
                    acc.Add(node.Field);
            }
            else if (node.Children != null)
            {
                foreach (var child in node.Children)
                    CollectFields(child, acc);
            }

            return acc;
        }
    }
}