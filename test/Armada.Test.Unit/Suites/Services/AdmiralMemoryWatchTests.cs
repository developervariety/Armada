namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Server;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for the admiral memory watch: under its threshold it is silent; at or above it, it names the
    /// memory figures and the REST requests still running, oldest first, keeps the run marker current on
    /// every sample, and repeats only after further growth or its repeat interval.
    /// </summary>
    public class AdmiralMemoryWatchTests : TestSuite
    {
        private const long _GiB = 1024L * 1024 * 1024;

        /// <summary>Suite name.</summary>
        public override string Name => "Admiral Memory Watch";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A sample under the threshold logs nothing and leaves the run marker alone", () =>
            {
                int beats = 0;
                AdmiralMemoryWatch watch = new AdmiralMemoryWatch(NewLogging(), new InFlightRequestRegistry(), () => beats++, thresholdBytes: 6 * _GiB);

                string? text = watch.Sample(DateTime.UtcNow, 2 * _GiB, 3 * _GiB, null, null);

                AssertNull(text, "no warning under the threshold");
                AssertEqual(0, beats, "no marker beat under the threshold");
                return Task.CompletedTask;
            });

            await RunTest("A sample above the threshold names the memory and the running requests oldest first", () =>
            {
                DateTime now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                InFlightRequestRegistry requests = new InFlightRequestRegistry();
                object slow = new object();
                object quick = new object();
                object finished = new object();
                requests.Begin(quick, "GET", "/api/v1/status", now.AddSeconds(-3));
                requests.Begin(slow, "POST", "/api/v1/history/enumerate", now.AddSeconds(-40));
                requests.Begin(finished, "GET", "/api/v1/inbox", now.AddSeconds(-20));
                requests.End(finished);
                int beats = 0;
                AdmiralMemoryWatch watch = new AdmiralMemoryWatch(NewLogging(), requests, () => beats++, thresholdBytes: 6 * _GiB);

                string? text = watch.Sample(now, 10 * _GiB, 11 * _GiB, 14 * _GiB, 20 * _GiB);

                AssertNotNull(text, "a warning at the threshold");
                AssertContains("managed heap 10240 MiB", text!);
                AssertContains("working set 11264 MiB", text!);
                AssertContains("container 14336 MiB of 20480 MiB", text!);
                AssertContains("requests in flight: POST /api/v1/history/enumerate (40 s), GET /api/v1/status (3 s)", text!);
                AssertFalse(text!.Contains("/api/v1/inbox", StringComparison.Ordinal), "a request that ended is not named");
                AssertEqual(1, beats, "the run marker is beaten");
                return Task.CompletedTask;
            });

            await RunTest("The warning repeats after more growth or the repeat interval, and the marker is beaten every sample", () =>
            {
                DateTime now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
                int beats = 0;
                AdmiralMemoryWatch watch = new AdmiralMemoryWatch(
                    NewLogging(),
                    new InFlightRequestRegistry(),
                    () => beats++,
                    thresholdBytes: 6 * _GiB,
                    growthStepBytes: 1 * _GiB,
                    repeatInterval: TimeSpan.FromSeconds(60));

                AssertNotNull(watch.Sample(now, 7 * _GiB, 0, null, null), "the first sample above the threshold warns");
                AssertNull(watch.Sample(now.AddSeconds(5), 7 * _GiB + 100, 0, null, null), "the same size a few seconds later does not warn again");
                AssertNotNull(watch.Sample(now.AddSeconds(10), 8 * _GiB + 200, 0, null, null), "another step of growth warns again");
                AssertNull(watch.Sample(now.AddSeconds(15), 8 * _GiB + 300, 0, null, null), "no growth and no interval: silent");
                AssertNotNull(watch.Sample(now.AddSeconds(75), 8 * _GiB + 300, 0, null, null), "the repeat interval warns again");
                AssertEqual(5, beats, "every sample above the threshold beats the run marker");

                AssertNull(watch.Sample(now.AddSeconds(80), 1 * _GiB, 0, null, null), "falling under the threshold is silent");
                AssertNotNull(watch.Sample(now.AddSeconds(85), 7 * _GiB, 0, null, null), "rising above it again warns at once");
                return Task.CompletedTask;
            });
        }

        private static LoggingModule NewLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }
    }
}
