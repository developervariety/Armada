namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// In-memory monitor for near-instant exit-code-1 crash loops and captain bench deadlines.
    /// </summary>
    public sealed class CaptainHealthMonitor : ICaptainHealthMonitor
    {
        #region Public-Methods

        /// <inheritdoc />
        public CaptainHealthDecision RecordExit(string captainId, AgentRuntimeEnum runtime, int? exitCode, long runtimeMs)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));

            if (!_Settings.Enabled)
            {
                return new CaptainHealthDecision
                {
                    ShouldBench = false,
                    ConsecutiveInstantFailures = 0,
                    Reason = string.Empty,
                    Runtime = runtime
                };
            }

            long maxRuntimeMs = (long)_Settings.MaxRuntimeSeconds * 1000L;
            bool isNearInstantFailure = exitCode == 1 && runtimeMs < maxRuntimeMs;
            int count;

            if (isNearInstantFailure)
            {
                count = _FailureCounters.AddOrUpdate(captainId, 1, (_, current) => current + 1);
                _Logging.Info(_Header + "near-instant exit recorded captainId=" + captainId + " count=" + count);
            }
            else
            {
                _FailureCounters.AddOrUpdate(captainId, 0, (_, _) => 0);
                count = 0;
            }

            bool shouldBench = count >= _Settings.FailureThreshold;
            string reason = string.Empty;

            if (shouldBench)
            {
                reason = count + " consecutive near-instant exit-1 launches (runtime < " + maxRuntimeMs + " ms) -- suspected usage limit";
                _Logging.Warn(_Header + "bench threshold reached captainId=" + captainId + " count=" + count);
            }

            return new CaptainHealthDecision
            {
                ShouldBench = shouldBench,
                ConsecutiveInstantFailures = count,
                Reason = reason,
                Runtime = runtime
            };
        }

        /// <inheritdoc />
        public void MarkBenched(string captainId, DateTime benchedUntilUtc)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));

            _BenchDeadlines[captainId] = benchedUntilUtc;
            _Logging.Info(_Header + "captain benched captainId=" + captainId + " untilUtc=" + benchedUntilUtc.ToString("O"));
        }

        /// <inheritdoc />
        public bool IsBenched(string captainId)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));

            return _BenchDeadlines.ContainsKey(captainId);
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetElapsedBenched(DateTime nowUtc)
        {
            List<string> elapsed = new List<string>();

            foreach (KeyValuePair<string, DateTime> entry in _BenchDeadlines)
            {
                if (entry.Value <= nowUtc)
                {
                    elapsed.Add(entry.Key);
                }
            }

            return elapsed;
        }

        /// <inheritdoc />
        public void ClearBench(string captainId)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));

            _BenchDeadlines.TryRemove(captainId, out _);
            _FailureCounters.AddOrUpdate(captainId, 0, (_, _) => 0);
            _Logging.Info(_Header + "bench cleared captainId=" + captainId);
        }

        /// <inheritdoc />
        public void Reset(string captainId)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));

            _FailureCounters.AddOrUpdate(captainId, 0, (_, _) => 0);
            _Logging.Info(_Header + "failure counter reset captainId=" + captainId);
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Creates a monitor with the given settings and logging.
        /// </summary>
        public CaptainHealthMonitor(CrashLoopDetectionSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Private-Members

        private readonly CrashLoopDetectionSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly ConcurrentDictionary<string, int> _FailureCounters = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, DateTime> _BenchDeadlines = new ConcurrentDictionary<string, DateTime>();
        private const string _Header = "[CaptainHealthMonitor] ";

        #endregion
    }
}
