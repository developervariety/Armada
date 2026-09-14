namespace Armada.Runtimes.Tools
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Writes content to a file, creating parent directories as needed.
    /// Preserves existing line ending style for existing files, uses platform default for new files.
    /// </summary>
    public class WriteFileTool : IToolExecutor
    {
        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "write_file";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Writes content to a file. Creates parent directories if they do not exist. "
            + "Preserves original line ending style for existing files; uses platform default for new files.";

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
                    description = "The absolute path to the file to write."
                },
                content = new
                {
                    type = "string",
                    description = "The content to write to the file."
                }
            },
            required = new[] { "file_path", "content" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Executes the write_file tool.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The parsed JSON arguments containing file_path and content.</param>
        /// <param name="workingDirectory">The current working directory for resolving relative paths.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> containing the write operation result.</returns>
        public async Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = ToolExecution.CreateTimeoutSource(cancellationToken);
            CancellationToken operationToken = timeoutSource.Token;
            try
            {
                WriteFileArguments request = ToolArgumentParser.Parse<WriteFileArguments>(argumentsJson);
                string filePath = request.FilePath ?? throw new ArgumentException("Required parameter 'file_path' is missing or not a string.");
                string content = request.Content ?? throw new ArgumentException("Required parameter 'content' is missing or not a string.");
                ToolExecution.EnsureInputSize(content, "content");
                string resolvedPath = ResolvePath(filePath, workingDirectory);

                string lineEnding = Environment.NewLine;

                if (File.Exists(resolvedPath))
                {
                    lineEnding = await ToolExecution.DetectLineEndingAsync(resolvedPath, operationToken).ConfigureAwait(false);
                }

                string? parentDir = Path.GetDirectoryName(resolvedPath);
                if (parentDir != null && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                string normalizedContent = content.Replace("\r\n", "\n").Replace("\r", "\n");
                string outputContent = normalizedContent.Replace("\n", lineEnding);

                await ToolExecution.AtomicWriteTextFileAsync(resolvedPath, outputContent, operationToken).ConfigureAwait(false);

                int lineCount = normalizedContent.Split('\n').Length;

                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = true,
                    Content = JsonSerializer.Serialize(new
                    {
                        success = true,
                        file_path = resolvedPath,
                        line_count = lineCount
                    })
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
                    Content = JsonSerializer.Serialize(new { error = "permission_denied", message = "Permission denied when writing the file." })
                };
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { error = "write_error", message = ex.Message })
                };
            }
        }

        #endregion

        #region Private-Methods

        private sealed class WriteFileArguments
        {
            [JsonPropertyName("file_path")]
            public string? FilePath { get; set; }
            public string? Content { get; set; }
        }

        private string ResolvePath(string filePath, string workingDirectory)
        {
            return WorkspacePathPolicy.ResolvePath(filePath, workingDirectory);
        }

        #endregion
    }
}
