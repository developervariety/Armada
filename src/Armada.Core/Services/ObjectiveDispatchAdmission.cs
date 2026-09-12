namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// A durable objective-dispatch reservation. The database lease is acquired before voyage
    /// creation, so concurrent server processes cannot both start work for one objective.
    /// </summary>
    public sealed class ObjectiveDispatchAdmission : IAsyncDisposable
    {
        internal ObjectiveDispatchAdmission(
            ICoordinationLeaseMethods leases,
            string leaseName,
            string holder,
            TimeSpan ttl,
            Objective objective,
            LoggingModule? logging)
        {
            _Leases = leases;
            _LeaseName = leaseName;
            _Holder = holder;
            _Ttl = ttl;
            Objective = objective;
            _Logging = logging;
            _Renewal = RenewUntilReleasedAsync();
        }

        /// <summary>
        /// Fresh objective state read while the reservation was held.
        /// </summary>
        public Objective Objective { get; }

        /// <summary>
        /// Throw if the durable lease was lost while dispatch work was in progress.
        /// </summary>
        public void ThrowIfOwnershipLost()
        {
            if (Volatile.Read(ref _OwnershipLost) != 0)
                throw new InvalidOperationException("Objective dispatch admission was lost before linking completed.");
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
                _Logging?.Warn("[ObjectiveDispatchAdmission] could not release " + _LeaseName + ": " + ex.Message);
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
                        _Logging?.Warn("[ObjectiveDispatchAdmission] ownership lost for " + _LeaseName + ".");
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
                _Logging?.Warn("[ObjectiveDispatchAdmission] renewal failed for " + _LeaseName + ": " + ex.Message);
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
