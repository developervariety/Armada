namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Searches for files matching a glob pattern within a directory tree.
    /// Supports *, **, and ? wildcard patterns.
    /// </summary>
    public class GlobTool : IToolExecutor
    {
        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "glob";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Searches for files matching a glob pattern. "
            + "Supports * (any characters in filename), ** (any path segments), and ? (single character).";

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
                    description = "The glob pattern to match files against (e.g., '**/*.cs', 'src/**/*.json')."
                },
                path = new
                {
                    type = "string",
                    description = "The directory to search in. Defaults to the working directory."
                }
            },
            required = new[] { "pattern" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Executes the glob tool.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The parsed JSON arguments containing pattern and optional path.</param>
        /// <param name="workingDirectory">The current working directory for resolving relative paths.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> containing the matching file paths.</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = ToolExecution.CreateTimeoutSource(cancellationToken);
            try
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                GlobArguments request = ToolArgumentParser.Parse<GlobArguments>(argumentsJson);
                string pattern = request.Pattern ?? throw new ArgumentException("Required parameter 'pattern' is missing or not a string.");
                string searchPath = request.Path ?? workingDirectory;
                string resolvedPath = ResolvePath(searchPath, workingDirectory);

                if (!Directory.Exists(resolvedPath))
                {
                    return Task.FromResult(new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new { error = "directory_not_found", message = $"Directory not found: {resolvedPath}" })
                    });
                }

                Regex regex = GlobToRegex(pattern);

                List<string> matches = new List<string>();
                IEnumerable<string> entries = WorkspacePathPolicy.EnumerateEntries(resolvedPath, timeoutSource.Token, ToolSafetyLimits.MaxEnumeratedEntries);
                foreach (string entry in entries)
                {
                    timeoutSource.Token.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0) continue;

                    string relativePath = Path.GetRelativePath(resolvedPath, entry).Replace('\\', '/');

                    if (regex.IsMatch(relativePath))
                    {
                        matches.Add(relativePath);
                    }
                }

                matches.Sort(StringComparer.Ordinal);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"Found {matches.Count} matching file(s):");

                foreach (string match in matches)
                {
                    sb.AppendLine(match);
                }

                return Task.FromResult(new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = true,
                    Content = ToolExecution.LimitOutput(sb.ToString())
                });
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(ToolExecution.Cancelled(toolCallId, cancellationToken));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { error = "glob_error", message = ex.Message })
                });
            }
        }

        #endregion

        #region Private-Methods

        private Regex GlobToRegex(string pattern)
        {
            string normalized = pattern.Replace('\\', '/');
            StringBuilder regexPattern = new StringBuilder("^");
            int i = 0;

            while (i < normalized.Length)
            {
                char c = normalized[i];

                if (c == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
                {
                    // ** matches any path segments
                    if (i + 2 < normalized.Length && normalized[i + 2] == '/')
                    {
                        regexPattern.Append("(.+/)?");
                        i += 3;
                    }
                    else
                    {
                        regexPattern.Append(".*");
                        i += 2;
                    }
                }
                else if (c == '*')
                {
                    // * matches any characters except /
                    regexPattern.Append("[^/]*");
                    i++;
                }
                else if (c == '?')
                {
                    // ? matches single character except /
                    regexPattern.Append("[^/]");
                    i++;
                }
                else if (c == '.')
                {
                    regexPattern.Append("\\.");
                    i++;
                }
                else if (c == '{')
                {
                    regexPattern.Append("(");
                    i++;
                }
                else if (c == '}')
                {
                    regexPattern.Append(")");
                    i++;
                }
                else if (c == ',')
                {
                    regexPattern.Append("|");
                    i++;
                }
                else
                {
                    regexPattern.Append(Regex.Escape(c.ToString()));
                    i++;
                }
            }

            regexPattern.Append("$");
            return new Regex(regexPattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private sealed class GlobArguments
        {
            public string? Pattern { get; set; }
            public string? Path { get; set; }
        }

        private string ResolvePath(string filePath, string workingDirectory)
        {
            return WorkspacePathPolicy.ResolvePath(filePath, workingDirectory);
        }

        #endregion
    }
}
