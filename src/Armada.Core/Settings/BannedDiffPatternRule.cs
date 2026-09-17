namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// One operator-configured banned-diff pattern. The banned-diff guard fails a change whose ADDED
    /// lines match any configured pattern, so a deployment can forbid an added code path without a
    /// code change and without naming its domain in this product's source. The pattern list ships
    /// EMPTY: with no rules the guard is a no-op, so the product carries the capability and none of a
    /// deployment's domain. A rule is a name, a .NET regex, and a human reason shown when it fires.
    /// </summary>
    public class BannedDiffPatternRule
    {
        /// <summary>A short identifier for the rule, shown in the check output.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>A .NET regular expression matched against each added code line (comments excluded).</summary>
        public string Pattern { get; set; } = String.Empty;

        /// <summary>Why the pattern is banned, shown to the operator when the guard fires.</summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>Deep-copy this rule so a hot reload replaces values without sharing references.</summary>
        /// <returns>An independent copy.</returns>
        public BannedDiffPatternRule Clone()
        {
            return new BannedDiffPatternRule { Name = Name, Pattern = Pattern, Description = Description };
        }
    }
}
