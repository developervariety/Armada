namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Where a mission's definition-of-done gate stands for the host-wide build and test slot. A gate takes the
    /// slot at its first command and holds it until the evaluation ends, so a gate that is registered but holds no
    /// slot has not reached its first command yet (it is preparing or waiting for host load to fall). This mirrors
    /// <see cref="CheckRunSlotWait"/> so a queued gate is as visible as a queued check: an operator reading a gate
    /// can tell one that waits in line from one that is lost, without inspecting its dock.
    /// </summary>
    public class GateSlotWait
    {
        #region Public-Members

        /// <summary>
        /// Guidance for an operator reading a gate that has not finished.
        /// </summary>
        public const string WaitGuidance =
            "This gate has not finished because the host runs a limited number of full build or test suites at once, "
            + "and starts the next one only while enough memory is free (MemoryWaitReason names a memory wait). "
            + "A gate in WaitingForSlot or Queued is in line, not lost: Position names its place and the slot is granted "
            + "in request order. The wait is an environment condition, not a code defect; the gate runs by itself.";

        /// <summary>
        /// Running while the gate holds a slot and a suite executes, WaitingForSlot while it waits in line for one,
        /// or Queued when it is registered but has not reached its first command yet.
        /// </summary>
        public string State { get; set; } = String.Empty;

        /// <summary>
        /// Place in line for the slot (1 is granted next), or null when the gate is not waiting for it.
        /// </summary>
        public int? Position { get; set; } = null;

        /// <summary>
        /// When the gate asked for the slot, or null when it is not waiting for it.
        /// </summary>
        public DateTime? WaitingSinceUtc { get; set; } = null;

        /// <summary>
        /// What holds the slots now, every holder in grant order separated by "; ", or null when every slot is
        /// free or this gate holds one.
        /// </summary>
        public string? HolderDescription { get; set; } = null;

        /// <summary>
        /// When the earliest current holder was granted its slot, or null.
        /// </summary>
        public DateTime? HolderSinceUtc { get; set; } = null;

        /// <summary>
        /// Why the queue waits although a slot is free, such as
        /// "waiting for free memory: 4,100 MB available, floor 6,144 MB", or null when this gate is not waiting
        /// or memory holds no request.
        /// </summary>
        public string? MemoryWaitReason { get; set; } = null;

        /// <summary>
        /// Guidance for an operator; see <see cref="WaitGuidance"/>.
        /// </summary>
        public string Guidance { get; set; } = WaitGuidance;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Describe where a mission's registered gate stands for the host-wide slot.
        /// </summary>
        /// <param name="missionId">The mission whose gate is registered.</param>
        /// <param name="slot">A snapshot of the host-wide slot.</param>
        /// <returns>The wait; never null for a registered gate.</returns>
        public static GateSlotWait Describe(string missionId, HostSlotSnapshot? slot)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            GateSlotWait wait = new GateSlotWait();
            if (slot == null)
            {
                wait.State = "Queued";
                return wait;
            }

            if (slot.FindHolder(missionId) != null)
            {
                wait.State = "Running";
                return wait;
            }

            HostSlotWaiter? waiter = slot.FindWaiter(missionId);
            if (waiter != null)
            {
                wait.State = "WaitingForSlot";
                wait.Position = waiter.Position;
                wait.WaitingSinceUtc = waiter.RequestedUtc;

                // Every waiter stands behind the first request in line, so a memory hold on it holds this gate too.
                if (slot.HeldForMemory) wait.MemoryWaitReason = slot.MemoryWaitReason;
            }
            else
            {
                wait.State = "Queued";
            }

            wait.HolderDescription = DescribeHolders(slot);
            wait.HolderSinceUtc = slot.Holders.Count > 0 ? slot.HolderSinceUtc : null;
            return wait;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Name every holder in grant order, or the single holder when the snapshot lists none, or null when every
        /// slot is free.
        /// </summary>
        private static string? DescribeHolders(HostSlotSnapshot slot)
        {
            if (slot.Holders.Count == 0) return slot.HolderDescription;
            List<string> descriptions = new List<string>();
            foreach (HostSlotHolder holder in slot.Holders)
            {
                if (!String.IsNullOrEmpty(holder.Description)) descriptions.Add(holder.Description);
            }

            return descriptions.Count == 0 ? slot.HolderDescription : String.Join("; ", descriptions);
        }

        #endregion
    }
}
