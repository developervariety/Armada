namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// A configurable model-family classification rule. When a concrete model name is
    /// not in a tier membership list, the first matching pattern assigns that model to
    /// <see cref="Tier"/>. Patterns are .NET regular expressions, matched
    /// case-insensitively. An empty pattern is ignored.
    /// </summary>
    public class ModelFamilyClassificationRule
    {
        #region Public-Members

        /// <summary>
        /// Regular expression matched against the concrete model name. Matching is
        /// case-insensitive. Empty or whitespace patterns never match.
        /// </summary>
        public string Pattern
        {
            get => _Pattern;
            set => _Pattern = value ?? String.Empty;
        }

        /// <summary>
        /// Target tier when the pattern matches: <c>mid</c> or <c>high</c>.
        /// </summary>
        public string Tier
        {
            get => _Tier;
            set => _Tier = String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim();
        }

        #endregion

        #region Private-Members

        private string _Pattern = String.Empty;
        private string _Tier = String.Empty;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an empty rule.
        /// </summary>
        public ModelFamilyClassificationRule()
        {
        }

        /// <summary>
        /// Instantiate a rule with a pattern and target tier.
        /// </summary>
        /// <param name="pattern">Regular expression matched against the model name.</param>
        /// <param name="tier">Target tier (<c>mid</c> or <c>high</c>).</param>
        public ModelFamilyClassificationRule(string pattern, string tier)
        {
            Pattern = pattern;
            Tier = tier;
        }

        #endregion
    }
}
