namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>WebSocket framing and per-connection dispatch for the Harbor runner.</summary>
    public sealed partial class HarborRunnerClient
    {
        private async Task RunConnectedAsync(ClientWebSocket socket, CancellationToken token)
        {
            using (CancellationTokenSource connection = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (SemaphoreSlim sendLock = new SemaphoreSlim(1, 1))
            {
                HashSet<string> exitSent = new HashSet<string>(StringComparer.Ordinal);
                List<Task> background = new List<Task>();
                Func<HarborMessage, Task> send = message => SendAsync(socket, sendLock, message, connection.Token);

                await send(new HarborHandshake
                {
                    HarborId = _Settings.RunnerId,
                    Name = _Settings.Name,
                    ProtocolVersion = HarborProtocol.Version,
                    OsPlatform = Environment.OSVersion.Platform.ToString(),
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    MaxConcurrentJobs = _Settings.MaxConcurrentJobs,
                    Capabilities = _Settings.Supervisor.Commands.Keys
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .Select(name => new HarborCapability { Name = name, Available = true })
                        .ToList()
                }).ConfigureAwait(false);

                HarborMessage handshakeMessage = await ReceiveAsync(socket, connection.Token).ConfigureAwait(false);
                if (handshakeMessage is not HarborHandshakeAck ack || !ack.Accepted)
                {
                    string refusal = handshakeMessage is HarborHandshakeAck refused ? SafeReason(refused.Reason) : "harbor_handshake_response_invalid";
                    throw new InvalidOperationException(refusal);
                }

                List<HarborLocalJobRecord> records = _Journal.Enumerate();
                foreach (HarborLocalJobRecord record in records) exitSent.Add(record.JobId);
                HarborResumeRequest resume = new HarborResumeRequest();
                foreach (HarborLocalJobRecord record in records)
                {
                    HarborResumeJobInfo? proof = BuildResumeProof(record);
                    if (proof != null) resume.Jobs.Add(proof);
                    else
                    {
                        string reason = record.OutputGap
                            ? "harbor_output_gap"
                            : !String.IsNullOrWhiteSpace(record.FailureReason)
                                ? SafeReason(record.FailureReason)
                            : record.StopConfirmed && !record.ExitCode.HasValue
                                ? "harbor_exit_code_unknown"
                                : "harbor_process_identity_unverified";
                        _Log("Harbor job retained without a safe resume proof: " + record.JobId + " " + reason);
                    }
                }
                resume.CorrelationId = Guid.NewGuid().ToString("N");
                await send(resume).ConfigureAwait(false);
                List<HarborMessage> earlyMessages = new List<HarborMessage>();
                HarborResumeResponse? response = null;
                while (response == null)
                {
                    HarborMessage resumeMessage = await ReceiveAsync(socket, connection.Token).ConfigureAwait(false);
                    if (resumeMessage is HarborResumeResponse resumeResponse
                        && String.Equals(resumeResponse.CorrelationId, resume.CorrelationId, StringComparison.Ordinal))
                        response = resumeResponse;
                    else if (earlyMessages.Count < 32)
                        earlyMessages.Add(resumeMessage);
                    else
                        throw new InvalidOperationException("harbor_resume_backlog_exceeded");
                }

                ApplyResumeResponse(response, resume.Jobs, records, send, exitSent, background, connection.Token);
                foreach (HarborMessage earlyMessage in earlyMessages)
                    await HandleServerMessageAsync(earlyMessage, send, exitSent, background, connection.Token).ConfigureAwait(false);
                Task heartbeat = HeartbeatLoopAsync(socket, sendLock, exitSent, connection.Token);
                background.Add(heartbeat);
                Task? receive = null;
                try
                {
                    receive = ReceiveLoopAsync(socket, send, exitSent, background, connection.Token);
                    background.Add(receive);
                    Task completed = await Task.WhenAny(receive, heartbeat).ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                }
                finally
                {
                    connection.Cancel();
                    await JoinConnectionTasksAsync(receive, background, connection).ConfigureAwait(false);
                }
            }
        }

    }
}
