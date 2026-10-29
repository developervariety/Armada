namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// Folds the tool events of one chat turn into the ordered list of tool calls the turn made. A started
    /// call and its completion share an identifier and become one entry. Safe for concurrent use.
    /// </summary>
    public sealed class ChatToolCallRecorder
    {
        #region Private-Members

        // A turn that fans out into hundreds of calls must not make its stored response unbounded.
        private const int _MaxCalls = 100;
        private const int _MaxArgumentChars = 4000;
        private const int _MaxResultChars = 8000;
        private readonly object _Lock = new object();
        private readonly List<ChatToolCall> _Calls = new List<ChatToolCall>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record one tool event.
        /// </summary>
        /// <param name="phase">"started" or "completed".</param>
        /// <param name="id">Call identifier.</param>
        /// <param name="name">Tool name.</param>
        /// <param name="arguments">Call arguments, or null.</param>
        /// <param name="ok">True for success, false for failure, null when unknown.</param>
        /// <param name="elapsedMs">Elapsed milliseconds, or null.</param>
        /// <param name="result">Call result, or null.</param>
        public void Record(string? phase, string? id, string? name, string? arguments, bool? ok, double? elapsedMs, string? result)
        {
            if (String.IsNullOrEmpty(id)) return;
            lock (_Lock)
            {
                ChatToolCall? existing = _Calls.Find(call => String.Equals(call.Id, id, StringComparison.Ordinal));
                if (String.Equals(phase, "started", StringComparison.Ordinal))
                {
                    if (existing != null || _Calls.Count >= _MaxCalls) return;
                    _Calls.Add(new ChatToolCall { Id = id, Name = String.IsNullOrEmpty(name) ? "tool" : name, Arguments = Clip(arguments, _MaxArgumentChars) });
                }
                else if (String.Equals(phase, "completed", StringComparison.Ordinal))
                {
                    if (existing == null)
                    {
                        if (_Calls.Count >= _MaxCalls) return;
                        existing = new ChatToolCall { Id = id };
                        _Calls.Add(existing);
                    }
                    if (!String.IsNullOrEmpty(name)) existing.Name = name;
                    else if (String.IsNullOrEmpty(existing.Name)) existing.Name = "tool";
                    if (arguments != null) existing.Arguments = Clip(arguments, _MaxArgumentChars);
                    existing.Status = ok == false ? "failed" : "success";
                    existing.Result = Clip(result, _MaxResultChars);
                    existing.ElapsedMs = elapsedMs;
                }
            }
        }

        /// <summary>
        /// Copy the calls recorded so far.
        /// </summary>
        /// <returns>The calls in call order.</returns>
        public List<ChatToolCall> Snapshot()
        {
            lock (_Lock)
            {
                List<ChatToolCall> copy = new List<ChatToolCall>(_Calls.Count);
                foreach (ChatToolCall call in _Calls)
                    copy.Add(new ChatToolCall { Id = call.Id, Name = call.Name, Status = call.Status, Arguments = call.Arguments, Result = call.Result, ElapsedMs = call.ElapsedMs });
                return copy;
            }
        }

        #endregion

        #region Private-Methods

        private static string? Clip(string? value, int max)
        {
            if (String.IsNullOrEmpty(value) || value.Length <= max) return value;
            return value.Substring(0, max) + "... (truncated)";
        }

        #endregion
    }
}
