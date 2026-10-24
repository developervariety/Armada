namespace Armada.Server.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    public sealed partial class HarborWorkspaceCoordinator
    {
        private static bool ResultWithinLimit(HarborWorkspaceOperationResult result)
        {
            try
            {
                return System.Text.Encoding.UTF8.GetByteCount(HarborProtocol.Serialize(result)) <= MaximumResultBytes;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is System.Text.Json.JsonException)
            {
                return false;
            }
        }

        private sealed class WorkspaceLink
        {
            internal HarborRunnerSession Session { get; }
            internal Func<HarborMessage, CancellationToken, Task> Send { get; }

            internal WorkspaceLink(HarborRunnerSession session, Func<HarborMessage, CancellationToken, Task> send)
            {
                Session = session;
                Send = send;
            }
        }
    }
}
