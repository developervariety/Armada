namespace Armada.Runtimes.Tools
{
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads a file from the filesystem and returns its contents with line numbers.
    /// </summary>
    public class ReadFileTool : IToolExecutor
    {
        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "read_file";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Reads a file from the filesystem and returns its contents with line numbers (like cat -n). "
            + "Supports optional offset and limit parameters to read a specific range of lines.";

        /// <summary>
        /// The JSON Schema object describing the tool's input parameters.
        /// </summary>
        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                file_path = new
                {
                    type = "string",
                    description = "The absolute path to the file to read."
                },
                offset = new
                {
                    type = "integer",
                    description = "The line number to start reading from (1-based). Defaults to 1."
                },
                limit = new
                {
                    type = "integer",
                    description = "The maximum number of lines to read. Defaults to reading the entire file."
                }
            },
            required = new[] { "file_path" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Executes the read_file tool.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The parsed JSON arguments containing file_path, offset, and limit.</param>
        /// <param name="workingDirectory">The current working directory for resolving relative paths.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> containing the file contents with line numbers.</returns>
        public async Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = ToolExecution.CreateTimeoutSource(cancellationToken);
            CancellationToken operationToken = timeoutSource.Token;
            try
            {
                ReadFileArguments request = ToolArgumentParser.Parse<ReadFileArguments>(argumentsJson);
                string filePath = request.FilePath ?? throw new ArgumentException("Required parameter 'file_path' is missing or not a string.");
                string resolvedPath = ResolvePath(filePath, workingDirectory);

                int offset = request.Offset ?? 1;
                int limit = request.Limit ?? -1;
                if (offset < 1 || limit == 0 || limit < -1)
                    throw new ArgumentException("offset must be at least 1 and limit must be positive or omitted.");

                if (!File.Exists(resolvedPath))
                {
                    return new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new { error = "file_not_found", message = $"File not found: {resolvedPath}" })
                    };
                }

                FileInfo fileInfo = new FileInfo(resolvedPath);
                if (fileInfo.Length > Math.Clamp(ToolSafetyLimits.MaxReadFileBytes, 1, 64 * 1024 * 1024))
                {
                    return new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new
                        {
                            error = "file_too_large",
                            message = $"File size ({fileInfo.Length} bytes) exceeds maximum allowed ({ToolSafetyLimits.MaxReadFileBytes} bytes): {resolvedPath}"
                        })
                    };
                }

                string text = await ToolExecution.ReadTextFileAsync(resolvedPath, operationToken).ConfigureAwait(false);
                string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

                int startIndex = Math.Max(0, offset - 1);
                int endIndex = limit > 0 ? Math.Min(lines.Length, startIndex + limit) : lines.Length;

                StringBuilder sb = new StringBuilder();

                for (int i = startIndex; i < endIndex; i++)
                {
                    string lineContent = lines[i].Replace("\r", string.Empty);
                    int lineNumber = i + 1;
                    sb.Append($"{lineNumber,6}\t{lineContent}\n");
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
            catch (UnauthorizedAccessException)
            {
                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { error = "permission_denied", message = "Permission denied when reading the file." })
                };
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { error = "read_error", message = ex.Message })
                };
            }
        }

        #endregion

        #region Private-Methods

        private sealed class ReadFileArguments
        {
            [JsonPropertyName("file_path")]
            public string? FilePath { get; set; }
            public int? Offset { get; set; }
            public int? Limit { get; set; }
        }

        private string ResolvePath(string filePath, string workingDirectory)
        {
            return WorkspacePathPolicy.ResolvePath(filePath, workingDirectory);
        }

        #endregion
    }
}
