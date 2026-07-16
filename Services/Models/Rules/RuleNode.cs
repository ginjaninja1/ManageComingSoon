namespace ManageComingSoon.Model.Rules
{
    using System.Collections.Generic;

    public enum RuleNodeKind { Group, Condition }

    public enum RuleLogicOperator { And, Or }

    public enum RuleOperator { LT, LTE, GT, GTE, EQ, NEQ, CONTAINS, NOTCONTAINS }

    public class RuleNode
    {
        public RuleNodeKind Kind { get; set; } = RuleNodeKind.Condition;
        public bool Not { get; set; } = false;

        // ---- Group fields (used when Kind == Group) ----
        public RuleLogicOperator LogicOperator { get; set; } = RuleLogicOperator.And;
        public List<RuleNode> Children { get; set; } = new List<RuleNode>();

        // ---- Condition fields (used when Kind == Condition) ----
        public string Field { get; set; } = string.Empty;
        public RuleOperator Operator { get; set; } = RuleOperator.EQ;
        public string Value { get; set; } = string.Empty;
    }

    public class RuleSet
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public RuleNode Root { get; set; } = new RuleNode { Kind = RuleNodeKind.Group };
    }

    public class RadarrRuleSetsFile
    {
        public List<RuleSet> RuleSets { get; set; } = new List<RuleSet>();
        public string ActiveRuleSetId { get; set; } = string.Empty;
    }
}