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
    /// Each edit is validated against the content the previous edits produced, and the file is written only
    /// when every edit is valid.
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
            + "Each edit applies to the content the previous edits produced, and its old_string must be non-empty and match "
            + "exactly one location there. The file is written only when every edit is valid.";

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
                string lfContent = ExactTextMatch.NormalizeLineEndings(originalContent);

                // Each edit is checked against the content the edits before it produced, which is the content it
                // is applied to: an edit unique in the original can be missing or ambiguous after earlier edits.
                // Nothing is written unless every edit passes.
                string workingContent = lfContent;
                for (int i = 0; i < edits.Count; i++)
                {
                    string lfOldString = ExactTextMatch.NormalizeLineEndings(edits[i].OldString);
                    string lfNewString = ExactTextMatch.NormalizeLineEndings(edits[i].NewString);

                    if (lfOldString.Length == 0)
                    {
                        return Failure(toolCallId, "empty_old_string", i,
                            $"Edit at index {i}: old_string must not be empty; include the text to replace.");
                    }

                    ExactTextMatch match = ExactTextMatch.Find(workingContent, lfOldString, operationToken);

                    if (match.MatchCount == 0)
                    {
                        if (i > 0 && lfContent.Contains(lfOldString, StringComparison.Ordinal))
                        {
                            return Failure(toolCallId, "edit_conflict", i,
                                $"Edit at index {i}: old_string no longer found after applying previous edits.");
                        }

                        return Failure(toolCallId, "old_string_not_found", i,
                            $"Edit at index {i}: old_string was not found in the file content.");
                    }

                    if (!match.IsUnique)
                    {
                        string after = i == 0 ? "in the file content" : "after applying previous edits";
                        return Failure(toolCallId, "ambiguous_match", i,
                            $"Edit at index {i}: old_string matches {match.MatchCount} locations {after}. Provide more context to uniquely identify the target.");
                    }

                    workingContent = ExactTextMatch.Replace(workingContent, match.FirstPosition, lfOldString.Length, lfNewString);
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

        private static ToolResult Failure(string toolCallId, string error, int editIndex, string message)
        {
            return new ToolResult
            {
                ToolCallId = toolCallId,
                Success = false,
                Content = JsonSerializer.Serialize(new
                {
                    success = false,
                    error,
                    edit_index = editIndex,
                    message
                })
            };
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
