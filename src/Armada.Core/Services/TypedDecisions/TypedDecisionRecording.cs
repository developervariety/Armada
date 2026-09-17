namespace Armada.Core.Services
{
    using System;
    using System.Threading.Tasks;
    using SyslogLogging;

    /// <summary>
    /// The shared recording envelope for typed-decision adapters. A recorded event is observability only,
    /// so a recorder failure is logged and swallowed and never changes a decision's outcome. Every
    /// adapter — the single-verdict gate base and the multi-signal hold-outs alike — records through this
    /// one wrapper instead of re-copying the try/catch.
    /// </summary>
    internal static class TypedDecisionRecording
    {
        /// <summary>
        /// Run a recorder write, logging and swallowing any failure. Never throws.
        /// </summary>
        /// <param name="record">The recorder write to attempt.</param>
        /// <param name="logging">Logging module used to note a swallowed failure.</param>
        /// <param name="header">Log-line prefix identifying the adapter, and the decision when it carries one.</param>
        /// <returns>A task that completes when the write has been attempted.</returns>
        internal static async Task SafeRecordAsync(Func<Task> record, LoggingModule logging, string header)
        {
            try
            {
                await record().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logging.Warn(header + "event record failed: " + ex.Message);
            }
        }
    }
}
