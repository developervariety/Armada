namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using Armada.Core.Harbor;

    /// <summary>Writes or removes bounded files inside an owned checkout.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private void WriteText(HarborWorkspaceOperationRequest request, string checkout, HarborWorkspaceOperationResult result, CancellationToken token)
        {
            if (request.Text == null) throw Failure("workspace_text_missing", "The file text is required.");
            WriteFile(request, checkout, new UTF8Encoding(false, true).GetBytes(request.Text), result, token);
        }

        private void WriteBytes(HarborWorkspaceOperationRequest request, string checkout, HarborWorkspaceOperationResult result, CancellationToken token)
        {
            if (request.BytesBase64 == null) throw Failure("workspace_bytes_missing", "The file bytes are required.");
            byte[] content;
            try { content = Convert.FromBase64String(request.BytesBase64); }
            catch (FormatException exception) { throw Failure("workspace_bytes_invalid", "The file bytes are not valid Base64."); }
            if (content.Length > MaximumRequestTextBytes) throw Failure("workspace_request_too_large", "The file bytes exceed the input limit.");
            WriteFile(request, checkout, content, result, token);
        }

        private void WriteFile(HarborWorkspaceOperationRequest request, string checkout, byte[] content, HarborWorkspaceOperationResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = ResolveRelativePath(request.RelativePath, checkout, false);
            if (content.Length > MaximumRequestTextBytes) throw Failure("workspace_request_too_large", "The file content exceeds the input limit.");
            if (!request.Overwrite && (File.Exists(path) || Directory.Exists(path)))
                throw Failure("workspace_file_exists", "The target file already exists.");
            string? parent = Path.GetDirectoryName(path);
            if (String.IsNullOrWhiteSpace(parent)) throw Failure("workspace_path_invalid", "The target file has no parent directory.");
            Directory.CreateDirectory(parent);
            EnsureNoLinks(parent, requireExists: true);
            string temporary = Path.Combine(parent, ".armada-write-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant());
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content, 0, content.Length);
                    stream.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                EnsureNoLinks(parent, requireExists: true);
                if (request.Overwrite) File.Move(temporary, path, true);
                else File.Move(temporary, path, false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            result.Exists = true;
            result.Length = content.Length;
        }
        private void DeleteFile(HarborWorkspaceOperationRequest request, string checkout, HarborWorkspaceOperationResult result)
        {
            string path = ResolveRelativePath(request.RelativePath, checkout, false);
            EnsureNoLinks(path, requireExists: false);
            if (Directory.Exists(path)) throw Failure("workspace_directory_delete_refused", "This operation cannot delete a directory.");
            result.Exists = File.Exists(path);
            if (result.Exists)
            {
                File.Delete(path);
                result.Exists = false;
            }
        }
    }
}
