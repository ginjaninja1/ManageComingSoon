namespace ManageComingSoon.Services.Rules
{
    using ManageComingSoon.Model.Rules;
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Serialization;
    using System;
    using System.IO;
    using System.Linq;

    public class RadarrRuleSetStore
    {
        private const string FileName = "radarr-rulesets.json";

        private readonly IApplicationPaths appPaths;
        private readonly IJsonSerializer json;
        private readonly ILogger logger;

        public RadarrRuleSetStore(IApplicationPaths appPaths, IJsonSerializer json, ILogger logger)
        {
            this.appPaths = appPaths;
            this.json = json;
            this.logger = logger;
        }

        private string FilePath => Path.Combine(appPaths.DataPath, "manage-coming-soon", FileName);

        public RadarrRuleSetsFile Load()
        {
            var path = FilePath;

            if (!File.Exists(path))
            {
                var seeded = BuildDefaultFile();
                Save(seeded);
                return seeded;
            }

            try
            {
                var text = File.ReadAllText(path);
                var file = json.DeserializeFromString<RadarrRuleSetsFile>(text);

                if (file == null || file.RuleSets == null || file.RuleSets.Count == 0)
                {
                    var seeded = BuildDefaultFile();
                    Save(seeded);
                    return seeded;
                }

                return file;
            }
            catch (Exception ex)
            {
                logger.ErrorException("ManageComingSoon: Failed to read {0} — reseeding default rule set", ex, path);
                var seeded = BuildDefaultFile();
                Save(seeded);
                return seeded;
            }
        }

        public void Save(RadarrRuleSetsFile file)
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, json.SerializeToString(file));
        }

        public RuleNode GetActiveRuleRoot()
        {
            var file = Load();
            var active = file.RuleSets.FirstOrDefault(r => r.Id == file.ActiveRuleSetId)
                         ?? file.RuleSets.First();
            return active.Root;
        }

        private static RadarrRuleSetsFile BuildDefaultFile()
        {
            var defaultSet = new RuleSet
            {
                Name = "Default",
                Root = new RuleNode
                {
                    Kind = RuleNodeKind.Group,
                    LogicOperator = RuleLogicOperator.And,
                    Children =
                    {
                        new RuleNode { Kind = RuleNodeKind.Condition, Field = "monitored", Operator = RuleOperator.EQ, Value = "true" },
                        new RuleNode { Kind = RuleNodeKind.Condition, Field = "hasFile", Operator = RuleOperator.EQ, Value = "false" }
                    }
                }
            };

            return new RadarrRuleSetsFile
            {
                RuleSets = { defaultSet },
                ActiveRuleSetId = defaultSet.Id
            };
        }
    }
}