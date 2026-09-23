namespace Test.Shared.Infrastructure
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading.Tasks;

    /// <summary>
    /// Loopback ports for in-process servers that bind a configured port. A port is found by binding port 0 and
    /// releasing it, and the server binds it afterwards; between the two, the kernel can hand the same port to any
    /// other socket on the host (another test process's listener, or the source port of an outgoing connection).
    /// That window cannot be closed while the server takes a port number rather than a bound socket, so a start
    /// that loses the race is detected by its address-in-use failure and repeated on newly found ports.
    /// </summary>
    public static class LoopbackPorts
    {
        #region Public-Members

        /// <summary>
        /// Starts attempted before an address-in-use failure is reported. Each attempt uses newly found ports,
        /// so a repeated collision needs the host to reuse a just-released port several times in a row.
        /// </summary>
        public const int MaxStartAttempts = 5;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Find a loopback port that is free at the moment of the call.
        /// </summary>
        /// <returns>Port number.</returns>
        public static int FindFree()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Run a start that finds its own ports with <see cref="FindFree"/>, and repeat it on newly found ports
        /// when it fails because a port was taken after it was found. Any other failure propagates at once.
        /// </summary>
        /// <param name="start">Finds ports, then starts the server on them.</param>
        /// <param name="releaseFailedStart">Releases whatever a start that lost the race left running.</param>
        /// <param name="maxAttempts">Starts attempted before the address-in-use failure propagates.</param>
        /// <returns>Task.</returns>
        public static async Task StartAsync(Func<Task> start, Action releaseFailedStart, int maxAttempts = MaxStartAttempts)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (releaseFailedStart == null) throw new ArgumentNullException(nameof(releaseFailedStart));
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await start().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsAddressInUse(ex))
                {
                    Console.Error.WriteLine("[LoopbackPorts] a port was taken between finding and binding it; starting again on new ports (attempt "
                        + (attempt + 1) + " of " + maxAttempts + "): " + ex.Message);
                    releaseFailedStart();
                }
            }
        }

        /// <summary>
        /// Whether an exception, or any exception it wraps, reports that a listen address was already in use.
        /// </summary>
        /// <param name="ex">Exception.</param>
        /// <returns>True when a bind failed because the address was in use.</returns>
        public static bool IsAddressInUse(Exception? ex)
        {
            while (ex != null)
            {
                if (ex is SocketException socketEx && socketEx.SocketErrorCode == SocketError.AddressAlreadyInUse) return true;
                if (ex is HttpListenerException listenerEx && IsAddressInUseErrorCode(listenerEx.ErrorCode)) return true;
                // Kestrel reports a lost bind as AddressInUseException, which this assembly does not reference.
                if (String.Equals(ex.GetType().Name, "AddressInUseException", StringComparison.Ordinal)) return true;
                if (ex is AggregateException aggregate)
                {
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        if (IsAddressInUse(inner)) return true;
                    }
                    return false;
                }
                ex = ex.InnerException;
            }
            return false;
        }

        #endregion

        #region Private-Methods

        private static bool IsAddressInUseErrorCode(int errorCode)
        {
            // EADDRINUSE on Linux (98) and macOS (48); ERROR_ALREADY_EXISTS (183) and WSAEADDRINUSE (10048) on Windows.
            return errorCode == 98 || errorCode == 48 || errorCode == 183 || errorCode == 10048;
        }

        #endregion
    }
}
