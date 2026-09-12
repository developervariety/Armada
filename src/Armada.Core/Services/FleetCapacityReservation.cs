namespace Armada.Core.Services
{
    using Armada.Core.Database.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// A renewable, durable reservation for a fleet-capacity decision and its initial write.
    /// </summary>
    public sealed class FleetCapacityReservation : IAsyncDisposable
    {
        internal FleetCapacityReservation(
            ICoordinationLeaseMethods leases,
            string leaseName,
            string holder,
            TimeSpan ttl,
            LoggingModule? logging)
        {
            _Leases = leases;
            _LeaseName = leaseName;
            _Holder = holder;
            _Ttl = ttl;
            _Logging = logging;
            _Renewal = RenewUntilReleasedAsync();
        }

        /// <summary>
        /// Renew the lease at the commit boundary and throw if ownership was lost before the
        /// durable work graph was created.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task VerifyOwnershipAsync(CancellationToken token = default)
        {
            if (Volatile.Read(ref _OwnershipLost) != 0)
                throw new InvalidOperationException("Fleet-capacity admission was lost before work creation completed.");

            bool renewed = await _Leases.TryRenewAsync(
                _LeaseName,
                _Holder,
                _Ttl,
                token).ConfigureAwait(false);
            if (!renewed)
            {
                Interlocked.Exchange(ref _OwnershipLost, 1);
                throw new InvalidOperationException("Fleet-capacity admission was lost before work creation completed.");
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _Released, 1) != 0) return;
            _RenewalCancellation.Cancel();
            try
            {
                await _Renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await _Leases.ReleaseAsync(_LeaseName, _Holder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging?.Warn("[FleetCapacityReservation] could not release " + _LeaseName + ": " + ex.Message);
            }
            _RenewalCancellation.Dispose();
        }

        private async Task RenewUntilReleasedAsync()
        {
            TimeSpan interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(25).Ticks, _Ttl.Ticks / 3));
            try
            {
                while (true)
                {
                    await Task.Delay(interval, _RenewalCancellation.Token).ConfigureAwait(false);
                    bool renewed = await _Leases.TryRenewAsync(
                        _LeaseName,
                        _Holder,
                        _Ttl,
                        _RenewalCancellation.Token).ConfigureAwait(false);
                    if (!renewed)
                    {
                        Interlocked.Exchange(ref _OwnershipLost, 1);
                        _Logging?.Warn("[FleetCapacityReservation] ownership lost for " + _LeaseName + ".");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_RenewalCancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _OwnershipLost, 1);
                _Logging?.Warn("[FleetCapacityReservation] renewal failed for " + _LeaseName + ": " + ex.Message);
            }
        }

        private readonly ICoordinationLeaseMethods _Leases;
        private readonly string _LeaseName;
        private readonly string _Holder;
        private readonly TimeSpan _Ttl;
        private readonly LoggingModule? _Logging;
        private readonly CancellationTokenSource _RenewalCancellation = new CancellationTokenSource();
        private readonly Task _Renewal;
        private int _Released;
        private int _OwnershipLost;
    }
}
