namespace Armada.Runtimes.Tools
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reads metadata about a file or directory (size, timestamps, attributes).
    /// </summary>
    public class FileMetadataTool : IToolExecutor
    {
        #region Public-Members

        /// <summary>
        /// The unique name of this tool.
        /// </summary>
        public string Name => "file_metadata";

        /// <summary>
        /// A human-readable description of what this tool does.
        /// </summary>
        public string Description => "Returns metadata about a file or directory including size, creation time, "
            + "last modified time, last access time, and attributes. Works for both files and directories.";

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
                    description = "The path to the file or directory."
                }
            },
            required = new[] { "path" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Executes the file_metadata tool.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The parsed JSON arguments containing path.</param>
        /// <param name="workingDirectory">The current working directory for resolving relative paths.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> containing the metadata.</returns>
        public Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource = ToolExecution.CreateTimeoutSource(cancellationToken);
            try
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                MetadataArguments request = ToolArgumentParser.Parse<MetadataArguments>(argumentsJson);
                string path = request.Path ?? throw new ArgumentException("Required parameter 'path' is missing or not a string.");
                string resolvedPath = ResolvePath(path, workingDirectory);

                if (File.Exists(resolvedPath))
                {
                    FileInfo info = new FileInfo(resolvedPath);

                    string content = JsonSerializer.Serialize(new
                    {
                        success = true,
                        path = resolvedPath,
                        type = "file",
                        size_bytes = info.Length,
                        created_utc = info.CreationTimeUtc.ToString("o"),
                        modified_utc = info.LastWriteTimeUtc.ToString("o"),
                        accessed_utc = info.LastAccessTimeUtc.ToString("o"),
                        is_readonly = info.IsReadOnly,
                        extension = info.Extension
                    });

                    return Task.FromResult(new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = true,
                        Content = content
                    });
                }

                if (Directory.Exists(resolvedPath))
                {
                    DirectoryInfo info = new DirectoryInfo(resolvedPath);

                    int fileCount = 0;
                    int dirCount = 0;
                    foreach (string child in Directory.EnumerateFileSystemEntries(resolvedPath))
                    {
                        timeoutSource.Token.ThrowIfCancellationRequested();
                        string validatedChild = WorkspacePathPolicy.ResolvePath(child, workingDirectory);
                        FileAttributes attributes = File.GetAttributes(validatedChild);
                        if ((attributes & FileAttributes.Directory) != 0) dirCount++;
                        else fileCount++;
                        if (fileCount + dirCount > ToolSafetyLimits.MaxEnumeratedEntries)
                            throw new WorkspaceEnumerationLimitException(ToolSafetyLimits.MaxEnumeratedEntries);
                    }

                    string content = JsonSerializer.Serialize(new
                    {
                        success = true,
                        path = resolvedPath,
                        type = "directory",
                        created_utc = info.CreationTimeUtc.ToString("o"),
                        modified_utc = info.LastWriteTimeUtc.ToString("o"),
                        accessed_utc = info.LastAccessTimeUtc.ToString("o"),
                        file_count = fileCount,
                        directory_count = dirCount
                    });

                    return Task.FromResult(new ToolResult
                    {
                        ToolCallId = toolCallId,
                        Success = true,
                        Content = ToolExecution.LimitOutput(content)
                    });
                }

                return Task.FromResult(new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { success = false, error = "not_found", message = $"Path not found: {resolvedPath}" })
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
                    Content = JsonSerializer.Serialize(new { success = false, error = "permission_denied", message = "Permission denied when reading metadata." })
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = false,
                    Content = JsonSerializer.Serialize(new { success = false, error = "metadata_error", message = ex.Message })
                });
            }
        }

        #endregion

        #region Private-Methods

        private sealed class MetadataArguments
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
