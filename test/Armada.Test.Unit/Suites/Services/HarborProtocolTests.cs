namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>Round-trip and rejection tests for the Harbor wire protocol.</summary>
    public sealed class HarborProtocolTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Harbor Protocol";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Handshake_RoundTripsWithCapabilities", () =>
            {
                HarborHandshake typed = RoundTrip<HarborHandshake>(new HarborHandshake
                {
                    CorrelationId = "corr-1",
                    TraceParent = "00-abc-def-01",
                    HarborId = "hbr_test",
                    Name = "Workstation",
                    ProtocolVersion = HarborProtocol.Version,
                    MaxConcurrentJobs = 8,
                    Capabilities = new List<HarborCapability> { new HarborCapability { Name = "claude", Available = true } }
                });
                AssertEqual("hbr_test", typed.HarborId, "harbor id");
                AssertEqual("corr-1", typed.CorrelationId, "correlation id");
                AssertEqual(8, typed.MaxConcurrentJobs, "capacity");
                AssertEqual("claude", typed.Capabilities[0].Name, "capability");
            });

            await RunTest("CommandsAndEvents_RoundTripWithTypeDiscriminator", () =>
            {
                HarborLaunchRequest launch = RoundTrip<HarborLaunchRequest>(new HarborLaunchRequest
                {
                    JobId = "job-1",
                    Runtime = "claude",
                    WorkingDirectory = "/work",
                    Arguments = new List<string> { "--print" },
                    Environment = new Dictionary<string, string> { { "FOO", "bar" } }
                });
                AssertEqual("job-1", launch.JobId, "launch job");
                AssertEqual("bar", launch.Environment["FOO"], "launch environment");
                AssertEqual(5000, RoundTrip<HarborKillRequest>(new HarborKillRequest { JobId = "j", GracefulTimeoutMs = 5000 }).GracefulTimeoutMs, "kill grace");
                AssertEqual(0, RoundTrip<HarborKillRequest>(new HarborKillRequest { JobId = "j", GracefulTimeoutMs = -4 }).GracefulTimeoutMs, "negative grace clamps");
                AssertEqual(4242, RoundTrip<HarborStarted>(new HarborStarted { JobId = "j", ProcessId = 4242 }).ProcessId, "started pid");
                HarborOutput output = RoundTrip<HarborOutput>(new HarborOutput { JobId = "j", Sequence = 7, Stream = HarborOutputStreamEnum.Stderr, Data = "warn" });
                AssertEqual(7L, output.Sequence, "output sequence");
                AssertEqual(HarborOutputStreamEnum.Stderr, output.Stream, "output stream");
                AssertEqual(3, RoundTrip<HarborExited>(new HarborExited { JobId = "j", ExitCode = 3 }).ExitCode, "exit code");
                AssertEqual(2, RoundTrip<HarborHeartbeat>(new HarborHeartbeat { LiveJobIds = new List<string> { "a", "b" } }).LiveJobIds.Count, "heartbeat jobs");
                AssertEqual("boom", RoundTrip<HarborError>(new HarborError { JobId = "j", Message = "boom" }).Message, "error message");
                AssertEqual("nope", RoundTrip<HarborHandshakeAck>(new HarborHandshakeAck { Accepted = false, Reason = "nope" }).Reason, "ack reason");
            });

            await RunTest("Deserialize_RejectsEmptyMalformedUnknownAndRemovedTypes", () =>
            {
                AssertRejected<ArgumentNullException>(() => HarborProtocol.Serialize(null!), "null serialize");
                AssertRejected<ArgumentNullException>(() => HarborProtocol.Deserialize("   "), "empty payload");
                AssertRejected<FormatException>(() => HarborProtocol.Deserialize("{ not valid json"), "malformed payload");
                AssertRejected<FormatException>(() => HarborProtocol.Deserialize("{\"type\":\"bogus\"}"), "unknown discriminator");
                AssertRejected<FormatException>(() => HarborProtocol.Deserialize("{\"type\":\"git\",\"requestId\":\"r\"}"), "git command is not part of the protocol");
                AssertRejected<FormatException>(() => HarborProtocol.Deserialize("{\"type\":\"stdin\",\"jobId\":\"j\"}"), "stdin command is not part of the protocol");
            });
        }

        private T RoundTrip<T>(HarborMessage message) where T : HarborMessage
        {
            HarborMessage decoded = HarborProtocol.Deserialize(HarborProtocol.Serialize(message));
            AssertTrue(decoded is T, "decoded type " + decoded.GetType().Name + " is " + typeof(T).Name);
            return (T)decoded;
        }

        private void AssertRejected<TException>(Action action, string label) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception exception)
            {
                AssertTrue(false, label + ": expected " + typeof(TException).Name + " but got " + exception.GetType().Name);
                return;
            }
            AssertTrue(false, label + ": expected " + typeof(TException).Name);
        }
    }
}
