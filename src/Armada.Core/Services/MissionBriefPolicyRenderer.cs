namespace Armada.Core.Services
{
    using System;
    using System.Text;

    /// <summary>
    /// Renders the authorization and hard-limit module of a captain brief. Every brief path -- operator
    /// dispatch, the objective scheduler, a retry and an autonomous rescue -- writes its instruction file
    /// through <see cref="MissionService.GenerateClaudeMdAsync"/>, which calls this renderer, so the owner's
    /// authorization reaches every captain in the same words. The owner policy is reproduced verbatim: a
    /// paraphrase or a generic summary loses exactly the scope a captain needs to recognize work as
    /// permitted. The hard limits are fixed text that no owner policy can relax.
    /// </summary>
    public static class MissionBriefPolicyRenderer
    {
        #region Public-Members

        /// <summary>Ledger module name of the rendered section. The budget backstop never elides it.</summary>
        public const string ModuleName = "mission.authorization_policy";

        /// <summary>Heading that opens the rendered section.</summary>
        public const string Heading = "## Authorization and Hard Limits";

        /// <summary>
        /// The limits that hold for every mission whatever the owner policy says. One definition, rendered
        /// into every brief.
        /// </summary>
        public const string HardLimits =
            "### Hard Limits (always apply; owner authorization never relaxes them)\n" +
            "- Secrets: never print, log, commit, or copy credentials, API keys, tokens, private keys, passwords or license material into any file, commit message, report or signal.\n" +
            "- Tenant isolation: never read, write or disclose another tenant's data, missions or repositories.\n" +
            "- Protected paths: never modify a path the vessel protects; propose the change in your final report instead.\n" +
            "- Destructive operations: never force-push, delete branches or tags, rewrite shared history, drop data, or run a destructive command outside your own dock.\n";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Render the authorization and hard-limit section for one brief.
        /// </summary>
        /// <param name="authorizationPolicy">The owner authorization policy that applies to the vessel, or null when none is recorded.</param>
        /// <returns>The section text, always ending in a newline.</returns>
        public static string Render(string? authorizationPolicy)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(Heading).Append('\n').Append('\n');

            if (String.IsNullOrWhiteSpace(authorizationPolicy))
            {
                builder.Append("No project authorization policy is recorded for this vessel. Work within the mission scope and the hard limits below.\n\n");
            }
            else
            {
                builder.Append("### Owner Authorization (verbatim project policy)\n");
                builder.Append("The owner of this project recorded the policy below. It states what engineering is authorized here. ");
                builder.Append("Work the policy permits is authorized work: do not refuse it, and do not reduce it to a narrower task. ");
                builder.Append("If a request still conflicts with a limit you cannot cross, say which limit and why in your final report.\n\n");
                builder.Append(authorizationPolicy.Trim()).Append('\n').Append('\n');
            }

            builder.Append(HardLimits);
            return builder.ToString();
        }

        #endregion
    }
}
