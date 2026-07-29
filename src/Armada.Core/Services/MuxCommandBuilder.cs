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
        /// Build single-shot arguments for jchristn/Mux (`mux print &lt;prompt&gt;`). Uses the
        /// `print` SUBCOMMAND (not the `--print` flag, which enters the interactive REPL and
        /// fails headless on Console.KeyAvailable). The prompt is passed as the trailing
        /// positional argument. `-w` sets the tool-execution directory, `--yolo` auto-approves
        /// tool calls, and `--config-dir`/`--endpoint` select the OpenAI-compatible backend.
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

            if (options != null)
            {
                if (!String.IsNullOrWhiteSpace(options.ConfigDirectory))
                {
                    args.Add("--config-dir");
                    args.Add(options.ConfigDirectory!);
                }
                if (!String.IsNullOrWhiteSpace(options.Endpoint))
                {
                    args.Add("--endpoint");
                    args.Add(options.Endpoint!);
                }
            }

            AppendCommonOverrides(args, options, model);

            args.Add("--output-format");
            args.Add("jsonl");

            args.Add("-w");
            args.Add(workingDirectory);

            args.Add("--yolo");

            if (!String.IsNullOrWhiteSpace(finalMessageFilePath))
            {
                args.Add("--output-last-message");
                args.Add(finalMessageFilePath!);
            }

            // jchristn/Mux takes the prompt as the trailing positional argument.
            args.Add(prompt);

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

        #endregion
    }
}
