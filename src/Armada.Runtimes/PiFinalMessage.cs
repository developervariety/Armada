namespace Armada.Runtimes
{
    using System;
    using Armada.Core.Protocol;

    /// <summary>
    /// Final-message capture and exit-code resolution for <c>pi -p --mode json</c>. pi exits 0 in JSON mode even when the
    /// provider fails, the output limit is hit, or the run is aborted, so the stream decides the outcome: the run
    /// succeeded only when it reached its end (<c>agent_end</c> without a retry, or <c>agent_settled</c>) and the last
    /// assistant message stopped normally. On success the last assistant reply text is written to the launch's
    /// final-message file (when the file is missing or empty), as the other runtimes do.
    /// </summary>
    public sealed class PiFinalMessage
    {
        #region Public-Members

        /// <summary>
        /// Exit code reported for a run pi ended with exit 0 that the stream shows failed.
        /// </summary>
        public const int FailedRunExitCode = 1;

        /// <summary>
        /// Most characters kept; a longer reply keeps its end, where the result and markers are.
        /// </summary>
        public const int MaxCharacters = 524288;

        #endregion

        #region Private-Members

        private readonly object _Lock = new object();
        private string? _Path = null;
        private string _LastText = String.Empty;
        private string? _LastStopReason = null;
        private bool _SawRunEnd = false;
        private bool _Aborted = false;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start a launch: remember its final-message file and drop any earlier state.
        /// </summary>
        /// <param name="finalMessageFilePath">The launch's final-message file, or null.</param>
        public void Begin(string? finalMessageFilePath)
        {
            lock (_Lock)
            {
                _Path = String.IsNullOrEmpty(finalMessageFilePath) ? null : finalMessageFilePath;
                _LastText = String.Empty;
                _LastStopReason = null;
                _SawRunEnd = false;
                _Aborted = false;
            }
        }

        /// <summary>
        /// Observe one stdout line. Non-protocol lines are ignored.
        /// </summary>
        /// <param name="line">The line.</param>
        public void Observe(string? line)
        {
            if (!PiStreamEvent.TryParse(line, out PiStreamEvent? evt) || evt == null) return;
            lock (_Lock)
            {
                PiMessage? assistant = evt.AssistantMessageEnd;
                if (assistant != null)
                {
                    _LastStopReason = assistant.StopReason;
                    string text = assistant.Text.Trim();
                    if (text.Length > 0) _LastText = text.Length > MaxCharacters ? text.Substring(text.Length - MaxCharacters) : text;
                    // A response that pi retries after an error starts a new run; the run end is only final when
                    // agent_end says no retry follows.
                    _SawRunEnd = false;
                    return;
                }

                if (evt.IsRunEnd)
                {
                    _SawRunEnd = true;
                    if (evt.Aborted == true) _Aborted = true;
                }
            }
        }

        /// <summary>
        /// The last assistant reply text seen, trimmed (empty when none).
        /// </summary>
        /// <returns>The reply text.</returns>
        public string Text()
        {
            lock (_Lock) return _LastText;
        }

        /// <summary>
        /// The stop reason of the last assistant message, or null.
        /// </summary>
        /// <returns>The stop reason.</returns>
        public string? LastStopReason()
        {
            lock (_Lock) return _LastStopReason;
        }

        /// <summary>
        /// Resolve the launch's exit code and, on success, write the final message. A nonzero or unknown exit code is
        /// returned unchanged. Exit 0 becomes <see cref="FailedRunExitCode"/> when the stream never reached the run end
        /// (stopped or crashed before <c>agent_end</c>), the run was aborted, no assistant message arrived, or the last
        /// assistant message stopped with <c>error</c>, <c>aborted</c> or <c>length</c>.
        /// </summary>
        /// <param name="exitCode">The process exit code, or null when unknown.</param>
        /// <returns>The resolved exit code.</returns>
        public int? Complete(int? exitCode)
        {
            if (exitCode != 0) return exitCode;
            string? path;
            string text;
            bool failed;
            lock (_Lock)
            {
                path = _Path;
                text = _LastText;
                failed = !_SawRunEnd
                    || _Aborted
                    || _LastStopReason == null
                    || String.Equals(_LastStopReason, PiMessage.StopReasonError, StringComparison.Ordinal)
                    || String.Equals(_LastStopReason, PiMessage.StopReasonAborted, StringComparison.Ordinal)
                    || String.Equals(_LastStopReason, PiMessage.StopReasonLength, StringComparison.Ordinal);
            }

            if (failed) return FailedRunExitCode;
            MuxRuntime.WriteFinalMessageIfMissing(path, text);
            return exitCode;
        }

        #endregion
    }
}
