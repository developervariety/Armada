namespace Armada.Core.Services
{
    /// <summary>Shared reading guidance for implementation template defaults and fallback briefs.</summary>
    internal static class ImplementationContextConservation
    {
        internal static string Build()
        {
            return
                "## Context Conservation (CRITICAL)\n" +
                "\n" +
                "Use bounded reads and an evidence ledger across compaction. The mission's explicit required " +
                "reads and approved read budget take precedence over default lookup guidance. Never omit assigned " +
                "evidence.\n" +
                "\n" +
                "1. **Read what the task requires.** For an ordinary edit, target the relevant sections. Read " +
                "each required whole file, report, log and source closure in consecutive bounded chunks to its " +
                "end. Searches, hashes, inventories and summaries do not replace complete reads.\n" +
                "\n" +
                "2. **Keep a read ledger.** Record completed bodies, offsets, findings and remaining work. Do not " +
                "re-read text you still hold. Next chunks, changed inputs and evidence lost to compaction may " +
                "require further reads.\n" +
                "\n" +
                "3. **Stay within the assigned scope.** Avoid unrelated source. Read all required callers, " +
                "dependencies and references, even across many files.\n" +
                "\n" +
                "4. **Check transport limits.** Size reads and batches to receive every required byte. Retrieve " +
                "clipped ranges before relying on the evidence. Count reads against the approved budget.\n" +
                "\n" +
                "5. **Finish the assigned duties.** File and line counts are not stop rules. Continue bounded " +
                "reads and compaction within the approved budget. At a real resource limit or unavailable input, " +
                "preserve work and report exact remaining evidence and unmet acceptance criteria. Never claim " +
                "unfinished required work is complete.\n";
        }
    }
}
