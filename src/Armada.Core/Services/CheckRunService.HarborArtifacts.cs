namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class CheckRunService
    {
        private async Task<HarborArtifactStage> StageHarborArtifactsAsync(
            HarborCheckExecution execution, List<string>? expectedArtifacts, CancellationToken token)
        {
            string root = Path.Combine(Path.GetTempPath(), "armada-harbor-check-artifacts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            List<CheckRunArtifact> artifacts = new List<CheckRunArtifact>();
            try
            {
                foreach (string relativePath in expectedArtifacts ?? new List<string>())
                {
                    if (String.IsNullOrWhiteSpace(relativePath)) continue;
                    string? localPath = PathContainment.TryResolve(root, relativePath);
                    if (localPath == null) throw new InvalidOperationException("harbor_check_artifact_path_invalid");
                    HarborWorkspaceOperationRequest stat = HarborCheckoutService.CreateRequest(execution.Dock, HarborWorkspaceOperationEnum.Stat);
                    stat.RelativePath = relativePath.Replace('\\', '/');
                    HarborWorkspaceOperationResult metadata = await HarborCheckouts!.ExecuteAsync(execution.Dock, stat, token).ConfigureAwait(false);
                    if (!metadata.Exists || metadata.IsDirectory) continue;
                    if (metadata.Length > 2 * 1024 * 1024)
                        throw new InvalidOperationException("harbor_check_artifact_exceeds_transport_limit:" + relativePath);
                    HarborWorkspaceOperationRequest read = HarborCheckoutService.CreateRequest(execution.Dock, HarborWorkspaceOperationEnum.ReadBytes);
                    read.RelativePath = relativePath.Replace('\\', '/');
                    read.MaxBytes = 2 * 1024 * 1024;
                    HarborWorkspaceOperationResult content = await HarborCheckouts.ExecuteAsync(execution.Dock, read, token).ConfigureAwait(false);
                    if (content.Length != metadata.Length || content.BytesBase64 == null)
                        throw new InvalidOperationException("harbor_check_artifact_incomplete:" + relativePath);
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(content.BytesBase64); }
                    catch (FormatException exception) { throw new InvalidOperationException("harbor_check_artifact_invalid:" + relativePath, exception); }
                    if (bytes.LongLength != metadata.Length) throw new InvalidOperationException("harbor_check_artifact_incomplete:" + relativePath);
                    string? parent = Path.GetDirectoryName(localPath);
                    if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    await File.WriteAllBytesAsync(localPath, bytes, token).ConfigureAwait(false);
                    if (metadata.LastWriteUtc.HasValue) File.SetLastWriteTimeUtc(localPath, metadata.LastWriteUtc.Value);
                    artifacts.Add(new CheckRunArtifact
                    {
                        Path = relativePath.Replace('\\', '/'), SizeBytes = metadata.Length, LastWriteUtc = metadata.LastWriteUtc
                    });
                }
                return new HarborArtifactStage(root, artifacts);
            }
            catch
            {
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

    }
}
