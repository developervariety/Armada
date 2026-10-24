namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class DefinitionOfDoneGate
    {
        private static readonly AsyncLocal<GateBudgetLedger?> _ExecutionBudgets = new AsyncLocal<GateBudgetLedger?>();
        private static readonly AsyncLocal<GateExecutionBudget?> _ActiveExecutionBudget = new AsyncLocal<GateExecutionBudget?>();
        private GateExecutionBudget? ActiveExecutionBudget => _ActiveExecutionBudget.Value;

        private IDisposable EnterExecutionBudgetLedger()
        {
            GateBudgetLedger? previousLedger = _ExecutionBudgets.Value;
            GateExecutionBudget? previousBudget = _ActiveExecutionBudget.Value;
            _ExecutionBudgets.Value = new GateBudgetLedger();
            _ActiveExecutionBudget.Value = null;
            return new BudgetScope(() =>
            {
                _ExecutionBudgets.Value = previousLedger;
                _ActiveExecutionBudget.Value = previousBudget;
            });
        }

        private IDisposable EnterCommandBudget(string subject, string family, WorkflowProfile? profile)
        {
            GateExecutionBudget? previous = ActiveExecutionBudget;
            GateBudgetLedger ledger = _ExecutionBudgets.Value ?? new GateBudgetLedger();
            _ActiveExecutionBudget.Value = ledger.Resolve(subject, family, profile,
                () => ResolveCommandTimeout(ResolveCommandTimeoutSeconds(profile)));
            return new BudgetScope(() => _ActiveExecutionBudget.Value = previous);
        }

        private bool DeclaredExecutionBudgetFailed => _ExecutionBudgets.Value?.HasFailure == true;

        private async Task<DefinitionOfDoneResult> RunCommandAsync(
            string label, string command, string workingDir, CancellationToken token,
            Regex? failedTestLinePattern = null,
            IReadOnlyDictionary<string, string>? environment = null,
            WorkflowProfile? limitProfile = null)
        {
            GateExecutionBudget? budget = ActiveExecutionBudget;
            DefinitionOfDoneResult result = await RunCommandCoreAsync(label, command, workingDir, token,
                failedTestLinePattern, environment, limitProfile ?? budget?.Profile).ConfigureAwait(false);
            budget?.ApplyTo(result, label);
            return result;
        }

        private sealed class GateBudgetLedger
        {
            private readonly Dictionary<(string Subject, string Family), GateExecutionBudget?> _Budgets
                = new Dictionary<(string Subject, string Family), GateExecutionBudget?>();

            public bool HasFailure
            {
                get
                {
                    foreach (GateExecutionBudget? budget in _Budgets.Values)
                        if (budget?.HasFailure == true) return true;
                    return false;
                }
            }

            public void AppendSummary(DefinitionOfDoneResult result)
            {
                foreach (var entry in _Budgets)
                {
                    if (entry.Value == null) continue;
                    result.GateCommandOutput = (result.GateCommandOutput ?? String.Empty) + Environment.NewLine
                        + "DoD execution budget summary " + entry.Key.Subject + "/" + entry.Key.Family + ": "
                        + entry.Value.Timing;
                }
            }

            public GateExecutionBudget? Resolve(string subject, string family, WorkflowProfile? profile, Func<TimeSpan> resolveLimit)
            {
                var key = (subject, family);
                if (_Budgets.TryGetValue(key, out GateExecutionBudget? budget)) return budget;
                // An undeclared profile keeps its established per-command/recovery policy.
                // Remember that choice too: a later attempt cannot change this run's contract.
                budget = profile?.GateCommandTimeoutSeconds != null
                    ? new GateExecutionBudget(profile, resolveLimit()) : null;
                _Budgets.Add(key, budget);
                return budget;
            }
        }

        private sealed class BudgetScope : IDisposable
        {
            private Action? _Restore;
            public BudgetScope(Action restore) { _Restore = restore; }
            public void Dispose() { Interlocked.Exchange(ref _Restore, null)?.Invoke(); }
        }

        private sealed class GateExecutionBudget
        {
            private TimeSpan _Execution;
            private bool _ExecutionKnown = true;
            private string? _FailureReason;
            private DefinitionOfDoneFailureClassEnum _FailureClass = DefinitionOfDoneFailureClassEnum.Timeout;

            public GateExecutionBudget(WorkflowProfile profile, TimeSpan limit) { Profile = profile; Limit = limit; }
            public WorkflowProfile Profile { get; }
            public TimeSpan Limit { get; }
            public bool HasFailure => _FailureReason != null || _Execution >= Limit;
            public bool TimedOut => HasFailure && _FailureClass == DefinitionOfDoneFailureClassEnum.Timeout;
            public string Timing => "command_execution_ms="
                + (_ExecutionKnown ? Math.Ceiling(_Execution.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : "unknown")
                + "; shared_execution_budget_ms=" + Math.Ceiling(Limit.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

            public bool TryGetRemaining(out TimeSpan remaining)
            {
                remaining = Limit - _Execution;
                if (_FailureReason == null && remaining > TimeSpan.Zero) return true;
                MarkExhausted();
                remaining = TimeSpan.Zero;
                return false;
            }

            public int GetRemoteTimeoutSeconds(TimeSpan fallback, int protocolCap)
            {
                if (!TryGetRemaining(out TimeSpan remaining)) throw new InvalidOperationException(_FailureReason);
                double seconds = Math.Floor(remaining.TotalSeconds);
                if (seconds < 1)
                {
                    _FailureReason = "DoD command timed out: less than one whole second remains in the shared execution budget.";
                    throw new InvalidOperationException(_FailureReason);
                }
                return (int)Math.Min(protocolCap, seconds);
            }

            public void RecordNativeExecution(TimeSpan elapsed, bool timedOut)
            {
                _Execution += elapsed;
                // The native runner's deadline is the entire remaining budget. A fired
                // deadline cannot fund another attempt due to timer/stopwatch rounding.
                if (timedOut || _Execution >= Limit) MarkExhausted();
            }

            public void RecordRemoteExecution(HarborWorkspaceOperationResult result)
            {
                if (result.DurationMilliseconds is long milliseconds && milliseconds >= 0)
                {
                    try
                    {
                        _Execution += TimeSpan.FromMilliseconds(milliseconds);
                        if (_Execution >= Limit) MarkExhausted();
                        return;
                    }
                    catch (OverflowException) { }
                }
                _ExecutionKnown = false;
                _FailureClass = DefinitionOfDoneFailureClassEnum.Infra;
                _FailureReason = "Harbor verification has no valid duration for the declared DoD execution budget.";
            }

            private void MarkExhausted()
            {
                _FailureReason ??= "DoD command timed out: shared execution budget exhausted.";
            }

            public DefinitionOfDoneResult Failure(string label)
            {
                if (_FailureReason == null) MarkExhausted();
                DefinitionOfDoneResult result = DefinitionOfDoneResult.Fail(label, -1, _FailureReason!, _FailureClass);
                ApplyTo(result, label);
                return result;
            }

            public DefinitionOfDoneResult ApplyTo(DefinitionOfDoneResult result, string label)
            {
                if (_Execution >= Limit) MarkExhausted();
                string timing = "DoD execution budget: " + Timing;
                if (!(result.GateCommandOutput ?? String.Empty).Contains(Environment.NewLine + timing, StringComparison.Ordinal))
                    result.GateCommandOutput = (result.GateCommandOutput ?? String.Empty) + Environment.NewLine + timing;
                if (_FailureReason == null) return result;
                result.Passed = false;
                result.ExitCode = -1;
                result.CommandLabel = label;
                // Keep a real named test failure when its command also hit the budget.
                if (result.FailureClass != DefinitionOfDoneFailureClassEnum.TestFail || _FailureClass != DefinitionOfDoneFailureClassEnum.Timeout)
                    result.FailureClass = _FailureClass;
                result.TimedOut |= _FailureClass == DefinitionOfDoneFailureClassEnum.Timeout;
                result.RunnerOutputIncomplete |= result.TimedOut;
                result.FailedTestNamesOverflow |= result.TimedOut;
                if (!(result.OutputTail ?? String.Empty).Contains(_FailureReason, StringComparison.Ordinal))
                    result.OutputTail = (result.OutputTail ?? String.Empty) + Environment.NewLine + _FailureReason;
                if (!(result.GateCommandOutput ?? String.Empty).Contains(_FailureReason, StringComparison.Ordinal))
                    result.GateCommandOutput += Environment.NewLine + _FailureReason;
                return result;
            }
        }
    }
}
