namespace Test.Shared.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;

    /// <summary>
    /// One link session of a <see cref="HarborLinkClient"/> to a real <see cref="HarborConnectionManager"/> over a
    /// <see cref="LoopbackHarborTransport"/>, which a test can drop (as a Wi-Fi drop or lid close would) and then replace
    /// with a new session of a new client, as the Harbor app's reconnect loop does.
    /// </summary>
    public sealed class LoopbackHarborSession
    {
        #region Public-Members

        /// <summary>
        /// The session's client.
        /// </summary>
        public HarborLinkClient Client { get; }

        #endregion

        #region Private-Members

        private readonly LoopbackHarborTransport _Transport;
        private readonly CancellationTokenSource _Cancel = new CancellationTokenSource();
        private Task _Session = Task.CompletedTask;

        #endregion

        #region Constructors-and-Factories

        private LoopbackHarborSession(HarborLinkClient client, LoopbackHarborTransport transport)
        {
            Client = client;
            _Transport = transport;
        }

        /// <summary>
        /// Run a session of the client and wait until the Admiral accepted its handshake.
        /// </summary>
        /// <param name="manager">Admiral-side manager.</param>
        /// <param name="harborId">Harbor identifier the client handshakes with.</param>
        /// <param name="client">The client.</param>
        /// <returns>The connected session.</returns>
        /// <exception cref="TimeoutException">Thrown when the handshake is not accepted within 10 seconds.</exception>
        public static async Task<LoopbackHarborSession> ConnectAsync(HarborConnectionManager manager, string harborId, HarborLinkClient client)
        {
            LoopbackHarborTransport transport = new LoopbackHarborTransport(manager, harborId, null, null);
            LoopbackHarborSession session = new LoopbackHarborSession(client, transport);
            TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session._Session = Task.Run(() => client.RunSessionAsync(transport, session._Cancel.Token, () => connected.TrySetResult(true)));
            Task finished = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            if (finished != connected.Task) throw new TimeoutException("the Harbor session " + harborId + " did not connect");
            return session;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Drop the link and wait for the session to end.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task DropAsync()
        {
            _Transport.Complete();
            _Cancel.Cancel();
            try { await _Session.ConfigureAwait(false); }
            catch { }
        }

        #endregion
    }
}
