namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Builds the argument list for a headless <c>opencode run</c> invocation. Kept separate from the
    /// runtime adapter so the argument shape can be unit tested without launching a process, mirroring
    /// <see cref="MuxCommandBuilder"/>.
    /// </summary>
    public static class OpenCodeCommandBuilder
    {
        #region Public-Methods

        /// <summary>
        /// Build arguments for <c>opencode run</c> in JSONL-event mode.
        /// </summary>
        /// <param name="workingDirectory">Directory to run in (passed as <c>--dir</c>).</param>
        /// <param name="prompt">The message/prompt to send.</param>
        /// <param name="model">Optional <c>provider/model</c> identifier; omitted when null/blank.</param>
        /// <param name="variant">Optional reasoning variant (<c>minimal</c>/<c>high</c>); omitted when null/blank.</param>
        /// <param name="showThinking">When true, add <c>--thinking</c>.</param>
        /// <param name="autoApprove">When true, add <c>--auto</c> to auto-approve non-denied permissions.</param>
        /// <returns>The ordered argument list.</returns>
        public static List<string> BuildRunArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? variant,
            bool showThinking,
            bool autoApprove)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (String.IsNullOrWhiteSpace(prompt)) throw new ArgumentNullException(nameof(prompt));

            List<string> args = new List<string>
            {
                "run",
                "--format",
                "json"
            };

            if (!String.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model!.Trim());
            }

            if (!String.IsNullOrWhiteSpace(variant))
            {
                args.Add("--variant");
                args.Add(variant!.Trim());
            }

            if (showThinking)
            {
                args.Add("--thinking");
            }

            if (autoApprove)
            {
                args.Add("--auto");
            }

            args.Add("--dir");
            args.Add(workingDirectory);

            args.Add(prompt);

            return args;
        }

        #endregion
    }
}
