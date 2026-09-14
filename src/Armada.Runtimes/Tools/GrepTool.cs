namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Searches files recursively for lines matching a regular expression pattern.
    /// Returns matching lines with file path, line number, and content.
    /// </summary>
    public class GrepTool : IToolExecutor
    {
        #region Private-Members

        private const int _MaxMatches = 100;

        #endregion

        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "grep";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Searches files recursively for lines matching a regular expression. "
            + "Returns matching lines with file path and line number. Limited to the first 100 matches.";

        /// <summary>
        /// The JSON Schema object describing the tool's input parameters.
        /// </summary>
        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                pattern = new
                {
                    type = "string",
                    description = "The regular expression pattern to search for."
                },
                path = new
                {
                    type = "string",
                    description = "The directory to search in. Defaults to the working directory."
                },
                include = new
                {
                    type = "string",
                    description = "A glob pattern to filter which files to search (e.g., '*.cs', '*.json')."
                }
            },
            required = new[] { "pattern" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Executes the grep tool.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The parsed JSON arguments containing pattern, optional path, and optional include.</param>
        /// <param name="workingDirectory">The current working directory for resolving relative paths.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> containing the matching lines.</returns>
        public async Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = ToolExecution.CreateTimeoutSource(cancellationToken);
            try
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                GrepArguments request = ToolArgumentParser.Parse<GrepArguments>(argumentsJson);
                string pattern = request.Pattern ?? throw new ArgumentException("Required parameter 'pattern' is missing or not a string.");
                string searchPath = request.Path ?? workingDirectory;
                string? include = request.Include;
                string resolvedPath = ResolvePath(searchPath, workingDirectory);

                if (!Directory.Exists(resolvedPath))
                {
                    return new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new { error = "directory_not_found", message = $"Directory not found: {resolvedPath}" })
                    };
                }

                Regex regex;
                try
                {
                    regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException ex)
                {
                    return new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new { error = "invalid_regex", message = $"Invalid regular expression: {ex.Message}" })
                    };
                }

                string fileFilter = include ?? "*";
                IEnumerable<string> files = WorkspacePathPolicy.EnumerateEntries(resolvedPath, timeoutSource.Token, ToolSafetyLimits.MaxEnumeratedEntries)
                    .Where(path => (File.GetAttributes(path) & FileAttributes.Directory) == 0)
                    .Where(path => FileNameMatches(Path.GetFileName(path), fileFilter));

                StringBuilder sb = new StringBuilder();
                int matchCount = 0;

                foreach (string filePath in files)
                {
                    timeoutSource.Token.ThrowIfCancellationRequested();
                    if (matchCount >= _MaxMatches) break;

                    string text = await ToolExecution.ReadTextFileAsync(filePath, timeoutSource.Token).ConfigureAwait(false);
                    string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                    string relativePath = Path.GetRelativePath(resolvedPath, filePath).Replace('\\', '/');

                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        timeoutSource.Token.ThrowIfCancellationRequested();
                        if (matchCount >= _MaxMatches) break;
                        if (regex.IsMatch(lines[lineIndex]))
                        {
                            int lineNumber = lineIndex + 1;
                            sb.AppendLine($"{relativePath}:{lineNumber}: {lines[lineIndex]}");
                            matchCount++;
                        }
                    }
                }

                if (matchCount == 0)
                {
                    sb.AppendLine("No matches found.");
                }
                else if (matchCount >= _MaxMatches)
                {
                    sb.AppendLine($"(output truncated at {_MaxMatches} matches)");
                }

                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = true,
                    Content = ToolExecution.LimitOutput(sb.ToString())
                };
            }
            catch (OperationCanceledException)
            {
                return ToolExecution.Cancelled(toolCallId, cancellationToken);
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { error = "grep_error", message = ex.Message })
                };
            }
        }

        #endregion

        #region Private-Methods

        private sealed class GrepArguments
        {
            public string? Pattern { get; set; }
            public string? Path { get; set; }
            public string? Include { get; set; }
        }

        private string ResolvePath(string filePath, string workingDirectory)
        {
            return WorkspacePathPolicy.ResolvePath(filePath, workingDirectory);
        }

        private static bool FileNameMatches(string fileName, string pattern)
        {
            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }

        #endregion
    }
}
