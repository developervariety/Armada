namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Checks declared objective execution requirements against the environment captains launch in.
    /// </summary>
    public static class ObjectiveExecutionRequirementsValidator
    {
        /// <summary>
        /// Return every unavailable execution requirement. Executables are resolved by lookup and never run.
        /// </summary>
        /// <param name="requirements">Declared execution requirements, or null when none are declared.</param>
        /// <param name="environment">Read-only probe for the captain execution environment.</param>
        /// <returns>Blocking issues for requirements that the environment does not satisfy.</returns>
        public static List<ObjectiveDispatchPreviewIssue> Evaluate(
            ObjectiveExecutionRequirements? requirements,
            ICaptainExecutionEnvironmentProbe environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));

            List<ObjectiveDispatchPreviewIssue> issues = new List<ObjectiveDispatchPreviewIssue>();
            if (requirements == null || !requirements.HasAny) return issues;

            const string area = "execution";

            if (!String.IsNullOrWhiteSpace(requirements.OperatingSystem)
                && !String.Equals(requirements.OperatingSystem, environment.OperatingSystem, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(issues, "execution_operating_system_unavailable", area,
                    "Captains execute on " + environment.OperatingSystem + ", not the required " + requirements.OperatingSystem + ".",
                    requirements.OperatingSystem);
            }

            if (!String.IsNullOrWhiteSpace(requirements.Architecture)
                && !String.Equals(requirements.Architecture, environment.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(issues, "execution_architecture_unavailable", area,
                    "Captains execute on " + environment.Architecture + ", not the required " + requirements.Architecture + " architecture.",
                    requirements.Architecture);
            }

            foreach (string executable in requirements.Executables ?? new List<string>())
            {
                if (String.IsNullOrWhiteSpace(executable)) continue;
                if (environment.ResolveExecutable(executable) != null) continue;
                AddIssue(issues, "execution_executable_unavailable", area,
                    "Required executable " + executable + " is not available to captains. A readable file is not a runnable one without it.",
                    executable);
            }

            foreach (string dependency in requirements.DependencyPaths ?? new List<string>())
            {
                if (String.IsNullOrWhiteSpace(dependency)) continue;
                if (environment.PathExists(dependency)) continue;
                AddIssue(issues, "execution_dependency_unavailable", area,
                    "Required dependency path is not present in the captain execution environment.", dependency);
            }

            if (!String.IsNullOrWhiteSpace(requirements.IsolationBoundary))
            {
                bool wantsContainer = String.Equals(requirements.IsolationBoundary, "Container", StringComparison.OrdinalIgnoreCase);
                if (wantsContainer != environment.IsContainer)
                {
                    AddIssue(issues, "execution_isolation_unavailable", area,
                        "Captains execute " + (environment.IsContainer ? "inside a container" : "directly on the host") +
                        ", not behind the required " + requirements.IsolationBoundary + " boundary.",
                        requirements.IsolationBoundary);
                }
            }

            if (!String.IsNullOrWhiteSpace(requirements.LicensedContext)
                && !(environment.LicensedContexts ?? new List<string>()).Any(name => String.Equals(name, requirements.LicensedContext, StringComparison.OrdinalIgnoreCase)))
            {
                AddIssue(issues, "execution_licensed_context_unavailable", area,
                    "Licensed context " + requirements.LicensedContext + " is not available to captains.",
                    requirements.LicensedContext);
            }

            return issues;
        }

        private static void AddIssue(
            List<ObjectiveDispatchPreviewIssue> issues,
            string code,
            string area,
            string message,
            string? relatedValue)
        {
            issues.Add(new ObjectiveDispatchPreviewIssue
            {
                Code = code,
                Area = area,
                Severity = ReadinessSeverityEnum.Error,
                Message = message,
                RelatedValue = relatedValue
            });
        }
    }
}
