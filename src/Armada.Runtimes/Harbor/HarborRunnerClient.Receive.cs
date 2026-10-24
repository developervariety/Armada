namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Net.WebSockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Handles server job commands and emits runner liveness frames.</summary>
    public sealed partial class HarborRunnerClient
    {
        private async Task ReceiveLoopAsync(
            ClientWebSocket socket,
            Func<HarborMessage, Task> send,
            HashSet<string> exitSent,
            List<Task> background,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HarborMessage message = await ReceiveAsync(socket, token).ConfigureAwait(false);
                await HandleServerMessageAsync(message, send, exitSent, background, token).ConfigureAwait(false);
            }
        }

        private async Task HandleServerMessageAsync(
            HarborMessage message,
            Func<HarborMessage, Task> send,
            HashSet<string> exitSent,
            List<Task> background,
            CancellationToken token)
        {
            if (message is HarborLaunchRequest launch)
                await HandleLaunchAsync(launch, send, exitSent, background, token).ConfigureAwait(false);
            else if (message is HarborKillRequest kill)
                HandleKill(kill);
            else if (message is HarborError error)
                HandleServerError(error);
            else
                _Log("Harbor server sent an unsupported message: harbor_message_unexpected");
        }

        private void HandleServerError(HarborError error)
        {
            string reason = SafeReason(error.Message);
            _Log("Harbor server response: " + (String.IsNullOrWhiteSpace(error.JobId) ? reason : error.JobId + " " + reason));
            if (String.IsNullOrWhiteSpace(error.JobId)) return;
            try { RequestSafeStop(_Journal.Read(error.JobId)); }
            catch (HarborJobJournalException exception) { _Log("Harbor job stop unavailable: " + error.JobId + " " + exception.ReasonCode); }
        }

        private async Task HeartbeatLoopAsync(ClientWebSocket socket, SemaphoreSlim sendLock, HashSet<string> exitSent, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_Settings.HeartbeatIntervalSeconds), token).ConfigureAwait(false);
                HarborHeartbeat heartbeat = new HarborHeartbeat();
                foreach (HarborLocalJobRecord record in _Journal.Enumerate())
                {
                    bool sent;
                    lock (exitSent) sent = exitSent.Contains(record.JobId);
                    if (sent) continue;
                    bool supervisorAlive = HarborJobJournal.IsExactProcessAlive(record.SupervisorProcessId, record.SupervisorStartedUtc,
                        record.SupervisorLinuxStartTicks, record.SupervisorLinuxBootId);
                    bool childAlive = record.ProcessId.HasValue && record.ProcessStartedUtc.HasValue
                        && HarborJobJournal.IsExactProcessAlive(record.ProcessId.Value, record.ProcessStartedUtc,
                            record.ProcessLinuxStartTicks, record.ProcessLinuxBootId);
                    if ((record.StopConfirmed && record.ProcessId.HasValue && record.ProcessStartedUtc.HasValue)
                        || (!record.StopConfirmed && supervisorAlive && (!record.IsRunning || childAlive)))
                        heartbeat.LiveJobIds.Add(record.JobId);
                }
                await SendAsync(socket, sendLock, heartbeat, token).ConfigureAwait(false);
            }
        }

        private static string SafeReason(string? reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) return "harbor_error_unspecified";
            string value = reason.Trim();
            if (value.Length > 120) return "harbor_error_unspecified";
            foreach (char character in value)
                if (!(Char.IsAsciiLetterOrDigit(character) || character == '_' || character == '-')) return "harbor_error_unspecified";
            return value;
        }
    }
}
