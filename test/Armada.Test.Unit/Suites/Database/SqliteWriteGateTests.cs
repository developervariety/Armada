namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Test.Common;
    using Microsoft.Data.Sqlite;

    /// <summary>
    /// Tests SQLite write gate identity, ordering, cancellation, and lease flow.
    /// </summary>
    public class SqliteWriteGateTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "SQLite Write Gate";

        protected override async Task RunTestsAsync()
        {
            await RunTest("File URI, relative path, and symbolic link share one gate", FileAliasesShareGateAsync).ConfigureAwait(false);
            await RunTest("Shared-memory URI aliases share one gate", SharedMemoryAliasesShareGateAsync).ConfigureAwait(false);
            await RunTest("Private in-memory connections have independent gates", PrivateMemoryConnectionsAreIndependentAsync).ConfigureAwait(false);
            await RunTest("FIFO skips canceled waiters and rejects re-entry", CancellationAndReentryPreserveFifoAsync).ConfigureAwait(false);
        }

        private async Task FileAliasesShareGateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "armada_gate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "store space.db");
            string link = Path.Combine(directory, "alias.db");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, file);
            try
            {
                using (SqliteConnection initializer = new SqliteConnection("Data Source=" + file))
                    await initializer.OpenAsync().ConfigureAwait(false);
                File.CreateSymbolicLink(link, file);

                using (SqliteConnection first = new SqliteConnection("Data Source=" + file))
                using (SqliteConnection uri = new SqliteConnection("Data Source=" + new Uri(file).AbsoluteUri + "?cache=private"))
                using (SqliteConnection relativeConnection = new SqliteConnection("Data Source=" + relative))
                using (SqliteConnection linked = new SqliteConnection("Data Source=" + link))
                {
                    await first.OpenAsync().ConfigureAwait(false);
                    await uri.OpenAsync().ConfigureAwait(false);
                    await relativeConnection.OpenAsync().ConfigureAwait(false);
                    await linked.OpenAsync().ConfigureAwait(false);
                    await AssertAliasesQueueBehindAsync(first, uri, relativeConnection, linked).ConfigureAwait(false);
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private async Task SharedMemoryAliasesShareGateAsync()
        {
            string name = "armada_gate_" + Guid.NewGuid().ToString("N");
            string uriFirst = "Data Source=file:" + name + "?cache=shared&mode=memory";
            string uriSecond = "Data Source=file:" + name + "?mode=memory&cache=shared";
            SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder
            {
                DataSource = name,
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared
            };
            using (SqliteConnection first = new SqliteConnection(uriFirst))
            using (SqliteConnection reordered = new SqliteConnection(uriSecond))
            using (SqliteConnection builderAlias = new SqliteConnection(builder.ConnectionString))
            {
                await first.OpenAsync().ConfigureAwait(false);
                await reordered.OpenAsync().ConfigureAwait(false);
                await builderAlias.OpenAsync().ConfigureAwait(false);
                await AssertAliasesQueueBehindAsync(first, reordered, builderAlias).ConfigureAwait(false);
            }

            string implicitMemory = "Data Source=file::memory:?cache=shared";
            using (SqliteConnection implicitFirst = new SqliteConnection(implicitMemory))
            using (SqliteConnection implicitSecond = new SqliteConnection(implicitMemory))
            {
                await implicitFirst.OpenAsync().ConfigureAwait(false);
                await implicitSecond.OpenAsync().ConfigureAwait(false);
                await AssertAliasesQueueBehindAsync(implicitFirst, implicitSecond).ConfigureAwait(false);
            }
        }

        private async Task PrivateMemoryConnectionsAreIndependentAsync()
        {
            using (SqliteConnection first = new SqliteConnection("Data Source=:memory:"))
            using (SqliteConnection second = new SqliteConnection("Data Source=:memory:"))
            {
                await first.OpenAsync().ConfigureAwait(false);
                await second.OpenAsync().ConfigureAwait(false);
                IDisposable? firstLease = await SqliteWriteGate.EnterAsync(first).ConfigureAwait(false);
                Task<IDisposable?> secondRequest = await QueueInIndependentFlowAsync(second).ConfigureAwait(false);
                IDisposable? secondLease = await secondRequest.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                AssertNotNull(firstLease);
                AssertNotNull(secondLease);
                secondLease!.Dispose();
                firstLease!.Dispose();
            }
        }

        private async Task CancellationAndReentryPreserveFifoAsync()
        {
            using (SqliteConnection connection = new SqliteConnection("Data Source=:memory:"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                IDisposable? holder = await SqliteWriteGate.EnterAsync(connection).ConfigureAwait(false);
                AssertNotNull(holder);

                Task<IDisposable?> first = await QueueInIndependentFlowAsync(connection).ConfigureAwait(false);
                using (CancellationTokenSource middleCancellation = new CancellationTokenSource())
                {
                    Task<IDisposable?> middle = await QueueInIndependentFlowAsync(connection, middleCancellation.Token).ConfigureAwait(false);
                    Task<IDisposable?> last = await QueueInIndependentFlowAsync(connection).ConfigureAwait(false);
                    middleCancellation.Cancel();
                    await AssertThrowsAsync<TaskCanceledException>(async () => await middle.ConfigureAwait(false)).ConfigureAwait(false);

                    AssertFalse(first.IsCompleted, "The first queued writer waits for the holder.");
                    AssertFalse(last.IsCompleted, "The last queued writer waits behind the first.");
                    AssertThrows<InvalidOperationException>(() => SqliteWriteGate.EnterAsync(connection));

                    holder!.Dispose();
                    IDisposable? firstLease = await first.ConfigureAwait(false);
                    AssertNotNull(firstLease);
                    AssertFalse(last.IsCompleted, "The last writer waits until the first lease is released.");
                    firstLease!.Dispose();
                    IDisposable? lastLease = await last.ConfigureAwait(false);
                    AssertNotNull(lastLease);
                    lastLease!.Dispose();
                    firstLease.Dispose();
                }
            }

            using (SqliteConnection connection = new SqliteConnection("Data Source=:memory:"))
            using (CancellationTokenSource canceled = new CancellationTokenSource())
            {
                await connection.OpenAsync().ConfigureAwait(false);
                canceled.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(async () => await SqliteWriteGate.EnterAsync(connection, canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        private async Task AssertAliasesQueueBehindAsync(params SqliteConnection[] connections)
        {
            IDisposable? holder = await SqliteWriteGate.EnterAsync(connections[0]).ConfigureAwait(false);
            AssertNotNull(holder);
            System.Collections.Generic.List<Task<IDisposable?>> queued = new System.Collections.Generic.List<Task<IDisposable?>>();
            try
            {
                for (int index = 1; index < connections.Length; index++)
                    queued.Add(await QueueInIndependentFlowAsync(connections[index]).ConfigureAwait(false));
                foreach (Task<IDisposable?> task in queued)
                    AssertFalse(task.IsCompleted, "An alias waits for the current holder.");
            }
            finally
            {
                holder!.Dispose();
                foreach (Task<IDisposable?> task in queued)
                {
                    using (IDisposable? lease = await task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                        AssertNotNull(lease);
                }
            }
        }

        private static async Task<Task<IDisposable?>> QueueInIndependentFlowAsync(SqliteConnection connection, CancellationToken token = default)
        {
            TaskCompletionSource<Task<IDisposable?>> request = new TaskCompletionSource<Task<IDisposable?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() =>
                {
                    try { request.TrySetResult(SqliteWriteGate.EnterAsync(connection, token)); }
                    catch (Exception exception) { request.TrySetException(exception); }
                });
            }
            return await request.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
    }
}
