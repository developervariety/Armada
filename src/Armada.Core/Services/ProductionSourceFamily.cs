namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule that assigns an objective to a production source family. A family is never
    /// inferred from title text.
    /// </summary>
    public static class ProductionSourceFamily
    {
        /// <summary>Family used when the objective has no port tag.</summary>
        public const string Unknown = "unknown";

        /// <summary>Family used when the objective has conflicting port tags.</summary>
        public const string Invalid = "invalid";

        /// <summary>Resolve the family from the objective's single normalized <c>port:</c> tag.</summary>
        /// <param name="objective">Objective to classify.</param>
        /// <returns>The lower-case family, <see cref="Unknown"/>, or <see cref="Invalid"/>.</returns>
        public static string Resolve(Objective objective)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            List<string> tags = objective.Tags.Where(item => item.StartsWith("port:", StringComparison.OrdinalIgnoreCase)).ToList();
            return tags.Count == 1 ? tags[0].Substring("port:".Length).Trim().ToLowerInvariant()
                : tags.Count > 1 ? Invalid : Unknown;
        }
    }
}
