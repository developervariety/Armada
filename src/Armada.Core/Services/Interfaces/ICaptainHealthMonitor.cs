namespace Armada.Core.Services.Interfaces
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Tracks per-captain near-instant exit-code-1 failures and bench deadlines in memory.
    /// </summary>
    public interface ICaptainHealthMonitor
    {
        /// <summary>
        /// Records a runtime exit and returns whether the captain should be benched.
        /// Does not mutate bench state; the caller invokes <see cref="MarkBenched"/> when appropriate.
        /// </summary>
        CaptainHealthDecision RecordExit(string captainId, AgentRuntimeEnum runtime, int? exitCode, long runtimeMs);

        /// <summary>
        /// Records a bench deadline for the captain until restore sweep clears it.
        /// </summary>
        void MarkBenched(string captainId, DateTime benchedUntilUtc);

        /// <summary>
        /// Returns true when the captain has an active bench deadline entry.
        /// </summary>
        bool IsBenched(string captainId);

        /// <summary>
        /// Returns captain identifiers whose bench deadline has elapsed at <paramref name="nowUtc"/>.
        /// </summary>
        IReadOnlyList<string> GetElapsedBenched(DateTime nowUtc);

        /// <summary>
        /// Clears the bench deadline and resets the failure counter for the captain.
        /// </summary>
        void ClearBench(string captainId);

        /// <summary>
        /// Resets only the consecutive failure counter for the captain.
        /// </summary>
        void Reset(string captainId);
    }
}
