namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Where an unresolved check run stands for the host-wide build and test slot.
    /// </summary>
    public class CheckRunSlotWait
    {
        #region Public-Members

        /// <summary>
        /// Guidance for a captain or Judge reading a check that has not finished.
        /// </summary>
        public const string WaitGuidance =
            "This check has not finished because the host runs a limited number of full build or test suites at once, "
            + "and starts the next one only while enough memory is free (MemoryWaitReason names a memory wait, and QueueReason "
            + "what a check the automatic runner has not started waits for: a place in the runner, a running gate, or a "
            + "voyage stage that can still commit). "
            + "The wait is an environment condition: it is not a code defect and not a question for the owner, "
            + "so do not end the stage BLOCKED or return NEEDS_REVISION because of it. The check starts when the condition "
            + "QueueReason names ends; read it again with get_check_run. A Judge PASS given while a Check is unresolved is "
            + "held until the Check passes at the reviewed commit.";

        /// <summary>
        /// WaitingForSlot while the check waits for the slot, Running while it holds it, or Queued when it has not asked yet.
        /// </summary>
        public string State { get; set; } = String.Empty;

        /// <summary>
        /// Place in line for the slot (1 is granted next), or null when the check is not waiting for it.
        /// </summary>
        public int? Position { get; set; } = null;

        /// <summary>
        /// When the check asked for the slot, or null when it has not asked yet.
        /// </summary>
        public DateTime? WaitingSinceUtc { get; set; } = null;

        /// <summary>
        /// What holds the slots now, every holder in grant order separated by "; ", or null when every slot is
        /// free or this check holds one.
        /// </summary>
        public string? HolderDescription { get; set; } = null;

        /// <summary>
        /// When the earliest current holder was granted its slot, or null.
        /// </summary>
        public DateTime? HolderSinceUtc { get; set; } = null;

        /// <summary>
        /// Why the queue waits although a slot is free, such as
        /// "waiting for free memory: 4,100 MB available, floor 6,144 MB", or null when this check is not waiting
        /// or memory holds no request.
        /// </summary>
        public string? MemoryWaitReason { get; set; } = null;

        /// <summary>
        /// Why a check that has not asked for the slot still waits: behind a running definition-of-done gate
        /// that runs the same suite, for a voyage stage that can still commit on top of the work it measures,
        /// or for a place in the automatic check runner, such as "waiting for a place
        /// in the automatic check runner: 4 of 4 places hold started checks, ...". Null when nothing holds it.
        /// </summary>
        public string? QueueReason { get; set; } = null;

        /// <summary>
        /// Guidance for a captain or Judge; see <see cref="WaitGuidance"/>.
        /// </summary>
        public string Guidance { get; set; } = WaitGuidance;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Describe where an unresolved check stands for the host-wide slot.
        /// </summary>
        /// <param name="run">The check run.</param>
        /// <param name="slot">A snapshot of the host-wide slot.</param>
        /// <returns>The wait, or null when the check has finished.</returns>
        public static CheckRunSlotWait? Describe(CheckRun run, HostSlotSnapshot? slot)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (run.Status == CheckRunStatusEnum.Running)
            {
                return new CheckRunSlotWait { State = "Running" };
            }

            if (run.Status != CheckRunStatusEnum.Pending) return null;

            CheckRunSlotWait wait = new CheckRunSlotWait
            {
                State = run.SlotRequestedUtc.HasValue ? "WaitingForSlot" : "Queued",
                WaitingSinceUtc = run.SlotRequestedUtc
            };

            if (!run.SlotRequestedUtc.HasValue && run.Source == CheckRunSourceEnum.Armada)
                wait.QueueReason = Armada.Core.Services.AutomaticCheckRunPlaces.DescribeQueueReason(run.Id);

            if (slot != null)
            {
                HostSlotWaiter? waiter = slot.FindWaiter(run.Id);
                if (waiter != null)
                {
                    wait.State = "WaitingForSlot";
                    wait.QueueReason = null;
                    wait.Position = waiter.Position;
                    wait.WaitingSinceUtc ??= waiter.RequestedUtc;

                    // Every waiter stands behind the first request in line, so a memory hold on it holds this check too.
                    if (slot.HeldForMemory) wait.MemoryWaitReason = slot.MemoryWaitReason;
                }

                bool runHoldsSlot = String.Equals(slot.HolderKey, run.Id, StringComparison.Ordinal) || slot.FindHolder(run.Id) != null;
                if (!runHoldsSlot)
                {
                    wait.HolderDescription = DescribeHolders(slot);
                    wait.HolderSinceUtc = slot.HolderSinceUtc;
                }
            }

            return wait;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Name every holder in grant order, or the single holder when the snapshot lists none.
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
