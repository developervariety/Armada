namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>Bounded stage evidence with references to the complete persisted artifacts.</summary>
    internal static class PipelineHandoffContext
    {
        internal const int MaxOutputChars = 3000;

        internal static string Build(Mission stage)
        {
            return "\n\n---\n## Prior Stage Output\n"
                + "Mission: " + stage.Id + " (" + PersonaCatalog.NormalizeName(stage.Persona) + ")\n"
                + "Branch: " + (stage.BranchName ?? "unknown") + "\n"
                + "Commit: " + (stage.CommitHash ?? "unknown; verify branch HEAD") + "\n"
                + "Keep the original acceptance criteria. Recheck every prior finding; report resolved, superseded, or unresolved.\n"
                + "Inspect current source and git diff on this branch. Full output and diff remain in the prior mission; "
                + "retrieve them using mission_status/get_mission_diff (or the API) when this excerpt is incomplete. "
                + "Do not treat an excerpt as the complete review or inherited test claims as your own checks.\n"
                + "\n### Prior evidence\n" + BoundOutput(stage.AgentOutput);
        }

        internal static string BoundOutput(string? output)
        {
            if (String.IsNullOrWhiteSpace(output)) return "No prior output recorded; inspect the branch and mission artifacts.\n";
            string text = output!.Trim();
            if (text.Length <= MaxOutputChars) return text;
            return text.Substring(0, MaxOutputChars / 2)
                + "\n[Excerpt only: retrieve the full prior mission output before resolving findings.]\n"
                + text.Substring(text.Length - MaxOutputChars / 2);
        }
    }
}
