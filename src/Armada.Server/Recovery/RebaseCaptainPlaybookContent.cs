namespace Armada.Server.Recovery
{
    /// <summary>
    /// Compile-time inlined playbook content for <c>pbk_rebase_captain</c>. Delivered
    /// to the rebase-captain mission via <c>SelectedPlaybook</c> in
    /// <c>InlineFullContent</c> mode -- the orchestrator does not need a curated
    /// playbook row for this content because it is shipped inline on every
    /// recovery mission.
    /// </summary>
    public static class RebaseCaptainPlaybookContent
    {
        /// <summary>
        /// Playbook id as exposed on the SelectedPlaybook entry.
        /// </summary>
        public const string PlaybookId = "pbk_rebase_captain";

        /// <summary>
        /// Inline markdown content delivered with the rebase mission. Kept under 4 KB
        /// so it fits inline delivery without truncation.
        /// </summary>
        public const string Markdown =
            "You are receiving a captain branch mid-conflict. Original Brief above. Your job:\n" +
            "1. Inspect the conflict markers already in tree (do NOT rebase -- markers are already there).\n" +
            "2. Resolve them.\n" +
            "3. Run the test suite per the vessel CLAUDE.md.\n" +
            "4. Commit the resolution to the SAME captain branch.\n" +
            "5. Exit success.\n" +
            "\n" +
            "Persona allow-list: claude-opus-4-7 OR gpt-5.5 only.\n";
    }
}
