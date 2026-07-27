namespace Armada.Core.Services
{
    using Armada.Core.Models;

    /// <summary>
    /// Builds Mux CLI command arguments from Armada captain settings.
    /// </summary>
    public static class MuxCommandBuilder
    {
        #region Public-Methods

        /// <summary>
        /// Build arguments for `mux run`.
        /// </summary>
        public static List<string> BuildPrintArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            MuxCaptainOptions? options)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (String.IsNullOrWhiteSpace(prompt)) throw new ArgumentNullException(nameof(prompt));

            List<string> args = new List<string>
            {
                "run"
            };

            AppendCommonOverrides(args, options, model);

            args.Add("--dir");
            args.Add(workingDirectory);

            args.Add("--quiet");

            AppendApprovalArguments(args, options?.ApprovalPolicy);

            return args;
        }

        /// <summary>
        /// Build arguments for probing that the mux CLI is installed.
        /// </summary>
        public static List<string> BuildProbeArguments(
            string? model,
            MuxCaptainOptions? options,
            bool requireTools = true)
        {
            List<string> args = new List<string>
            {
                "--version"
            };

            return args;
        }

        /// <summary>
        /// Build legacy endpoint-list arguments.
        /// </summary>
        public static List<string> BuildEndpointListArguments(string? configDirectory)
        {
            List<string> args = new List<string>
            {
                "endpoint",
                "list",
                "--output-format",
                "json"
            };

            return args;
        }

        /// <summary>
        /// Build legacy endpoint-show arguments.
        /// </summary>
        public static List<string> BuildEndpointShowArguments(string endpointName, string? configDirectory)
        {
            if (String.IsNullOrWhiteSpace(endpointName)) throw new ArgumentNullException(nameof(endpointName));

            List<string> args = new List<string>
            {
                "endpoint",
                "show",
                endpointName.Trim(),
                "--output-format",
                "json"
            };

            return args;
        }

        #endregion

        #region Private-Methods

        private static void AppendCommonOverrides(List<string> args, MuxCaptainOptions? options, string? model)
        {
            if (!String.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model!);
            }
        }

        private static void AppendApprovalArguments(List<string> args, string? approvalPolicy)
        {
            string? normalized = approvalPolicy?.Trim().ToLowerInvariant();
            if (String.IsNullOrEmpty(normalized) || normalized == "auto" || normalized == "autoapprove")
            {
                return;
            }

            if (normalized == "plan")
            {
                args.Add("--mode");
                args.Add("plan");
            }
        }

        #endregion
    }
}
