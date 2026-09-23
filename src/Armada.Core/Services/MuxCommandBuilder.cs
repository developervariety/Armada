namespace Armada.Core.Services
{
    using Armada.Core.Models;

    /// <summary>
    /// Builds Mux CLI command arguments from Armada captain settings.
    /// </summary>
    public static class MuxCommandBuilder
    {
        #region Public-Members

        /// <summary>
        /// Environment variable Mux reads to select its active config directory. The --config-dir flag
        /// outranks it, and with neither set Mux uses ~/.mux.
        /// </summary>
        public const string ConfigDirectoryEnvironmentVariable = "MUX_CONFIG_DIR";

        /// <summary>
        /// Flag that selects the active Mux config directory (endpoints, settings, skills, hooks).
        /// </summary>
        public const string ConfigDirectoryFlag = "--config-dir";

        /// <summary>
        /// Flag naming the file of MCP servers a `mux print` run loads. Without it `mux print` loads no MCP server.
        /// </summary>
        public const string McpConfigFlag = "--mcp-config";

        /// <summary>
        /// Flag that makes a `mux print` run use only the --mcp-config servers and ignore the config
        /// directory's mcp-servers.json.
        /// </summary>
        public const string StrictMcpConfigFlag = "--strict-mcp-config";

        /// <summary>
        /// File name of the per-launch MCP servers document inside a scoped launch directory.
        /// </summary>
        public const string ScopedMcpConfigFileName = "mux-mcp.json";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the arguments that make a `mux print` run load exactly the MCP servers in one file.
        /// </summary>
        /// <param name="mcpConfigPath">Path of a servers document in the mcp-servers.json shape.</param>
        /// <returns>The --mcp-config and --strict-mcp-config arguments.</returns>
        public static List<string> BuildStrictMcpConfigArguments(string mcpConfigPath)
        {
            if (String.IsNullOrWhiteSpace(mcpConfigPath)) throw new ArgumentNullException(nameof(mcpConfigPath));
            return new List<string> { McpConfigFlag, mcpConfigPath, StrictMcpConfigFlag };
        }

        /// <summary>
        /// Build single-shot arguments for jchristn/Mux (`mux print &lt;prompt&gt;`). Uses the
        /// `print` SUBCOMMAND (not the `--print` flag, which enters the interactive REPL and
        /// fails headless on Console.KeyAvailable). The prompt is passed as the trailing
        /// positional argument. `-w` sets the tool-execution directory, `--yolo` auto-approves
        /// tool calls, and `--config-dir`/`--endpoint` select the OpenAI-compatible backend.
        /// </summary>
        /// <param name="workingDirectory">Tool-execution directory passed as -w.</param>
        /// <param name="prompt">Prompt delivered as the trailing positional argument.</param>
        /// <param name="model">Optional model override.</param>
        /// <param name="finalMessageFilePath">Optional path for --output-last-message.</param>
        /// <param name="options">Captain runtime options selecting the backend.</param>
        /// <param name="showThinking">When true, add --show-thinking so the model's reasoning is
        /// streamed. Mux is the only runtime with a headless reasoning channel.</param>
        /// <returns>Argument list for the Mux CLI.</returns>
        public static List<string> BuildPrintArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            MuxCaptainOptions? options,
            bool showThinking = false)
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
                    args.Add(ConfigDirectoryFlag);
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

            if (showThinking)
            {
                // Mux is the only runtime with a headless reasoning channel, so an interactive caller
                // asking to see the model's thinking is honored here and nowhere else.
                args.Add("--show-thinking");
            }

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
