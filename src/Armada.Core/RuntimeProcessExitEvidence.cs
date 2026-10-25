namespace Armada.Core
{
    using System;

    /// <summary>Trusted, in-memory finalization evidence for one runtime launch, never captain prose.</summary>
    public sealed class RuntimeProcessExitEvidence
    {
        private readonly object _Sync = new object();
        private DateTime? _ObservedExitUtc;
        private bool _Finalized;
        private int? _ExitCode;
        private bool _LogCaptureFailed;

        internal RuntimeProcessExitEvidence(DateTime? launchStartUtc) { LaunchStartUtc = launchStartUtc; }
        internal DateTime? LaunchStartUtc { get; }

        /// <summary>Publish the effective outcome only after output drain and log closure.</summary>
        public void Complete(int? exitCode, bool logCaptureFailed)
        {
            lock (_Sync)
            {
                if (_Finalized) return;
                _ExitCode = exitCode;
                _LogCaptureFailed = logCaptureFailed;
                _Finalized = true;
            }
        }

        /// <summary>Read the finalized outcome; an unfinished launch has no authoritative exit code.</summary>
        public bool TryGetOutcome(out int? exitCode, out bool logCaptureFailed)
        {
            lock (_Sync)
            {
                exitCode = _ExitCode;
                logCaptureFailed = _LogCaptureFailed;
                return _Finalized;
            }
        }

        /// <summary>Give an exited process a bounded interval to drain; never hide a stuck finalizer forever.</summary>
        public bool ShouldDefer(DateTime nowUtc)
        {
            lock (_Sync)
            {
                if (_Finalized) return false;
                _ObservedExitUtc ??= nowUtc;
                return nowUtc - _ObservedExitUtc.Value < TimeSpan.FromSeconds(30);
            }
        }
    }
}
