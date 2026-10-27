namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The prior-stage section persisted on the next stage's description: the stage, branch and commit, and a placeholder
    /// that the mission instructions replace at launch with the prior stage's handoff artifact rendered for the reader
    /// (<see cref="HandoffRenderer"/>). The description never carries an output excerpt of its own.
    /// </summary>
    internal static class PipelineHandoffContext
    {
        /// <summary>
        /// Start of the prior-stage section in a description.
        /// </summary>
        internal const string SectionMarker = "\n\n---\n## Prior Stage Output";

        /// <summary>
        /// Line replaced at launch with the rendered handoff artifact.
        /// </summary>
        internal const string ArtifactPlaceholder = "<!-- armada:handoff-artifact -->";

        internal static string Build(Mission stage)
        {
            return SectionMarker + "\n"
                + "Mission: " + stage.Id + " (" + PersonaCatalog.NormalizeName(stage.Persona) + ")\n"
                + "Branch: " + (stage.BranchName ?? "unknown") + "\n"
                + "Commit: " + (stage.CommitHash ?? "unknown; verify branch HEAD") + "\n"
                + "Keep the original acceptance criteria. Recheck every prior finding; report resolved, superseded, or unresolved.\n"
                + "Inspect current source and git diff on this branch. The prior stage's handoff artifact (findings, checks, "
                + "commits, output and diff) is delivered below within your budget, with a pointer to the complete artifact and a list of what did not fit. "
                + "Do not treat a partial delivery as the complete review or inherited test claims as your own checks.\n"
                + "\n### Prior evidence\n" + ArtifactPlaceholder + "\n"
                + "(Handoff artifact of mission " + stage.Id + ": rendered into the mission instructions at launch; "
                + "list it with list_handoff_artifacts or GET /api/v1/missions/" + stage.Id + "/handoff-artifacts.)\n";
        }

        /// <summary>
        /// Replace the placeholder section in a description with a rendered block. A description without the placeholder
        /// (written before handoff artifacts) is returned unchanged.
        /// </summary>
        internal static string Fill(string? description, string rendered)
        {
            string text = description ?? String.Empty;
            int at = text.IndexOf(ArtifactPlaceholder, StringComparison.Ordinal);
            if (at < 0) return text;
            int lineEnd = text.IndexOf('\n', at);
            int noteEnd = lineEnd < 0 ? -1 : text.IndexOf('\n', lineEnd + 1);
            string after = noteEnd < 0 ? String.Empty : text.Substring(noteEnd + 1);
            return text.Substring(0, at) + rendered.TrimEnd() + "\n" + after;
        }
    }
}
