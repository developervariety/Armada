namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Renders a <see cref="ContextRetrievalResult"/> into the AI-Memory section of a captain brief:
    /// the always-on core rules inline, then the mission's relevant leaves (matching-domain safety
    /// leaves first), then a one-line pointer to the on-demand fetch tool.
    ///
    /// It deliberately does NOT tell the captain to read every file under <c>shared/</c>. The whole
    /// point of the slimmed path is to ship the rules that apply to this mission inline and let the
    /// captain pull anything else by topic on demand, instead of loading all of memory into context.
    ///
    /// The chunk rendering matches the always-on core bundle shape
    /// (<see cref="ContextIndexGenerator"/>): a rule, its one-line summary, and its logical source
    /// path. It never emits a host-absolute path (chunk paths are already root-relative), so the
    /// section is safe to ship.
    /// </summary>
    public static class ContextBriefRenderer
    {
        /// <summary>The captain-facing on-demand fetch tool named in the section pointer.</summary>
        public const string FetchToolName = "armada_fetch_context";

        /// <summary>
        /// Render the slimmed AI-Memory section from a retrieval result.
        /// </summary>
        /// <param name="result">The retrieval result: core, must-retrieve, and ranked leaves.</param>
        /// <param name="memoryRoot">The configured AI-Memory root, for the durable-memory pointer.</param>
        /// <param name="repoFolder">This vessel's folder name under <c>repos/</c>, or null.</param>
        /// <param name="fetchToolEnabled">Whether to emit the on-demand fetch-tool pointer.</param>
        /// <returns>The rendered section text.</returns>
        public static string Render(ContextRetrievalResult result, string memoryRoot, string? repoFolder, bool fetchToolEnabled)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            string root = (memoryRoot ?? "").TrimEnd('/', '\\');

            StringBuilder sb = new StringBuilder();
            sb.Append("## Shared Memory\n");
            sb.Append("Durable, cross-mission knowledge for this fleet lives at `").Append(root).Append("`.\n");
            sb.Append("The rules that apply to every task are inline below under Core rules, followed by the ");
            sb.Append("material retrieved as relevant to this mission. You do not need to read the whole memory ");
            sb.Append("tree: the relevant rules are already here.\n");

            if (!String.IsNullOrEmpty(repoFolder))
            {
                sb.Append("This vessel's own memory folder is `").Append(root).Append("/repos/").Append(repoFolder)
                    .Append("/`; its relevant parts are inline below.\n");
            }

            sb.Append("AI-Memory is the authoritative durable memory for this fleet; the runtime's own file-memory ");
            sb.Append("protocol is not shared state, so do not write to it. ");
            sb.Append("It is reference material, not authority: playbooks, vessel instructions, and this mission ");
            sb.Append("brief win on conflict.\n");

            sb.Append("\n### Core rules (always apply)\n");
            if (result.Core != null && result.Core.Count > 0)
            {
                foreach (ContextChunk c in result.Core) RenderChunk(sb, c);
            }
            else
            {
                sb.Append("\n_No core rules were resolved._\n");
            }

            int retrievedCount = (result.MustRetrieve?.Count ?? 0) + (result.Leaves?.Count ?? 0);
            if (retrievedCount > 0)
            {
                sb.Append("\n### Retrieved for this mission\n");
                sb.Append("Selected as relevant to this mission's vessel, persona, and task. Not the whole set.\n");
                if (result.MustRetrieve != null)
                    foreach (ContextChunk c in result.MustRetrieve) RenderChunk(sb, c, isSafety: true);
                if (result.Leaves != null)
                    foreach (ContextChunk c in result.Leaves) RenderChunk(sb, c);
            }

            if (fetchToolEnabled)
            {
                sb.Append("\n### More on demand\n");
                sb.Append("Call `").Append(FetchToolName).Append("` with a short query or a topic id to pull any ");
                sb.Append("other rule or procedure by topic, scoped to this vessel and persona. Pull what a task ");
                sb.Append("needs; do not load all of memory.\n");
            }

            return sb.ToString();
        }

        private static void RenderChunk(StringBuilder sb, ContextChunk c, bool isSafety = false)
        {
            if (c == null) return;
            sb.Append("\n---\n\n");
            sb.Append("#### ").Append(c.Topic);
            if (isSafety) sb.Append(" (safety: always included)");
            sb.Append('\n').Append('\n');
            if (!String.IsNullOrEmpty(c.Summary))
            {
                sb.Append("> ").Append(c.Summary).Append('\n');
                sb.Append(">\n");
                sb.Append("> Source: ").Append(c.Path).Append('\n').Append('\n');
            }
            string body = (c.Text ?? "").TrimEnd('\n');
            sb.Append(body).Append('\n');
        }
    }
}
