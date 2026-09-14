namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Lists files and directories at a given path with type indicators.
    /// Directories are listed first, then files, each sorted alphabetically.
    /// </summary>
    public class ListDirectoryTool : IToolExecutor
    {
        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "list_directory";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Lists files and directories at a given path. "
            + "Directories are listed first (marked [DIR]), then files (marked [FILE]), sorted alphabetically within each group.";

        /// <summary>
        /// The JSON Schema object describing the tool's input parameters.
        /// </summary>
        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "The absolute path to the directory to list."
                }
            },
            required = new[] { "path" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Executes the list_directory tool.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The parsed JSON arguments containing path.</param>
        /// <param name="workingDirectory">The current working directory for resolving relative paths.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> containing the directory listing.</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = ToolExecution.CreateTimeoutSource(cancellationToken);
            try
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                ListDirectoryArguments request = ToolArgumentParser.Parse<ListDirectoryArguments>(argumentsJson);
                string path = request.Path ?? throw new ArgumentException("Required parameter 'path' is missing or not a string.");
                string resolvedPath = ResolvePath(path, workingDirectory);

                if (!Directory.Exists(resolvedPath))
                {
                    return Task.FromResult(new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new { error = "directory_not_found", message = $"Directory not found: {resolvedPath}" })
                    });
                }

                List<string> directories = new List<string>();
                List<string> files = new List<string>();
                foreach (string entry in Directory.EnumerateFileSystemEntries(resolvedPath))
                {
                    timeoutSource.Token.ThrowIfCancellationRequested();
                    string validated = WorkspacePathPolicy.ResolvePath(entry, workingDirectory);
                    FileAttributes attributes = File.GetAttributes(validated);
                    string name = Path.GetFileName(validated);
                    if ((attributes & FileAttributes.Directory) != 0) directories.Add(name);
                    else files.Add(name);
                    if (directories.Count + files.Count > ToolSafetyLimits.MaxEnumeratedEntries)
                        throw new WorkspaceEnumerationLimitException(ToolSafetyLimits.MaxEnumeratedEntries);
                }
                directories.Sort(StringComparer.Ordinal);
                files.Sort(StringComparer.Ordinal);

                StringBuilder sb = new StringBuilder();

                foreach (string dir in directories)
                {
                    sb.AppendLine($"[DIR]  {dir}");
                }

                foreach (string file in files)
                {
                    sb.AppendLine($"[FILE] {file}");
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
            catch (UnauthorizedAccessException)
            {
                return Task.FromResult(new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { error = "permission_denied", message = "Permission denied when listing the directory." })
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { error = "list_error", message = ex.Message })
                });
            }
        }

        #endregion

        #region Private-Methods

        private sealed class ListDirectoryArguments
        {
            public string? Path { get; set; }
        }

        private string ResolvePath(string filePath, string workingDirectory)
        {
            return WorkspacePathPolicy.ResolvePath(filePath, workingDirectory);
        }

        #endregion
    }
}
