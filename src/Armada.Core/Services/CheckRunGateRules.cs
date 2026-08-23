namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The single definition of which Check records carry real signal, shared by every gate that
    /// reads Check state.
    /// </summary>
    /// <remarks>
    /// A Check record is created before it is executed, so its existence and its signal are two
    /// different facts. A record that has never run carries no command output, so it can neither
    /// confirm nor deny anything about the work under review, and a gate that waits on one waits
    /// for an event that record cannot produce by itself.
    /// <para>
    /// These rules live here, and only here, because they are read by the Judge gate, the voyage
    /// completion gate, and the command-resolution path. A second copy is how two gates start to
    /// disagree about the same record.
    /// </para>
    /// </remarks>
    public static class CheckRunGateRules
    {
        #region Public-Members

        /// <summary>
        /// The command a Check carries before a real one is resolved from the workflow profile.
        /// A record still holding it has never executed.
        /// </summary>
        public const string PlaceholderCommand = "echo";

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the Check has no runnable command, so nothing can be executed for it until a
        /// workflow profile resolves one.
        /// </summary>
        /// <param name="run">The Check to test. Null returns false.</param>
        /// <returns>True when the command is absent or is the unresolved placeholder.</returns>
        public static bool HasUnresolvedCommand(CheckRun? run)
        {
            if (run == null) return false;
            return String.IsNullOrWhiteSpace(run.Command)
                || String.Equals(run.Command, PlaceholderCommand, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when the Check is an intent marker: armed to declare that a gate is wanted, never
        /// executed, and therefore holding no command output.
        /// </summary>
        /// <remarks>
        /// An armed record also carries no branch or commit, so executing it would test the
        /// vessel's default branch rather than the work under review. Such a record must not be
        /// read as signal in either direction: not as a green that vouches for the work, and not
        /// as an unresolved result that a gate can wait on.
        /// </remarks>
        /// <param name="run">The Check to test. Null returns false.</param>
        /// <returns>True when the record is Pending, never started, and has no runnable command.</returns>
        public static bool IsUnexecutedIntentMarker(CheckRun? run)
        {
            if (run == null) return false;
            if (run.Status != CheckRunStatusEnum.Pending) return false;
            if (run.StartedUtc != null) return false;
            return HasUnresolvedCommand(run);
        }

        /// <summary>
        /// True when the Check may decide a gate. Canceled records are excluded because an
        /// operator has already ruled them out; intent markers are excluded because they have
        /// produced nothing to rule on.
        /// </summary>
        /// <param name="run">The Check to test. Null returns false.</param>
        /// <returns>True when the record participates in the real-signal gate.</returns>
        public static bool ParticipatesInRealSignalGate(CheckRun? run)
        {
            if (run == null) return false;
            if (run.Status == CheckRunStatusEnum.Canceled) return false;
            return !IsUnexecutedIntentMarker(run);
        }

        /// <summary>
        /// True when a participating Check has started but not reached a verdict, so a gate that
        /// needs its result must wait for it.
        /// </summary>
        /// <param name="run">The Check to test. Null returns false.</param>
        /// <returns>True when the record is genuinely unresolved.</returns>
        public static bool IsUnresolved(CheckRun? run)
        {
            if (!ParticipatesInRealSignalGate(run)) return false;
            return run!.Status == CheckRunStatusEnum.Pending || run.Status == CheckRunStatusEnum.Running;
        }

        #endregion
    }
}
