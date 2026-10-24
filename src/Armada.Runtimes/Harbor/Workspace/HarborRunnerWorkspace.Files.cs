namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Dispatches bounded read operations for an owned checkout.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task ExecuteFileOperationAsync(
            HarborWorkspaceOperationRequest request,
            HarborCheckoutIntent intent,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            string checkout = CheckoutPath(grant, intent.Binding, requireExists: true);
            if (HarborWorkspaceOperationRules.IsMutation(request) || !String.IsNullOrWhiteSpace(request.ExpectedHead))
                await CheckExpectedHeadAsync(request, checkout, token).ConfigureAwait(false);
            switch (request.Operation)
            {
                case HarborWorkspaceOperationEnum.Stat:
                    Stat(request, checkout, result);
                    break;
                case HarborWorkspaceOperationEnum.ReadText:
                    ReadText(request, checkout, result);
                    break;
                case HarborWorkspaceOperationEnum.ReadBytes:
                    ReadBytes(request, checkout, result);
                    break;
                case HarborWorkspaceOperationEnum.WriteText:
                    WriteText(request, checkout, result, token);
                    break;
                case HarborWorkspaceOperationEnum.WriteBytes:
                    WriteBytes(request, checkout, result, token);
                    break;
                case HarborWorkspaceOperationEnum.EnumerateFiles:
                    EnumerateFiles(request, checkout, result, token);
                    break;
                case HarborWorkspaceOperationEnum.DeleteFile:
                    DeleteFile(request, checkout, result);
                    break;
            }
            if (request.ReadOnly && (request.Operation == HarborWorkspaceOperationEnum.WriteText
                || request.Operation == HarborWorkspaceOperationEnum.WriteBytes))
            {
                string stagedPath = ResolveRelativePath(request.RelativePath, checkout, true);
                File.SetAttributes(stagedPath, File.GetAttributes(stagedPath) | FileAttributes.ReadOnly);
            }
            result.Binding = intent.Binding;
            result.HeadCommit = await ReadHeadAsync(checkout, token).ConfigureAwait(false);
            result.BranchName = await ReadBranchAsync(checkout, token).ConfigureAwait(false);
            result.CanonicalWorkingDirectory = checkout;
            result.Accepted = true;
        }

        private void Stat(HarborWorkspaceOperationRequest request, string checkout, HarborWorkspaceOperationResult result)
        {
            string path = ResolveRelativePath(request.RelativePath, checkout, false);
            bool directory = Directory.Exists(path);
            bool file = File.Exists(path);
            result.Exists = directory || file;
            result.IsDirectory = directory;
            if (result.Exists)
            {
                FileInfo info = new FileInfo(path);
                DirectoryInfo directoryInfo = new DirectoryInfo(path);
                result.Length = file ? info.Length : 0;
                result.LastWriteUtc = file ? info.LastWriteTimeUtc : directoryInfo.LastWriteTimeUtc;
            }
        }

        private void ReadText(HarborWorkspaceOperationRequest request, string checkout, HarborWorkspaceOperationResult result)
        {
            string path = ResolveRelativePath(request.RelativePath, checkout, true);
            byte[] content = ReadBoundedFile(path, request.MaxBytes);
            try { result.Text = new System.Text.UTF8Encoding(false, true).GetString(content); }
            catch (System.Text.DecoderFallbackException) { throw Failure("workspace_text_not_utf8", "The selected file is not valid UTF-8."); }
            result.Length = content.Length;
        }

        private void ReadBytes(HarborWorkspaceOperationRequest request, string checkout, HarborWorkspaceOperationResult result)
        {
            string path = ResolveRelativePath(request.RelativePath, checkout, true);
            byte[] content = ReadBoundedFile(path, request.MaxBytes);
            result.BytesBase64 = Convert.ToBase64String(content);
            result.Length = content.Length;
        }
    }
}
