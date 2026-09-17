namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Runs the synthetic typed-decision evaluation set on request and whenever the provider reports a
    /// model version that has not been evaluated. One run at a time; a request while a run is in
    /// progress is refused rather than queued. Every run records one <see cref="EventType"/> event with
    /// the model, the totals, and each case's outcome. The cases are synthetic, so the event carries no
    /// operational state.
    /// </summary>
    public sealed class TypedDecisionEvalService
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded for every completed run.
        /// </summary>
        public const string EventType = "typed_decision.eval";

        #endregion

        #region Private-Members

        private const string _Header = "[TypedDecisionEvalService] ";

        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly TypedDecisionSettings _Settings;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly SemaphoreSlim _RunGate = new SemaphoreSlim(1, 1);
        private readonly object _ModelLock = new object();
        private string? _LastEvaluatedModel = null;
        private bool _LastEvaluatedModelLoaded = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the evaluation service.
        /// </summary>
        /// <param name="client">The typed-decision client the cases run against.</param>
        /// <param name="recorder">Recorder for the run event.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="database">Database, to find the model the last run evaluated.</param>
        /// <param name="logging">Logging module.</param>
        public TypedDecisionEvalService(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            DatabaseDriver database,
            LoggingModule logging)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run the evaluation set now. Returns null when another run is already in progress.
        /// </summary>
        /// <param name="decisionPoint">Run only this decision's cases; null runs every case.</param>
        /// <param name="reason">Why the run happens, for the report and event.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The report, or null when a run is already in progress.</returns>
        public async Task<TypedDecisionEvalReport?> RunAsync(string? decisionPoint, string reason, CancellationToken token)
        {
            if (!await _RunGate.WaitAsync(0, token).ConfigureAwait(false)) return null;
            try
            {
                List<TypedDecisionEvalCase> cases = TypedDecisionEvalCatalog.Build(_Recorder, _Settings, _Logging)
                    .Where(evalCase => String.IsNullOrWhiteSpace(decisionPoint)
                        || String.Equals(evalCase.DecisionPoint, decisionPoint, StringComparison.Ordinal))
                    .ToList();

                TypedDecisionEvalReport report = await TypedDecisionEvalRunner.RunAsync(_Client, cases, reason, token).ConfigureAwait(false);

                if (!String.IsNullOrWhiteSpace(report.Model) && report.Unavailable < report.Total)
                {
                    lock (_ModelLock)
                    {
                        _LastEvaluatedModel = report.Model;
                        _LastEvaluatedModelLoaded = true;
                    }
                }

                string message = "typed-decision eval (" + report.Reason + ") model=" + (report.Model ?? "unknown")
                    + " passed=" + report.Passed + " failed=" + report.Failed + " unavailable=" + report.Unavailable
                    + " total=" + report.Total;
                await _Recorder.RecordDomainEventAsync(EventType, message, JsonSerializer.Serialize(report), null, token).ConfigureAwait(false);
                _Logging.Info(_Header + message);
                return report;
            }
            finally
            {
                _RunGate.Release();
            }
        }

        /// <summary>
        /// Observe a model version the provider reported. When it differs from the last evaluated
        /// version and <see cref="TypedDecisionSettings.EvalOnModelChange"/> is on, a run starts in the
        /// background. Never throws and never blocks the caller.
        /// </summary>
        /// <param name="model">The reported model version.</param>
        public void ObserveModel(string? model)
        {
            if (String.IsNullOrWhiteSpace(model) || !_Settings.EvalOnModelChange) return;

            lock (_ModelLock)
            {
                if (_LastEvaluatedModelLoaded && String.Equals(_LastEvaluatedModel, model, StringComparison.Ordinal)) return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureLastEvaluatedModelLoadedAsync().ConfigureAwait(false);
                    lock (_ModelLock)
                    {
                        if (String.Equals(_LastEvaluatedModel, model, StringComparison.Ordinal)) return;
                    }

                    TypedDecisionEvalReport? report = await RunAsync(null, "model_changed", CancellationToken.None).ConfigureAwait(false);
                    if (report == null) return;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "evaluation after model change failed: " + ex.Message);
                }
            });
        }

        #endregion

        #region Private-Methods

        private async Task EnsureLastEvaluatedModelLoadedAsync()
        {
            lock (_ModelLock)
            {
                if (_LastEvaluatedModelLoaded) return;
            }

            string? model = null;
            List<ArmadaEvent> events = await _Database.Events.EnumerateByTypeAsync(EventType, 20).ConfigureAwait(false);
            foreach (ArmadaEvent evt in events.OrderByDescending(e => e.CreatedUtc))
            {
                if (String.IsNullOrWhiteSpace(evt.Payload)) continue;
                try
                {
                    TypedDecisionEvalReport? previous = JsonSerializer.Deserialize<TypedDecisionEvalReport>(evt.Payload!);
                    if (previous != null && !String.IsNullOrWhiteSpace(previous.Model) && previous.Unavailable < previous.Total)
                    {
                        model = previous.Model;
                        break;
                    }
                }
                catch (JsonException)
                {
                    // A malformed earlier payload is skipped; the next older event may still name the model.
                }
            }

            lock (_ModelLock)
            {
                if (!_LastEvaluatedModelLoaded)
                {
                    _LastEvaluatedModel = model;
                    _LastEvaluatedModelLoaded = true;
                }
            }
        }

        #endregion
    }
}
