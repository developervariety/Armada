namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Performs multiple sequential string replacements in a single file atomically.
    /// All edits are validated against the original content before any are applied.
    /// </summary>
    public class MultiEditTool : IToolExecutor
    {
        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "multi_edit";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Performs multiple sequential string replacements in a single file. "
            + "All edits are validated before any are applied. Each edit modifies the working content for subsequent edits.";

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
                    description = "The absolute path to the file to edit."
                },
                edits = new
                {
                    type = "array",
                    description = "An array of edit operations to apply sequentially.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            old_string = new
                            {
                                type = "string",
                                description = "The exact string to find and replace."
                            },
                            new_string = new
                            {
                                type = "string",
                                description = "The replacement string."
                            }
                        },
                        required = new[] { "old_string", "new_string" }
                    }
                }
            },
            required = new[] { "file_path", "edits" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Executes the multi_edit tool.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The parsed JSON arguments containing file_path and edits array.</param>
        /// <param name="workingDirectory">The current working directory for resolving relative paths.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> containing the edit result or error details.</returns>
        public async Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = ToolExecution.CreateTimeoutSource(cancellationToken);
            CancellationToken operationToken = timeoutSource.Token;
            try
            {
                MultiEditArguments request = ToolArgumentParser.Parse<MultiEditArguments>(argumentsJson);
                string filePath = request.FilePath ?? throw new ArgumentException("Required parameter 'file_path' is missing or not a string.");
                string resolvedPath = ResolvePath(filePath, workingDirectory);

                if (!File.Exists(resolvedPath))
                {
                    return new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new { success = false, error = "file_not_found", message = $"File not found: {resolvedPath}" })
                    };
                }

                if (request.Edits == null)
                {
                    return new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = false,
                        Content = JsonSerializer.Serialize(new { success = false, error = "invalid_parameter", message = "Parameter 'edits' is required and must be an array." })
                    };
                }

                List<EditOperation> edits = new List<EditOperation>();
                int editIndex = 0;
                foreach (EditRequest editRequest in request.Edits)
                {
                    string oldString = editRequest.OldString ?? throw new ArgumentException("Edit at index " + editIndex + ": old_string is required.");
                    string newString = editRequest.NewString ?? throw new ArgumentException("Edit at index " + editIndex + ": new_string is required.");
                    ToolExecution.EnsureInputSize(oldString, "old_string");
                    ToolExecution.EnsureInputSize(newString, "new_string");
                    edits.Add(new EditOperation { OldString = oldString, NewString = newString });
                    editIndex++;
                }

                string originalContent = await ToolExecution.ReadTextFileAsync(resolvedPath, operationToken).ConfigureAwait(false);
                string lineEnding = DetectLineEnding(originalContent);
                string lfContent = originalContent.Replace("\r\n", "\n").Replace("\r", "\n");

                // Validate all edits against original content
                for (int i = 0; i < edits.Count; i++)
                {
                    string lfOldString = edits[i].OldString.Replace("\r\n", "\n").Replace("\r", "\n");
                    int matchCount = CountOccurrences(lfContent, lfOldString);

                    if (matchCount == 0)
                    {
                        return new ToolResult
                        {
                            ToolCallId = toolCallId,
                            Success = false,
                            Content = JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "old_string_not_found",
                                edit_index = i,
                                message = $"Edit at index {i}: old_string was not found in the original file content."
                            })
                        };
                    }

                    if (matchCount > 1)
                    {
                        return new ToolResult
                        {
                            ToolCallId = toolCallId,
                            Success = false,
                            Content = JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "ambiguous_match",
                                edit_index = i,
                                message = $"Edit at index {i}: old_string matches {matchCount} locations. Provide more context to uniquely identify the target."
                            })
                        };
                    }
                }

                // Apply all edits sequentially
                string workingContent = lfContent;
                for (int i = 0; i < edits.Count; i++)
                {
                    string lfOldString = edits[i].OldString.Replace("\r\n", "\n").Replace("\r", "\n");
                    string lfNewString = edits[i].NewString.Replace("\r\n", "\n").Replace("\r", "\n");

                    int pos = workingContent.IndexOf(lfOldString, StringComparison.Ordinal);
                    if (pos < 0)
                    {
                        return new ToolResult
                        {
                            ToolCallId = toolCallId,
                            Success = false,
                            Content = JsonSerializer.Serialize(new
                            {
                                success = false,
                                error = "edit_conflict",
                                edit_index = i,
                                message = $"Edit at index {i}: old_string no longer found after applying previous edits."
                            })
                        };
                    }

                    workingContent = workingContent.Substring(0, pos)
                        + lfNewString
                        + workingContent.Substring(pos + lfOldString.Length);
                }

                string outputContent = workingContent.Replace("\n", lineEnding);
                ToolExecution.EnsureInputSize(outputContent, "result");
                await ToolExecution.AtomicWriteTextFileAsync(resolvedPath, outputContent, operationToken).ConfigureAwait(false);

                int newLineCount = workingContent.Split('\n').Length;

                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = true,
                    Content = JsonSerializer.Serialize(new
                    {
                        success = true,
                        file_path = resolvedPath,
                        edits_applied = edits.Count,
                        new_line_count = newLineCount
                    })
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
                    Content = JsonSerializer.Serialize(new { success = false, error = "multi_edit_error", message = ex.Message })
                };
            }
        }

        #endregion

        #region Private-Members

        private class EditOperation
        {
            public string OldString { get; set; } = string.Empty;
            public string NewString { get; set; } = string.Empty;
        }

        #endregion

        #region Private-Methods

        private int CountOccurrences(string content, string search)
        {
            int count = 0;
            int index = 0;

            while (index < content.Length)
            {
                int found = content.IndexOf(search, index, StringComparison.Ordinal);
                if (found < 0) break;
                count++;
                index = found + 1;
            }

            return count;
        }

        private string DetectLineEnding(string content)
        {
            int crlfIndex = content.IndexOf("\r\n", StringComparison.Ordinal);
            int lfIndex = content.IndexOf("\n", StringComparison.Ordinal);
            int crIndex = content.IndexOf("\r", StringComparison.Ordinal);

            if (crlfIndex >= 0 && (crlfIndex <= lfIndex || lfIndex < 0))
            {
                return "\r\n";
            }

            if (lfIndex >= 0)
            {
                return "\n";
            }

            if (crIndex >= 0)
            {
                return "\r";
            }

            return Environment.NewLine;
        }

        private sealed class MultiEditArguments
        {
            [JsonPropertyName("file_path")]
            public string? FilePath { get; set; }
            public List<EditRequest>? Edits { get; set; }
        }

        private sealed class EditRequest
        {
            [JsonPropertyName("old_string")]
            public string? OldString { get; set; }
            [JsonPropertyName("new_string")]
            public string? NewString { get; set; }
        }

        private string ResolvePath(string filePath, string workingDirectory)
        {
            return WorkspacePathPolicy.ResolvePath(filePath, workingDirectory);
        }

        #endregion
    }
}
