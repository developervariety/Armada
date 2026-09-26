namespace Armada.Core.Context
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Renders a <see cref="ContextRetrievalResult"/> into the AI-Memory part of a captain brief: the
    /// always-on core rules, the mission's relevant leaves (matching-domain safety leaves first), and a
    /// one-line pointer to the on-demand fetch tool.
    ///
    /// The memory itself is delivered as files in the dock, not inline in the instruction file. Inline,
    /// the memory alone outgrew what a runtime reads in one call, and a total size cap on the
    /// instruction file then cut the mission's own text to make room for it. As files, nothing is cut:
    /// each file is bounded to fit one read on every runtime, and the instruction file carries a short
    /// section that lists them in reading order.
    ///
    /// Leaves may be sorted into reference material. A reference leaf is still delivered in full; it is
    /// listed after the read-first files, for the captain to read when its task touches the topic. Core
    /// rules and safety leaves are always read first.
    ///
    /// It deliberately does NOT tell the captain to read every file under <c>shared/</c>. The memory the
    /// mission needs is in the delivered files, and the captain pulls anything else by topic on demand.
    /// The chunk rendering matches the always-on core bundle shape (<see cref="ContextIndexGenerator"/>):
    /// a rule, its one-line summary, and its logical source path. It never emits a host-absolute path
    /// (chunk paths are already root-relative), so the files are safe to ship.
    /// </summary>
    public static class ContextBriefRenderer
    {
        #region Public-Members

        /// <summary>The captain-facing on-demand fetch tool named in the section pointer.</summary>
        public const string FetchToolName = "armada_fetch_context";

        /// <summary>Dock-relative folder the memory files are written to.</summary>
        public const string MemoryFolder = "_briefing/memory";

        /// <summary>Largest memory file, in UTF-8 bytes. See <see cref="BriefFilePacker.MaxFileBytes"/>.</summary>
        public const int MaxFileBytes = BriefFilePacker.MaxFileBytes;

        /// <summary>Largest memory file, in lines. See <see cref="BriefFilePacker.MaxFileLines"/>.</summary>
        public const int MaxFileLines = BriefFilePacker.MaxFileLines;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Render the memory delivery from a retrieval result: the bounded memory files and the brief
        /// section that lists them.
        /// </summary>
        /// <param name="result">The retrieval result: core, must-retrieve, and ranked leaves.</param>
        /// <param name="memoryRoot">The configured AI-Memory root, for the durable-memory pointer.</param>
        /// <param name="repoFolder">This vessel's folder name under <c>repos/</c>, or null.</param>
        /// <param name="fetchToolEnabled">Whether to emit the on-demand fetch-tool pointer.</param>
        /// <param name="referenceTopics">Ranked-leaf topics to deliver as reference material; null or empty delivers every leaf as read-first.</param>
        /// <returns>The files and the section.</returns>
        public static ContextBriefDelivery RenderDelivery(
            ContextRetrievalResult result,
            string memoryRoot,
            string? repoFolder,
            bool fetchToolEnabled,
            ISet<string>? referenceTopics = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            string root = (memoryRoot ?? "").TrimEnd('/', '\\');

            List<ContextChunk> core = result.Core ?? new List<ContextChunk>();
            List<ContextChunk> safety = result.MustRetrieve ?? new List<ContextChunk>();
            List<ContextChunk> leaves = result.Leaves ?? new List<ContextChunk>();

            List<ContextChunk> missionLeaves = new List<ContextChunk>();
            List<ContextChunk> referenceLeaves = new List<ContextChunk>();
            foreach (ContextChunk leaf in leaves)
            {
                if (leaf == null) continue;
                if (referenceTopics != null && referenceTopics.Contains(leaf.Topic)) referenceLeaves.Add(leaf);
                else missionLeaves.Add(leaf);
            }

            List<ContextBriefFile> files = new List<ContextBriefFile>();
            AppendGroup(files, "core", "Core rules (always apply)", true, core.Select(c => RenderChunk(c, false)).ToList(), core);

            List<ContextChunk> missionChunks = new List<ContextChunk>();
            List<string> missionRenders = new List<string>();
            foreach (ContextChunk c in safety)
            {
                if (c == null) continue;
                missionChunks.Add(c);
                missionRenders.Add(RenderChunk(c, true));
            }
            foreach (ContextChunk c in missionLeaves)
            {
                missionChunks.Add(c);
                missionRenders.Add(RenderChunk(c, false));
            }
            AppendGroup(files, "mission", "Retrieved for this mission", true, missionRenders, missionChunks);
            AppendGroup(files, "reference", "Reference: retrieved as possibly relevant", false,
                referenceLeaves.Select(c => RenderChunk(c, false)).ToList(), referenceLeaves);

            ContextBriefDelivery delivery = new ContextBriefDelivery();
            delivery.Files = files;
            delivery.Section = RenderSection(files, root, repoFolder, fetchToolEnabled, core.Count == 0);
            return delivery;
        }

        #endregion

        #region Private-Methods

        private static string RenderSection(List<ContextBriefFile> files, string root, string? repoFolder, bool fetchToolEnabled, bool noCore)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("## Shared Memory\n");
            sb.Append("Durable, cross-mission knowledge for this fleet lives at `").Append(root).Append("`.\n");
            sb.Append("The memory for this mission is in the files below, in your working directory. ");
            sb.Append("You do not need to read the whole memory tree: the relevant rules are in these files.\n");

            if (!String.IsNullOrEmpty(repoFolder))
            {
                sb.Append("This vessel's own memory folder is `").Append(root).Append("/repos/").Append(repoFolder)
                    .Append("/`; its relevant parts are in these files.\n");
            }

            sb.Append("AI-Memory is the authoritative durable memory for this fleet; the runtime's own file-memory ");
            sb.Append("protocol is not shared state, so do not write to it. ");
            sb.Append("It is reference material, not authority: playbooks, vessel instructions, and this mission ");
            sb.Append("brief win on conflict.\n");

            List<ContextBriefFile> readFirst = files.Where(f => f.ReadFirst).ToList();
            List<ContextBriefFile> reference = files.Where(f => !f.ReadFirst).ToList();

            // Mode-neutral on purpose: the same wording reaches a read-only mission, which changes nothing.
            sb.Append("\n### Read first\n");
            sb.Append("Read each of these files in full, in this order, before you begin the mission's work. ");
            sb.Append("Each file fits in one read. Do not skip one because the list is long.\n");
            if (noCore) sb.Append("\n_No core rules were resolved._\n");
            foreach (ContextBriefFile f in readFirst) AppendFileLine(sb, f);

            if (reference.Count > 0)
            {
                sb.Append("\n### Reference\n");
                sb.Append("Retrieved as possibly relevant, but judged not to apply to this mission's work. ");
                sb.Append("Read a file when your task touches one of its topics.\n");
                foreach (ContextBriefFile f in reference) AppendFileLine(sb, f);
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

        private static void AppendFileLine(StringBuilder sb, ContextBriefFile f)
        {
            sb.Append("- `").Append(f.RelativePath).Append("` (").Append(f.Topics.Count)
                .Append(f.Topics.Count == 1 ? " topic): " : " topics): ")
                .Append(String.Join(", ", f.Topics.Distinct(StringComparer.Ordinal)))
                .Append('\n');
        }

        /// <summary>
        /// Packs rendered chunks into bounded files, in order. A chunk is never split across files unless
        /// it alone exceeds the bound, in which case it is split on line boundaries into consecutive parts.
        /// </summary>
        private static void AppendGroup(
            List<ContextBriefFile> files,
            string kind,
            string heading,
            bool readFirst,
            List<string> renders,
            List<ContextChunk> chunks)
        {
            if (renders.Count == 0) return;

            StringBuilder current = new StringBuilder();
            List<string> currentTopics = new List<string>();

            void Flush()
            {
                if (currentTopics.Count == 0) return;
                int number = files.Count + 1;
                string name = number.ToString("00") + "-" + kind + ".md";
                files.Add(new ContextBriefFile
                {
                    RelativePath = MemoryFolder + "/" + name,
                    Content = "# " + heading + "\n" + current.ToString(),
                    ReadFirst = readFirst,
                    Topics = new List<string>(currentTopics)
                });
                current.Clear();
                currentTopics.Clear();
            }

            int headerBytes = BriefFilePacker.HeaderBytes(heading);
            int headerLines = BriefFilePacker.HeaderLines;

            for (int i = 0; i < renders.Count; i++)
            {
                string render = renders[i];
                string topic = chunks[i].Topic;

                if (BriefFilePacker.Fits(current.ToString() + render, headerBytes, headerLines))
                {
                    current.Append(render);
                    currentTopics.Add(topic);
                    continue;
                }

                Flush();
                if (BriefFilePacker.Fits(render, headerBytes, headerLines))
                {
                    current.Append(render);
                    currentTopics.Add(topic);
                    continue;
                }

                // One chunk larger than a file: split it on line boundaries into consecutive parts.
                foreach (string part in BriefFilePacker.Split(render, heading))
                {
                    current.Append(part);
                    currentTopics.Add(topic);
                    Flush();
                }
            }

            Flush();
        }

        private static string RenderChunk(ContextChunk c, bool isSafety)
        {
            if (c == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append("\n---\n\n");
            sb.Append("## ").Append(c.Topic);
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
            return sb.ToString();
        }

        #endregion
    }
}
