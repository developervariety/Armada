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
        private const int _DefaultPageLines = 200;
        private const int _DefaultPageCharacters = 24_000;
        private const int _PaginationFooterReserve = 512;

        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "read_file";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Reads a file from the filesystem and returns its contents with line numbers (like cat -n). "
            + "Defaults to pages of at most 200 lines and 24,000 characters with continuation instructions. "
            + "CLAUDE.md, AGENTS.md, CODEX.md, CURSOR.md, GEMINI.md and MUX.md request complete reads by default within the input and output byte limits. "
            + "Use a positive limit for a line range or limit=-1 to read the full file.";

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
                    description = "The line number to start reading from (1-based). Defaults to 1; values below 1 are invalid."
                },
                limit = new
                {
                    type = "integer",
                    description = "The maximum number of lines to read. Omit for a bounded page of at most 200 lines and 24,000 characters. Instruction files default to the full file within the input and output byte limits. Use -1 for an explicit full read; zero and values below -1 are invalid."
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
                bool boundedDefault = !request.Limit.HasValue && !IsInstructionFile(resolvedPath);
                int limit = request.Limit ?? (boundedDefault ? _DefaultPageLines : -1);
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

                int startIndex = offset - 1;
                int endIndex = limit > 0 ? (int)Math.Min(lines.Length, (long)startIndex + limit) : lines.Length;

                StringBuilder sb = new StringBuilder();
                int nextIndex = startIndex;
                bool oversizedLine = false;

                for (int i = startIndex; i < endIndex; i++)
                {
                    string lineContent = lines[i].Replace("\r", string.Empty);
                    string numberedLine = $"{i + 1,6}\t{lineContent}\n";
                    if (boundedDefault && (long)sb.Length + numberedLine.Length > _DefaultPageCharacters - _PaginationFooterReserve)
                    {
                        oversizedLine = numberedLine.Length > _DefaultPageCharacters - _PaginationFooterReserve;
                        break;
                    }

                    sb.Append(numberedLine);
                    nextIndex = i + 1;
                }

                if (boundedDefault && nextIndex < lines.Length)
                {
                    sb.Append($"[read_file: output paged; {lines.Length} total lines. Continue with offset={nextIndex + 1}. ");
                    sb.Append(oversizedLine
                        ? "The next line exceeds the default character budget; request an explicit limit=1 to read it at this offset, or use grep for a smaller result."
                        : "Omitted limit defaults to at most 200 lines and 24,000 characters; an explicit limit selects a range.");
                    sb.Append("]\n");
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

        private static bool IsInstructionFile(string filePath)
        {
            string name = Path.GetFileName(filePath);
            return String.Equals(name, "CLAUDE.md", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "AGENTS.md", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "CODEX.md", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "CURSOR.md", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "GEMINI.md", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "MUX.md", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
