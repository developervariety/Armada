namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// A durable objective-dispatch reservation covering every objective one dispatch links. The
    /// database leases are acquired in stable order before voyage creation, so concurrent server
    /// processes cannot both start work for one objective, and the attempt is recorded durably so a
    /// process that stops between voyage creation and linking can be reconciled.
    /// </summary>
    public sealed class ObjectiveDispatchAdmission : IAsyncDisposable
    {
        #region Public-Members

        /// <summary>Event type written when an attempt is admitted.</summary>
        public const string StartedEventType = "objective.dispatch_attempt.started";

        /// <summary>Event type written when an attempt records the voyage it created.</summary>
        public const string VoyageCreatedEventType = "objective.dispatch_attempt.voyage_created";

        /// <summary>Event type written when an attempt ends, by its owner or by reconciliation.</summary>
        public const string ClosedEventType = "objective.dispatch_attempt.closed";

        /// <summary>Entity type of attempt events; the entity id is the attempt id.</summary>
        public const string AttemptEntityType = "objective-dispatch-attempt";

        /// <summary>
        /// How far back reconciliation examines unclosed attempts. Automatic event retention keeps
        /// every attempt record younger than this, so an unclosed attempt cannot vanish before it is reconciled.
        /// </summary>
        public static readonly TimeSpan ReconciliationLookBack = TimeSpan.FromDays(7);

        /// <summary>
        /// Attempt identifier, also the holder of every admission lease.
        /// </summary>
        public string AttemptId => _Record.AttemptId;

        /// <summary>
        /// Fresh state, read while the reservation was held, of the first admitted objective in lease order.
        /// </summary>
        public Objective Objective => Objectives[0];

        /// <summary>
        /// Fresh state of every admitted objective, read while the reservation was held, in lease order.
        /// </summary>
        public IReadOnlyList<Objective> Objectives { get; }

        #endregion

        #region Private-Members

        private readonly ICoordinationLeaseMethods _Leases;
        private readonly IEventMethods _Events;
        private readonly ObjectiveDispatchAttemptRecord _Record;
        private readonly TimeSpan _Ttl;
        private readonly LoggingModule? _Logging;
        private readonly CancellationTokenSource _RenewalCancellation = new CancellationTokenSource();
        private readonly Task _Renewal;
        private int _Released;
        private int _OwnershipLost;
        private int _Linked;

        #endregion

        #region Constructors-and-Factories

        internal ObjectiveDispatchAdmission(
            ICoordinationLeaseMethods leases,
            IEventMethods events,
            ObjectiveDispatchAttemptRecord record,
            TimeSpan ttl,
            IReadOnlyList<Objective> objectives,
            LoggingModule? logging)
        {
            _Leases = leases;
            _Events = events;
            _Record = record;
            _Ttl = ttl;
            Objectives = objectives;
            _Logging = logging;
            _Renewal = RenewUntilReleasedAsync();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return the admitted state of one objective, or null when it is not part of this admission.
        /// </summary>
        public Objective? FindObjective(string objectiveId)
        {
            return Objectives.FirstOrDefault(item => String.Equals(item.Id, objectiveId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Throw if lease renewal already observed that ownership was lost.
        /// </summary>
        public void ThrowIfOwnershipLost()
        {
            if (Volatile.Read(ref _OwnershipLost) != 0)
                throw new ObjectiveDispatchOwnershipLostException("lease renewal failed for attempt " + AttemptId + ".");
        }

        /// <summary>
        /// Confirm ownership of every admission lease with a compare-and-set renewal. Linking calls this
        /// immediately before the objective write, so a holder whose lease expired or was taken over
        /// cannot link.
        /// </summary>
        public async Task ConfirmOwnershipAsync(CancellationToken token = default)
        {
            ThrowIfOwnershipLost();
            foreach (string leaseName in _Record.LeaseNames)
            {
                bool renewed = await _Leases.TryRenewAsync(leaseName, AttemptId, _Ttl, token).ConfigureAwait(false);
                if (!renewed)
                {
                    Interlocked.Exchange(ref _OwnershipLost, 1);
                    _Logging?.Warn("[ObjectiveDispatchAdmission] attempt " + AttemptId + " no longer owns " + leaseName + ".");
                    throw new ObjectiveDispatchOwnershipLostException("attempt " + AttemptId + " no longer owns " + leaseName + ".");
                }
            }
        }

        /// <summary>
        /// Record the voyage this attempt created, before any objective is linked to it.
        /// </summary>
        public async Task RecordVoyageCreatedAsync(Voyage voyage, CancellationToken token = default)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            _Record.VoyageId = voyage.Id;
            await WriteAttemptEventAsync(
                _Events,
                VoyageCreatedEventType,
                _Record,
                "Objective dispatch attempt " + AttemptId + " created voyage " + voyage.Id + ".",
                token).ConfigureAwait(false);
        }

        /// <summary>
        /// Mark that every admitted objective is linked, so disposal closes the attempt as linked.
        /// </summary>
        public void MarkLinked()
        {
            Interlocked.Exchange(ref _Linked, 1);
        }

        /// <summary>
        /// Close the attempt record and release every lease.
        /// </summary>
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
                // Renewal stops by cancellation; that is the expected end of the loop.
            }

            _Record.Outcome = Volatile.Read(ref _Linked) != 0
                ? "linked"
                : Volatile.Read(ref _OwnershipLost) != 0 ? "ownership_lost" : "released";
            try
            {
                await WriteAttemptEventAsync(
                    _Events,
                    ClosedEventType,
                    _Record,
                    "Objective dispatch attempt " + AttemptId + " closed: " + _Record.Outcome + ".",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging?.Warn("[ObjectiveDispatchAdmission] could not close attempt " + AttemptId
                    + "; reconciliation will examine it once its leases are released: " + ex.Message);
            }

            foreach (string leaseName in _Record.LeaseNames)
            {
                try
                {
                    await _Leases.ReleaseAsync(leaseName, AttemptId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging?.Warn("[ObjectiveDispatchAdmission] could not release " + leaseName + ": " + ex.Message);
                }
            }

            _RenewalCancellation.Dispose();
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Stop renewing without releasing leases or closing the attempt, as a process that stopped would.
        /// </summary>
        internal async Task AbandonAsCrashedAsync()
        {
            Interlocked.Exchange(ref _Released, 1);
            _RenewalCancellation.Cancel();
            try
            {
                await _Renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Renewal stops by cancellation; that is the expected end of the loop.
            }
        }

        internal static async Task WriteAttemptEventAsync(
            IEventMethods events,
            string eventType,
            ObjectiveDispatchAttemptRecord record,
            string message,
            CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                TenantId = record.TenantId,
                EntityType = AttemptEntityType,
                EntityId = record.AttemptId,
                Payload = JsonSerializer.Serialize(record)
            };
            await events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task RenewUntilReleasedAsync()
        {
            TimeSpan interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(25).Ticks, _Ttl.Ticks / 3));
            try
            {
                while (true)
                {
                    await Task.Delay(interval, _RenewalCancellation.Token).ConfigureAwait(false);
                    foreach (string leaseName in _Record.LeaseNames)
                    {
                        bool renewed = await _Leases.TryRenewAsync(
                            leaseName,
                            AttemptId,
                            _Ttl,
                            _RenewalCancellation.Token).ConfigureAwait(false);
                        if (!renewed)
                        {
                            Interlocked.Exchange(ref _OwnershipLost, 1);
                            _Logging?.Warn("[ObjectiveDispatchAdmission] ownership lost for " + leaseName + ".");
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_RenewalCancellation.IsCancellationRequested)
            {
                // Disposal or abandonment cancelled renewal.
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _OwnershipLost, 1);
                _Logging?.Warn("[ObjectiveDispatchAdmission] renewal failed for attempt " + AttemptId + ": " + ex.Message);
            }
        }

        #endregion
    }
}
