namespace Armada.Core.Models
{
    using System;
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
            "This check has not finished because the host runs one full build or test suite at a time. "
            + "The wait is an environment condition: it is not a code defect and not a question for the owner, "
            + "so do not end the stage BLOCKED or return NEEDS_REVISION because of it. The check runs by itself; "
            + "read it again with get_check_run. A Judge PASS given while a Check is unresolved is held until the "
            + "Check passes at the reviewed commit.";

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
        /// What holds the slot now, or null when the slot is free or this check holds it.
        /// </summary>
        public string? HolderDescription { get; set; } = null;

        /// <summary>
        /// When the current holder was granted the slot, or null.
        /// </summary>
        public DateTime? HolderSinceUtc { get; set; } = null;

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

            if (slot != null)
            {
                HostSlotWaiter? waiter = slot.FindWaiter(run.Id);
                if (waiter != null)
                {
                    wait.State = "WaitingForSlot";
                    wait.Position = waiter.Position;
                    wait.WaitingSinceUtc ??= waiter.RequestedUtc;
                }

                if (!String.Equals(slot.HolderKey, run.Id, StringComparison.Ordinal))
                {
                    wait.HolderDescription = slot.HolderDescription;
                    wait.HolderSinceUtc = slot.HolderSinceUtc;
                }
            }

            return wait;
        }

        #endregion
    }
}
