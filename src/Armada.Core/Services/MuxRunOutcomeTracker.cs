namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Core.Protocol;

    /// <summary>
    /// Follows one <c>mux print --output-format jsonl</c> run line by line and decides what its exit means to Armada.
    /// Mux exits 2 after a denied tool call (<c>tool_call_denied</c>, for example a tool name the model invented) and 1
    /// after any other error event, even when the run then finishes and its <c>run_completed</c> event reports
    /// <c>completed_with_errors</c>; on a non-zero exit it also skips writing <c>--output-last-message</c>. This tracker
    /// reads the typed events (never free text): when <c>run_completed</c> reports <c>completed</c> or
    /// <c>completed_with_errors</c> and no fatal error was reported, the run succeeded, <see cref="ResolveExitCode"/>
    /// reports exit 1 or 2 as 0, and <see cref="FinalAssistantText"/> is the final reply rebuilt from the stream (the
    /// assistant_text deltas of the last assistant turn). Genuine failures (iteration limit, token budget, degenerate
    /// output, context limit, connection, endpoint and authentication errors, and any error code that is not a tool or
    /// approval error) keep their exit code. Each error event and each failed run status also yields a
    /// <see cref="RuntimeProviderError"/> with a human-readable <see cref="RuntimeProviderError.Reason"/>. One instance
    /// per run; thread safe.
    /// </summary>
    public class MuxRunOutcomeTracker
    {
        #region Public-Members

        /// <summary>
        /// The run statuses that mean the model answered: <c>completed</c> and <c>completed_with_errors</c>.
        /// </summary>
        public static readonly IReadOnlySet<string> SuccessfulRunStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "completed", "completed_with_errors"
        };

        /// <summary>
        /// Error codes that do not fail a run that went on to complete: a denied, blocked, or failed tool call and an
        /// approval error. Every other code (including codes this version does not know) is fatal.
        /// </summary>
        public static readonly IReadOnlySet<string> NonFatalErrorCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "tool_call_denied", "tool_call_blocked_by_hook", "approval_error", "tool_execution_error", "tool_timeout"
        };

        /// <summary>
        /// Longest provider message kept in a reason, in characters.
        /// </summary>
        public const int MaxReasonMessageChars = 300;

        /// <summary>
        /// Longest assistant turn kept for the rebuilt final reply, in characters. Older text of a longer turn is dropped
        /// so a model that never stops writing cannot grow memory without bound.
        /// </summary>
        public const int MaxTurnTextChars = 1024 * 1024;

        /// <summary>
        /// Whether a <c>run_completed</c> event arrived.
        /// </summary>
        public bool RunCompleted
        {
            get { lock (_Lock) return _RunStatus != null; }
        }

        /// <summary>
        /// The status the <c>run_completed</c> event reported, or null before it.
        /// </summary>
        public string? RunStatus
        {
            get { lock (_Lock) return _RunStatus; }
        }

        /// <summary>
        /// The model the run used (from <c>run_started</c>, or an error event), or null.
        /// </summary>
        public string? Model
        {
            get { lock (_Lock) return _Model; }
        }

        /// <summary>
        /// The run's iteration limit (from <c>run_started</c>), or null.
        /// </summary>
        public int? MaxIterations
        {
            get { lock (_Lock) return _MaxIterations; }
        }

        /// <summary>
        /// Whether any fatal error event arrived (see <see cref="IsFatalError"/>), before or after <c>run_completed</c>.
        /// </summary>
        public bool HasFatalError
        {
            get { lock (_Lock) return _HasFatalError; }
        }

        /// <summary>
        /// The code of the last error event, or null.
        /// </summary>
        public string? LastErrorCode
        {
            get { lock (_Lock) return _LastErrorCode; }
        }

        /// <summary>
        /// Whether the run succeeded: <c>run_completed</c> reported a successful status and no fatal error arrived.
        /// </summary>
        public bool Succeeded
        {
            get
            {
                lock (_Lock)
                {
                    return _RunStatus != null && SuccessfulRunStatuses.Contains(_RunStatus) && !_HasFatalError;
                }
            }
        }

        /// <summary>
        /// The final reply rebuilt from the stream: the assistant_text deltas of the last assistant turn that wrote text
        /// (a turn ends at a tool call or a heartbeat), trimmed; or null when the run wrote no text.
        /// </summary>
        public string? FinalAssistantText
        {
            get
            {
                lock (_Lock)
                {
                    string current = _CurrentTurn.ToString();
                    string? text = !String.IsNullOrWhiteSpace(current) ? current : _LastTurnText;
                    if (String.IsNullOrWhiteSpace(text)) return null;
                    return text!.Trim();
                }
            }
        }

        #endregion

        #region Private-Members

        private static readonly IReadOnlySet<string> _NonFatalFailureCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "approval", "tool"
        };

        private static readonly Regex _MaxIterationsMessage = new Regex(@"maximum of (?<count>\d+) iterations", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex _LlmErrorStatus = new Regex(@"failed with status (?<status>[A-Za-z0-9]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly object _Lock = new object();
        private readonly StringBuilder _CurrentTurn = new StringBuilder();
        private bool _TurnClosed = false;
        private string? _LastTurnText = null;
        private string? _RunStatus = null;
        private string? _Model = null;
        private int? _MaxIterations = null;
        private bool _HasFatalError = false;
        private string? _LastErrorCode = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate for one run.
        /// </summary>
        public MuxRunOutcomeTracker()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Observe one stdout line of the run. Lines that are not Mux protocol events are ignored.
        /// </summary>
        /// <param name="line">The line.</param>
        /// <returns>The provider error the line reports (an error event, or a failed run status not already explained
        /// by a fatal error event), or null.</returns>
        public RuntimeProviderError? ObserveLine(string? line)
        {
            if (!MuxProtocolEvent.TryParse(line, out MuxProtocolEvent? evt) || evt == null) return null;
            MuxProtocolEventDetails? details = null;
            if (evt.EventType == MuxProtocolEvent.RunStarted || evt.EventType == MuxProtocolEvent.Error)
                MuxProtocolEventDetails.TryParse(line, out details);

            lock (_Lock)
            {
                switch (evt.EventType)
                {
                    case MuxProtocolEvent.RunStarted:
                        if (!String.IsNullOrWhiteSpace(evt.Model)) _Model = evt.Model!.Trim();
                        if (details?.MaxIterations != null) _MaxIterations = details.MaxIterations;
                        return null;

                    case MuxProtocolEvent.AssistantText:
                        if (_TurnClosed)
                        {
                            _CurrentTurn.Clear();
                            _TurnClosed = false;
                        }

                        if (!String.IsNullOrEmpty(evt.Text))
                        {
                            _CurrentTurn.Append(evt.Text);
                            // A degenerate model can stream text without end; keep the most recent part of the turn.
                            if (_CurrentTurn.Length > MaxTurnTextChars) _CurrentTurn.Remove(0, _CurrentTurn.Length - MaxTurnTextChars);
                        }
                        return null;

                    case MuxProtocolEvent.ToolCallProposed:
                    case MuxProtocolEvent.ToolCallApproved:
                    case MuxProtocolEvent.ToolCallCompleted:
                    case MuxProtocolEvent.Heartbeat:
                        CloseTurn();
                        return null;

                    case MuxProtocolEvent.Error:
                        {
                            if (_Model == null && !String.IsNullOrWhiteSpace(evt.Model)) _Model = evt.Model!.Trim();
                            string? code = String.IsNullOrWhiteSpace(evt.Code) ? null : evt.Code!.Trim();
                            _LastErrorCode = code;
                            if (IsFatalError(code, details?.FailureCategory)) _HasFatalError = true;
                            return BuildError(code, evt.Message, _MaxIterations);
                        }

                    case MuxProtocolEvent.RunCompleted:
                        {
                            CloseTurn();
                            _RunStatus = String.IsNullOrWhiteSpace(evt.Status) ? "unknown" : evt.Status!.Trim();
                            if (SuccessfulRunStatuses.Contains(_RunStatus) || _HasFatalError) return null;

                            // A failed status no fatal error event explained (an older mux, or a status without its own
                            // error event): report the status itself.
                            RuntimeProviderError error = new RuntimeProviderError();
                            error.ErrorType = _RunStatus;
                            error.Message = "mux run ended with status " + _RunStatus;
                            error.Reason = DescribeStatus(_RunStatus, _MaxIterations);
                            return error;
                        }

                    default:
                        return null;
                }
            }
        }

        /// <summary>
        /// The exit code to report to Armada for the process's exit: 0 in place of mux's error exits (1 and 2) when the
        /// run succeeded (see <see cref="Succeeded"/>); 1 in place of exit 0 when a <c>run_completed</c> event reported a
        /// failed status or a fatal error arrived (the structured outcome wins over the process code); otherwise the exit
        /// code unchanged. Exit 0 without any <c>run_completed</c> (an older mux, or text mode) stays 0.
        /// </summary>
        /// <param name="exitCode">The process exit code, or null when unknown.</param>
        /// <returns>The exit code to report.</returns>
        public int? ResolveExitCode(int? exitCode)
        {
            if (!exitCode.HasValue) return exitCode;
            if ((exitCode.Value == 1 || exitCode.Value == 2) && Succeeded) return 0;
            if (exitCode.Value == 0 && (HasFatalError || (RunCompleted && !Succeeded))) return 1;
            return exitCode;
        }

        /// <summary>
        /// Whether an error event fails the run even when it goes on to complete. Tool and approval errors (by code, or
        /// by mux's failure category) are not fatal; every other code is, including codes this version does not know
        /// and an event without a code.
        /// </summary>
        /// <param name="code">Error code, or null.</param>
        /// <param name="failureCategory">Mux's failure category, or null.</param>
        /// <returns>True when the error is fatal.</returns>
        public static bool IsFatalError(string? code, string? failureCategory)
        {
            if (!String.IsNullOrWhiteSpace(code) && NonFatalErrorCodes.Contains(code!.Trim())) return false;
            if (!String.IsNullOrWhiteSpace(failureCategory) && _NonFatalFailureCategories.Contains(failureCategory!.Trim())) return false;
            return true;
        }

        /// <summary>
        /// A human-readable reason for a mux error event, ending with its code in parentheses (for example
        /// "mux stopped: reached the 50-iteration limit (max_iterations_reached)").
        /// </summary>
        /// <param name="code">Error code, or null.</param>
        /// <param name="message">Mux's message, or null.</param>
        /// <param name="maxIterations">The run's iteration limit, when known.</param>
        /// <returns>The reason.</returns>
        public static string DescribeError(string? code, string? message, int? maxIterations)
        {
            string trimmedCode = String.IsNullOrWhiteSpace(code) ? "error" : code!.Trim();
            string detail = Shorten(message);
            string suffix = " (" + trimmedCode + ")";
            switch (trimmedCode)
            {
                case "max_iterations_reached":
                    {
                        int? limit = maxIterations;
                        if (!String.IsNullOrEmpty(message))
                        {
                            Match match = _MaxIterationsMessage.Match(message!);
                            if (match.Success && Int32.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)) limit = parsed;
                        }

                        return limit.HasValue
                            ? "mux stopped: reached the " + limit.Value.ToString(CultureInfo.InvariantCulture) + "-iteration limit" + suffix
                            : "mux stopped: reached its iteration limit" + suffix;
                    }
                case "budget_exceeded":
                    return "mux stopped: the run exceeded its token budget" + suffix;
                case "degenerate_output":
                    return "mux stopped: the model's output degenerated into repetition" + suffix;
                case "context_limit_exceeded":
                    return "mux stopped: the conversation no longer fits the model's context window" + suffix;
                case "llm_connection_error":
                    return "mux could not reach the model endpoint" + WithDetail(detail) + suffix;
                case "llm_error":
                    return "the model endpoint rejected the request" + WithDetail(detail) + suffix;
                case "llm_stream_error":
                    return "mux lost the model's response stream" + WithDetail(detail) + suffix;
                case "tool_call_denied":
                    return "mux denied a tool call" + WithDetail(detail) + suffix;
                case "schema_validation_failed":
                    return "mux rejected the final answer" + WithDetail(detail) + suffix;
                case "cancelled":
                    return "the mux run was cancelled" + suffix;
                default:
                    return "mux error" + WithDetail(detail) + suffix;
            }
        }

        /// <summary>
        /// A human-readable reason for a failed mux run status (for example "mux stopped: the run failed (failed)").
        /// </summary>
        /// <param name="status">Run status.</param>
        /// <param name="maxIterations">The run's iteration limit, when known.</param>
        /// <returns>The reason.</returns>
        public static string DescribeStatus(string? status, int? maxIterations)
        {
            string trimmed = String.IsNullOrWhiteSpace(status) ? "unknown" : status!.Trim();
            switch (trimmed)
            {
                case "max_iterations_reached":
                case "budget_exceeded":
                    return DescribeError(trimmed, null, maxIterations);
                case "failed":
                    return "mux stopped: the run failed before the model answered (failed)";
                default:
                    return "mux stopped: the run ended with status " + trimmed + " (" + trimmed + ")";
            }
        }

        #endregion

        #region Private-Methods

        private void CloseTurn()
        {
            if (_TurnClosed) return;
            string current = _CurrentTurn.ToString();
            if (!String.IsNullOrWhiteSpace(current)) _LastTurnText = current;
            _TurnClosed = true;
        }

        private static RuntimeProviderError BuildError(string? code, string? message, int? maxIterations)
        {
            RuntimeProviderError error = new RuntimeProviderError();
            error.ErrorType = code;
            error.Message = String.IsNullOrWhiteSpace(message) ? null : message;
            error.Reason = DescribeError(code, message, maxIterations);

            // An HTTP failure of the model call: mux's llm_error message names the response status (a number, or the
            // status name), so authentication failures, rate limits, and a missing model classify as such.
            if (String.Equals(code, "llm_error", StringComparison.Ordinal) && !String.IsNullOrEmpty(message))
            {
                Match match = _LlmErrorStatus.Match(message!);
                if (match.Success) error.HttpStatusCode = ParseStatus(match.Groups["status"].Value);
            }

            return error;
        }

        private static int? ParseStatus(string value)
        {
            if (Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                return number >= 100 && number <= 599 ? number : (int?)null;
            if (Enum.TryParse<HttpStatusCode>(value, false, out HttpStatusCode named) && Enum.IsDefined(typeof(HttpStatusCode), named))
            {
                int status = (int)named;
                return status >= 100 && status <= 599 ? status : (int?)null;
            }

            return null;
        }

        private static string WithDetail(string detail)
        {
            return String.IsNullOrEmpty(detail) ? String.Empty : ": " + detail;
        }

        private static string Shorten(string? message)
        {
            if (String.IsNullOrWhiteSpace(message)) return String.Empty;
            string flat = message!.Replace("\r", " ").Replace("\n", " ").Trim();
            if (flat.EndsWith(".", StringComparison.Ordinal)) flat = flat.Substring(0, flat.Length - 1);
            if (flat.Length > MaxReasonMessageChars) flat = flat.Substring(0, MaxReasonMessageChars - 3).TrimEnd() + "...";
            return flat;
        }

        #endregion
    }
}
