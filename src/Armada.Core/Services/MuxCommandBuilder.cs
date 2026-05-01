namespace Armada.Core.Services
{
    using System.Globalization;
    using Armada.Core.Models;

    /// <summary>
    /// Builds Mux CLI command arguments from Armada captain settings.
    /// </summary>
    public static class MuxCommandBuilder
    {
        #region Public-Methods

        /// <summary>
        /// Build arguments for `mux print`.
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
                "print"
            };

            AppendConfigDirectory(args, options?.ConfigDirectory);

            args.Add("--output-format");
            args.Add("jsonl");

            if (!String.IsNullOrWhiteSpace(finalMessageFilePath))
            {
                args.Add("--output-last-message");
                args.Add(finalMessageFilePath!);
            }

            AppendApprovalArguments(args, options?.ApprovalPolicy);
            AppendCommonOverrides(args, options, model);

            args.Add("--working-directory");
            args.Add(workingDirectory);
            args.Add(prompt);

            return args;
        }

        /// <summary>
        /// Build arguments for `mux probe`.
        /// </summary>
        public static List<string> BuildProbeArguments(
            string? model,
            MuxCaptainOptions? options,
            bool requireTools = true)
        {
            List<string> args = new List<string>
            {
                "probe",
                "--output-format",
                "json"
            };

            AppendConfigDirectory(args, options?.ConfigDirectory);

            if (requireTools)
            {
                args.Add("--require-tools");
            }

            AppendCommonOverrides(args, options, model);
            return args;
        }

        /// <summary>
        /// Build arguments for `mux endpoint list`.
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

            AppendConfigDirectory(args, configDirectory);
            return args;
        }

        /// <summary>
        /// Build arguments for `mux endpoint show`.
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

            AppendConfigDirectory(args, configDirectory);
            return args;
        }

        #endregion

        #region Private-Methods

        private static void AppendConfigDirectory(List<string> args, string? configDirectory)
        {
            if (!String.IsNullOrWhiteSpace(configDirectory))
            {
                args.Add("--config-dir");
                args.Add(configDirectory.Trim());
            }
        }

        private static void AppendCommonOverrides(List<string> args, MuxCaptainOptions? options, string? model)
        {
            if (!String.IsNullOrWhiteSpace(options?.Endpoint))
            {
                args.Add("--endpoint");
                args.Add(options.Endpoint!);
            }

            if (!String.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model!);
            }

            if (!String.IsNullOrWhiteSpace(options?.BaseUrl))
            {
                args.Add("--base-url");
                args.Add(options.BaseUrl!);
            }

            if (!String.IsNullOrWhiteSpace(options?.AdapterType))
            {
                args.Add("--adapter-type");
                args.Add(options.AdapterType!);
            }

            if (options?.Temperature.HasValue == true)
            {
                args.Add("--temperature");
                args.Add(options.Temperature.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (options?.MaxTokens.HasValue == true)
            {
                args.Add("--max-tokens");
                args.Add(options.MaxTokens.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (!String.IsNullOrWhiteSpace(options?.SystemPromptPath))
            {
                args.Add("--system-prompt");
                args.Add(options.SystemPromptPath!);
            }
        }

        private static void AppendApprovalArguments(List<string> args, string? approvalPolicy)
        {
            string? normalized = approvalPolicy?.Trim().ToLowerInvariant();
            if (String.IsNullOrEmpty(normalized) || normalized == "auto" || normalized == "autoapprove")
            {
                args.Add("--yolo");
                return;
            }

            args.Add("--approval-policy");
            args.Add(normalized);
        }

        #endregion
    }
}
