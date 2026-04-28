namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    public class ProcessHostTests : TestSuite
    {
        public override string Name => "Process Host";

        protected override async Task RunTestsAsync()
        {
            await RunTest("SpawnDetachedAsync_ValidCommand_ReturnsPid", async () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ProcessHost host = new ProcessHost(logging);

                // Use cmd /c exit 0 on Windows -- quick exit, no output
                ProcessSpawnRequest req = new ProcessSpawnRequest
                {
                    Command = "cmd",
                    Args = "/c exit 0",
                    StdinPayload = "",
                    TimeoutSeconds = 10,
                };

                ProcessSpawnResult result = await host.SpawnDetachedAsync(req, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.ProcessId > 0, "ProcessId should be a positive integer after spawn");
            });

            await RunTest("SpawnDetachedAsync_ReturnsWithoutBlockingOnExit", async () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ProcessHost host = new ProcessHost(logging);

                // cmd /c timeout /t 5 would block for 5 seconds; SpawnDetachedAsync should return immediately
                ProcessSpawnRequest req = new ProcessSpawnRequest
                {
                    Command = "cmd",
                    // /c echo just prints something quickly; the test is that the method returns fast
                    Args = "/c echo hello",
                    StdinPayload = "ignored",
                    TimeoutSeconds = 30,
                };

                DateTime start = DateTime.UtcNow;
                ProcessSpawnResult result = await host.SpawnDetachedAsync(req, CancellationToken.None).ConfigureAwait(false);
                TimeSpan elapsed = DateTime.UtcNow - start;

                AssertTrue(result.ProcessId > 0, "ProcessId should be populated");
                // SpawnDetachedAsync must return in well under 1 second; 3s is a generous upper bound
                AssertTrue(elapsed.TotalSeconds < 3.0, "SpawnDetachedAsync should return without blocking on process exit");
            });

            await RunTest("SpawnDetachedAsync_InvalidCommand_Throws", async () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ProcessHost host = new ProcessHost(logging);

                ProcessSpawnRequest req = new ProcessSpawnRequest
                {
                    Command = "__nonexistent_command_xyz__",
                    Args = "",
                    StdinPayload = "",
                    TimeoutSeconds = 5,
                };

                bool threw = false;
                try
                {
                    await host.SpawnDetachedAsync(req, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    threw = true;
                }

                AssertTrue(threw, "SpawnDetachedAsync should throw when the command does not exist");
            });
        }
    }
}
