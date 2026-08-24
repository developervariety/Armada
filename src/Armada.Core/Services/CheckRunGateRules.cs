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

        /// <summary>
        /// True when a Passed Check measured a commit other than the one under review, so its green
        /// vouches for older work and must not decide the gate for the newer work.
        /// </summary>
        /// <remarks>
        /// A voyage-armed Check is stamped once, at the first stage that commits, and the stages
        /// that follow keep committing on top. A Judge then reviews the tip while the only green
        /// record measured a commit several stages back -- in the worst case a planner commit that
        /// never landed. A gate that reads Status alone cannot see that; comparing the record's
        /// commit to the reviewed one can.
        /// <para>
        /// A voyage-attached green that carries NO commit is stale too. Build and UnitTest run in a
        /// checkout resolved from the record, so a record executed without a branch measured the
        /// vessel's default branch, never the work under review; the rescue path once ran its
        /// checks that way, seconds after dispatch and before the rescue Worker had started. A
        /// record attached to no voyage is left alone, because nothing re-arms it.
        /// </para>
        /// </remarks>
        /// <param name="run">The Check to test. Null returns false.</param>
        /// <param name="reviewedCommit">The commit under review. Null or empty returns false.</param>
        /// <returns>True when the record is a green for a different commit, or for no commit at all.</returns>
        public static bool IsStale(CheckRun? run, string? reviewedCommit)
        {
            if (!ParticipatesInRealSignalGate(run)) return false;
            if (run!.Status != CheckRunStatusEnum.Passed) return false;
            if (String.IsNullOrWhiteSpace(reviewedCommit)) return false;
            if (String.IsNullOrWhiteSpace(run.CommitHash)) return !String.IsNullOrWhiteSpace(run.VoyageId);
            return !SameCommit(run.CommitHash, reviewedCommit);
        }

        /// <summary>
        /// True when two commit identifiers name the same commit. Either side may be abbreviated,
        /// so the shorter is compared as a prefix of the longer; an abbreviation shorter than seven
        /// characters is too weak to match anything.
        /// </summary>
        /// <param name="left">A full or abbreviated commit hash.</param>
        /// <param name="right">A full or abbreviated commit hash.</param>
        /// <returns>True when both name the same commit.</returns>
        public static bool SameCommit(string? left, string? right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right)) return false;
            string a = left.Trim();
            string b = right.Trim();
            int shortest = Math.Min(a.Length, b.Length);
            if (shortest < 7) return false;
            return String.Compare(a, 0, b, 0, shortest, StringComparison.OrdinalIgnoreCase) == 0;
        }

        #endregion
    }
}
