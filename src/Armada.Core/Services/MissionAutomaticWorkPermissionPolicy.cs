namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Checks one freshly loaded permission without replacing review or other admission gates.</summary>
    public static class MissionAutomaticWorkPermissionPolicy
    {
        /// <summary>Named hold for an actual failed or unverified runtime exit, never a successful verdict.</summary>
        public const string UnexpectedExitHoldPrefix = "automatic_work_unexpected_exit:";

        /// <summary>Whether a durable unexpected-exit hold still requires an explicit operator rejection or retry.</summary>
        public static bool IsUnexpectedExitHeld(Mission mission) => mission.HeldForOperatorReview
            && (mission.HeldForOperatorReviewReason ?? String.Empty).StartsWith(UnexpectedExitHoldPrefix, StringComparison.Ordinal);

        /// <summary>Whether the current grant permits starting the requested action now.</summary>
        public static bool IsAllowed(Mission mission, MissionAutomaticActionEnum action, DateTime utcNow,
            out string? reason)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
            reason = null;
            if (mission.AutomaticWorkPermissionInvalid)
            {
                reason = "automatic_work_permission_invalid";
                return false;
            }

            if (IsUnexpectedExitHeld(mission))
            {
                reason = "automatic_work_unexpected_exit_held";
                return false;
            }

            MissionAutomaticWorkPermission? permission = mission.AutomaticWorkPermission;
            if (permission == null) return true;
            if (permission.Version != 1)
            {
                reason = "automatic_work_permission_invalid";
                return false;
            }

            MissionAutomaticWorkGrant? grant = action == MissionAutomaticActionEnum.Verification
                ? permission.Verification : permission.DependentAdmission;
            if (grant == null)
            {
                reason = action == MissionAutomaticActionEnum.Verification
                    ? "automatic_verification_permission_required" : "dependent_admission_permission_required";
                return false;
            }

            if (!String.Equals(grant.MissionId, mission.Id, StringComparison.Ordinal)
                || !String.Equals(grant.TenantId, mission.TenantId, StringComparison.Ordinal)
                || !String.Equals(grant.UserId, mission.UserId, StringComparison.Ordinal)
                || !mission.StartedUtc.HasValue || grant.StartedUtc != mission.StartedUtc.Value
                || !IsCommit(mission.CommitHash)
                || !String.Equals(grant.CommitHash, mission.CommitHash, StringComparison.Ordinal)
                || String.IsNullOrWhiteSpace(grant.GrantedByUserId)
                || grant.GrantedUtc > utcNow || grant.ExpiresUtc <= grant.GrantedUtc)
            {
                reason = "automatic_work_permission_stale";
                return false;
            }

            if (grant.ExpiresUtc <= utcNow)
            {
                reason = "automatic_work_permission_expired";
                return false;
            }
            return true;
        }

        private static bool IsCommit(string? commit)
        {
            if (commit == null || commit.Length != 40) return false;
            foreach (char character in commit)
            {
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f')) return false;
            }
            return true;
        }
    }
}
