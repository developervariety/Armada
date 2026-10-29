namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Result of applying a mission's newest saved dock work to a checkout.
    /// </summary>
    public sealed class DockWorkRestore
    {
        #region Public-Members

        /// <summary>Outcome.</summary>
        public DockWorkRestoreStatusEnum Status { get; }

        /// <summary>Full ref name of the saved work that was applied or skipped; null when none exists.</summary>
        public string? RefName { get; }

        /// <summary>Human-readable outcome; for a skip, the reason.</summary>
        public string Message { get; }

        #endregion

        #region Constructors-and-Factories

        private DockWorkRestore(DockWorkRestoreStatusEnum status, string? refName, string message)
        {
            Status = status;
            RefName = refName;
            Message = message ?? String.Empty;
        }

        /// <summary>The mission has no saved dock work, or nothing can apply it.</summary>
        /// <returns>The result.</returns>
        public static DockWorkRestore NothingSaved()
        {
            return new DockWorkRestore(DockWorkRestoreStatusEnum.NothingSaved, null, "the mission has no saved dock work");
        }

        /// <summary>The saved work is in the checkout as uncommitted changes.</summary>
        /// <param name="refName">Full ref name of the saved work.</param>
        /// <returns>The result.</returns>
        public static DockWorkRestore Applied(string refName)
        {
            return new DockWorkRestore(DockWorkRestoreStatusEnum.Applied, refName, "applied the saved dock work from " + refName);
        }

        /// <summary>The saved work was not applied and the checkout is unchanged.</summary>
        /// <param name="refName">Full ref name of the saved work.</param>
        /// <param name="reason">Why nothing was applied.</param>
        /// <returns>The result.</returns>
        public static DockWorkRestore Skipped(string? refName, string reason)
        {
            return new DockWorkRestore(DockWorkRestoreStatusEnum.Skipped, refName, reason);
        }

        #endregion
    }
}
