namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Admiral shutdown: stop runs once, waits for every background loop that uses the database before
    /// disposing it, and still disposes the database when an earlier shutdown step throws.
    /// </summary>
    public class ArmadaServerStopTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Admiral Server Stop";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A repeated stop does not run the shutdown again", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir);
                int notifications = 0;
                server.OnStopping = () => Interlocked.Increment(ref notifications);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    server.Stop();
                    server.Stop();

                    AssertEqual(1, notifications, "the stop notification must fire once for two stop calls");
                    AssertTrue(IsDatabaseDisposed(server), "the first stop disposes the database");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Stop waits for the health check loop before disposing the database", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    CancellationToken serverToken = GetField<CancellationTokenSource>(server, "_TokenSource").Token;
                    Task realLoop = GetField<Task>(server, "_HealthCheckTask");
                    DatabaseDriver database = GetField<DatabaseDriver>(server, "_Database");
                    bool databaseReadAfterCancel = false;

                    // A health loop that still reads the database for a while after shutdown cancels it.
                    Task slowLoop = Task.Run(async () =>
                    {
                        try
                        {
                            await realLoop.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The real loop's own outcome is not under test.
                        }
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, serverToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
                        await database.Captains.EnumerateAsync().ConfigureAwait(false);
                        databaseReadAfterCancel = true;
                    });
                    SetField(server, "_HealthCheckTask", slowLoop);

                    server.Stop();

                    AssertTrue(slowLoop.IsCompleted, "Stop must not return before the health check loop finishes");
                    AssertFalse(slowLoop.IsFaulted, "the loop must not see a disposed database: " + slowLoop.Exception?.GetBaseException().Message);
                    AssertTrue(databaseReadAfterCancel, "the loop's last database read must succeed");
                    AssertTrue(IsDatabaseDisposed(server), "the database is disposed after the loop finishes");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Stop waits for the Harbor job expiry loop before disposing the database", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir, harborEnabled: true);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    CancellationToken serverToken = GetField<CancellationTokenSource>(server, "_TokenSource").Token;
                    Task? realLoop = GetField<Task?>(server, "_HarborJobExpiryTask");
                    AssertNotNull(realLoop, "an enabled Harbor starts the job expiry loop");
                    DatabaseDriver database = GetField<DatabaseDriver>(server, "_Database");
                    bool databaseReadAfterCancel = false;

                    // An expiry pass that is still reading the database when shutdown cancels the loop.
                    Task slowLoop = Task.Run(async () =>
                    {
                        try
                        {
                            await realLoop!.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The real loop's own outcome is not under test.
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
                        await database.Captains.EnumerateAsync().ConfigureAwait(false);
                        databaseReadAfterCancel = true;
                    });
                    SetField(server, "_HarborJobExpiryTask", slowLoop);

                    server.Stop();

                    AssertTrue(serverToken.IsCancellationRequested, "shutdown cancels the loop's token");
                    AssertTrue(slowLoop.IsCompleted, "Stop must not return before the Harbor job expiry loop finishes");
                    AssertFalse(slowLoop.IsFaulted, "the loop must not see a disposed database: " + slowLoop.Exception?.GetBaseException().Message);
                    AssertTrue(databaseReadAfterCancel, "the loop's last database read must succeed");
                    AssertTrue(IsDatabaseDisposed(server), "the database is disposed after the loop finishes");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });

            await RunTest("Stop disposes the database and notifies even when an earlier step throws", async () =>
            {
                string tempDir = NewTempDirectory();
                ArmadaServer server = NewServer(tempDir);
                int notifications = 0;
                server.OnStopping = () => Interlocked.Increment(ref notifications);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);

                    // A remote tunnel whose stop throws: its internal state was never initialized.
                    SetField(server, "_RemoteTunnel", RuntimeHelpers.GetUninitializedObject(typeof(RemoteTunnelManager)));

                    Exception? thrown = null;
                    try
                    {
                        server.Stop();
                    }
                    catch (Exception ex)
                    {
                        thrown = ex;
                    }

                    AssertNull(thrown, "a failing shutdown step is logged, not thrown: " + thrown?.Message);
                    AssertTrue(IsDatabaseDisposed(server), "the database must be disposed after a failing step");
                    AssertEqual(1, notifications, "the stop notification must still fire");
                }
                finally
                {
                    server.Stop();
                    DeleteDirectory(tempDir);
                }
            });
        }

        #region Private-Methods

        private static ArmadaServer NewServer(string tempDir, bool harborEnabled = false)
        {
            DatabaseSettings dbSettings = new DatabaseSettings
            {
                Type = DatabaseTypeEnum.Sqlite,
                Filename = Path.Combine(tempDir, "armada.db")
            };
            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = tempDir,
                DatabasePath = dbSettings.Filename,
                Database = dbSettings,
                LogDirectory = Path.Combine(tempDir, "logs"),
                DocksDirectory = Path.Combine(tempDir, "docks"),
                ReposDirectory = Path.Combine(tempDir, "repos"),
                AdmiralPort = FreePort(),
                McpPort = FreePort(),
                ApiKey = "test-key-" + Guid.NewGuid().ToString("N"),
                HeartbeatIntervalSeconds = 300
            };
            settings.Rest.Hostname = "127.0.0.1";
            settings.AutonomousObjectiveScheduler.Enabled = false;
            settings.Harbor.Enabled = harborEnabled;
            settings.SettingsFilePath = Path.Combine(tempDir, "settings.json");
            settings.InitializeDirectories();

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new ArmadaServer(logging, settings, quiet: true);
        }

        private static bool IsDatabaseDisposed(ArmadaServer server)
        {
            DatabaseDriver database = GetField<DatabaseDriver>(server, "_Database");
            FieldInfo? disposed = database.GetType().GetField("_Disposed", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("the database driver must track disposal");
            return (bool)disposed.GetValue(database)!;
        }

        private static T GetField<T>(ArmadaServer server, string name)
        {
            FieldInfo field = typeof(ArmadaServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ArmadaServer must have the field " + name);
            return (T)field.GetValue(server)!;
        }

        private static void SetField(ArmadaServer server, string name, object value)
        {
            FieldInfo field = typeof(ArmadaServer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ArmadaServer must have the field " + name);
            field.SetValue(server, value);
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_server_stop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                // A stopped server can briefly hold log handles; the temporary directory is disposable.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above on hosts that report a held handle as an access failure.
            }
        }

        private static int FreePort()
        {
            using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        #endregion
    }
}
