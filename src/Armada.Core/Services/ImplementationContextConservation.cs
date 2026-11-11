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
                "Your context window is limited. Use bounded reads and retain an evidence ledger across " +
                "compaction. The mission's explicit required reads and approved read budget take precedence " +
                "over default lookup guidance. These rules control how you read, not how much assigned " +
                "evidence you may omit.\n" +
                "\n" +
                "1. **Read what the task requires.** For an ordinary edit, target the relevant sections. " +
                "When the mission requires a whole file, report, log or listed source closure, read it in " +
                "consecutive bounded chunks to its end. A search, hash, inventory or summary does not " +
                "replace a required complete read.\n" +
                "\n" +
                "2. **Keep a read ledger.** Record complete bodies, chunk offsets, findings and remaining " +
                "work. Do not re-read text you still hold. Reading the next chunk, a changed input, or " +
                "evidence lost to compaction is not unnecessary repetition.\n" +
                "\n" +
                "3. **Stay within the assigned scope.** Do not explore unrelated source. Read the callers, " +
                "dependencies and reference material the task explicitly requires, even when that assigned " +
                "closure spans many files.\n" +
                "\n" +
                "4. **Check transport limits.** Keep each read and batch small enough to receive every " +
                "required byte. A clipped response is unfinished reading; retrieve its missing range " +
                "before relying on the evidence. Count reads against the mission's approved budget.\n" +
                "\n" +
                "5. **Finish the assigned duties.** Do not stop merely because assigned evidence spans " +
                "many files or lines. Continue with bounded reads and compaction while the approved " +
                "budget permits. If a real resource limit or unavailable input prevents completion, " +
                "preserve finished work and report the exact remaining evidence and unmet acceptance " +
                "criteria. Never claim unfinished required work is complete.\n";
        }
    }
}
