namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public static partial class HarborGitDispatcher
    {
        private static async Task<HarborGitOperationResult> EmptyAsync(Task operation)
        {
            await operation.ConfigureAwait(false);
            return new HarborGitOperationResult();
        }

        private static async Task<HarborGitOperationResult> TextAsync(Task<string> operation) =>
            new HarborGitOperationResult { Text = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> NullableTextAsync(Task<string?> operation) =>
            new HarborGitOperationResult { Text = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> FlagAsync(Task<bool> operation) =>
            new HarborGitOperationResult { Flag = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> NullableFlagAsync(Task<bool?> operation) =>
            new HarborGitOperationResult { Flag = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> NumberAsync(Task<int> operation) =>
            new HarborGitOperationResult { Number = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> NullableNumberAsync(Task<int?> operation) =>
            new HarborGitOperationResult { Number = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> TimeAsync(Task<DateTime?> operation) =>
            new HarborGitOperationResult { TimeUtc = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> PathsAsync(Task<IReadOnlyList<string>> operation)
        {
            IReadOnlyList<string> paths = await operation.ConfigureAwait(false);
            return new HarborGitOperationResult { Paths = new List<string>(paths) };
        }

        private static async Task<HarborGitOperationResult> ListPathsAsync(Task<List<string>> operation) =>
            new HarborGitOperationResult { Paths = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> SnapshotAsync(Task<DockWorkSnapshot> operation) =>
            new HarborGitOperationResult { Snapshot = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> ChangedPathsAsync(Task<ChangedPathsRead> operation) =>
            new HarborGitOperationResult { ChangedPaths = await operation.ConfigureAwait(false) };

        private static async Task<HarborGitOperationResult> ChangedStatusAsync(Task<ChangedPathStatusRead> operation) =>
            new HarborGitOperationResult { ChangedPathStatuses = await operation.ConfigureAwait(false) };
    }
}
