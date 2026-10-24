namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Harbor;

    public sealed partial class MissionServiceRequestedCaptainRoutingTests
    {
        private sealed partial class RetryAfterPersistedDockChannel
        {
            private readonly Dictionary<string, string> _Files = new Dictionary<string, string>(StringComparer.Ordinal);

            private static bool IsFilesystemOperation(HarborWorkspaceOperationEnum operation)
            {
                return operation == HarborWorkspaceOperationEnum.Stat
                    || operation == HarborWorkspaceOperationEnum.ReadText
                    || operation == HarborWorkspaceOperationEnum.EnumerateFiles
                    || operation == HarborWorkspaceOperationEnum.WriteText
                    || operation == HarborWorkspaceOperationEnum.WriteBytes
                    || operation == HarborWorkspaceOperationEnum.DeleteFile
                    || operation == HarborWorkspaceOperationEnum.AddGitExclude;
            }

            private void HandleFilesystemOperation(HarborWorkspaceOperationRequest request, HarborWorkspaceOperationResult result)
            {
                string path = request.RelativePath ?? String.Empty;
                if (request.Operation == HarborWorkspaceOperationEnum.Stat)
                {
                    result.Exists = _Files.ContainsKey(path);
                    result.IsDirectory = HasChildren(path);
                    result.Length = result.Exists ? _Files[path].Length : 0;
                }
                else if (request.Operation == HarborWorkspaceOperationEnum.ReadText)
                {
                    _Files.TryGetValue(path, out string? contents);
                    result.Text = contents ?? String.Empty;
                }
                else if (request.Operation == HarborWorkspaceOperationEnum.EnumerateFiles)
                {
                    string prefix = String.IsNullOrEmpty(path) ? String.Empty : path.TrimEnd('/') + "/";
                    foreach (string candidate in _Files.Keys)
                        if (candidate.StartsWith(prefix, StringComparison.Ordinal)) result.Paths.Add(candidate);
                }
                else if (request.Operation == HarborWorkspaceOperationEnum.WriteText)
                {
                    _Files[path] = request.Text ?? String.Empty;
                }
                else if (request.Operation == HarborWorkspaceOperationEnum.DeleteFile)
                {
                    _Files.Remove(path);
                }
                else
                {
                    result.Accepted = true;
                }
            }

            private bool HasChildren(string path)
            {
                string prefix = String.IsNullOrEmpty(path) ? String.Empty : path.TrimEnd('/') + "/";
                foreach (string candidate in _Files.Keys)
                    if (candidate.StartsWith(prefix, StringComparison.Ordinal) && !String.Equals(candidate, path, StringComparison.Ordinal)) return true;
                return false;
            }
        }
    }
}
