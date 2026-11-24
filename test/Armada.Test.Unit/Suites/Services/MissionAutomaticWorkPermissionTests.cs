namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Attempt, source, expiry and action isolation for the opt-in automatic-work barrier.</summary>
    public sealed class MissionAutomaticWorkPermissionTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Automatic Work Permission";

        private static readonly DateTime _Now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Run the policy controls; service-boundary tests separately prove actual admission.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A legacy mission keeps both automatic actions", () =>
            {
                Mission mission = new Mission("Legacy work");
                AssertTrue(Allowed(mission, MissionAutomaticActionEnum.Verification), "legacy verification");
                AssertTrue(Allowed(mission, MissionAutomaticActionEnum.DependentAdmission), "legacy dependent admission");
            });

            await RunTest("Opting in without grants holds both actions", () =>
            {
                Mission mission = NewMission();
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "verification requires permission");
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.DependentAdmission), "dependent admission requires separate permission");
            });

            await RunTest("Verification permission cannot release a dependent or approve Review", () =>
            {
                Mission mission = NewMission();
                mission.RequiresReview = true;
                mission.Status = MissionStatusEnum.Review;
                mission.AutomaticWorkPermission!.Verification = Grant(mission);
                AssertTrue(Allowed(mission, MissionAutomaticActionEnum.Verification), "current verification grant");
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.DependentAdmission), "verification is not downstream authority");
                AssertTrue(mission.RequiresReview, "manual review duty unchanged");
                AssertEqual(MissionStatusEnum.Review, mission.Status, "permission does not accept the review");
                AssertNull(mission.ReviewedUtc, "permission writes no review decision");
            });

            await RunTest("Dependent permission cannot authorize verification", () =>
            {
                Mission mission = NewMission();
                mission.AutomaticWorkPermission!.DependentAdmission = Grant(mission);
                AssertTrue(Allowed(mission, MissionAutomaticActionEnum.DependentAdmission), "current downstream grant");
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "downstream permission is not verification authority");
            });

            await RunTest("A grant cannot be borrowed by a different mission or owner", () =>
            {
                Mission original = NewMission();
                Mission other = NewMission();
                other.AutomaticWorkPermission!.Verification = Grant(original);
                AssertFalse(Allowed(other, MissionAutomaticActionEnum.Verification), "other mission cannot borrow grant");
                original.AutomaticWorkPermission!.Verification = Grant(original);
                original.UserId = "different-owner";
                AssertFalse(Allowed(original, MissionAutomaticActionEnum.Verification), "owner binding changed");
                original.UserId = "owner";
                original.TenantId = "different-tenant";
                AssertFalse(Allowed(original, MissionAutomaticActionEnum.Verification), "tenant binding changed");
            });

            await RunTest("Restart and source changes invalidate the old grant", () =>
            {
                Mission mission = NewMission();
                mission.AutomaticWorkPermission!.Verification = Grant(mission);
                mission.StartedUtc = null;
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "restart cleared launch identity");
                mission.StartedUtc = _Now.AddMinutes(-1);
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "new launch is different");
                mission.StartedUtc = _Now.AddMinutes(-2);
                mission.CommitHash = new string('b', 40);
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "new source is different");
                mission.CommitHash = null;
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "missing source is not authority");
            });

            await RunTest("Expiry is exclusive and a future grant is not active", () =>
            {
                Mission mission = NewMission();
                MissionAutomaticWorkGrant grant = Grant(mission);
                mission.AutomaticWorkPermission!.Verification = grant;
                grant.ExpiresUtc = _Now;
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "exact expiry cannot admit new work");
                grant.GrantedUtc = _Now.AddSeconds(1);
                grant.ExpiresUtc = _Now.AddMinutes(1);
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "future issue time is not active");
                grant.GrantedUtc = _Now;
                grant.ExpiresUtc = _Now;
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "invalid interval cannot admit");
            });

            await RunTest("Unknown or unreadable stored permission fails closed", () =>
            {
                Mission mission = NewMission();
                mission.AutomaticWorkPermission!.Verification = Grant(mission);
                mission.AutomaticWorkPermission.Version = 2;
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "unknown format");
                mission.AutomaticWorkPermission = null;
                mission.AutomaticWorkPermissionInvalid = true;
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "invalid present JSON cannot become legacy behavior");
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.DependentAdmission), "invalid JSON holds both actions");
            });

            await RunTest("Revoking one action preserves the other action and existing history", () =>
            {
                Mission mission = NewMission();
                mission.AutomaticWorkPermission!.Verification = Grant(mission);
                mission.AutomaticWorkPermission.DependentAdmission = Grant(mission);
                mission.RecoveryAttempts = 1;
                mission.PreservedRunOutput = "Earlier failed attempt";
                mission.AutomaticWorkPermission.Verification = null;
                AssertFalse(Allowed(mission, MissionAutomaticActionEnum.Verification), "revoked verification");
                AssertTrue(Allowed(mission, MissionAutomaticActionEnum.DependentAdmission), "other grant retained");
                AssertEqual(1, mission.RecoveryAttempts, "counter retained");
                AssertEqual("Earlier failed attempt", mission.PreservedRunOutput, "history retained");
            });
        }

        private static bool Allowed(Mission mission, MissionAutomaticActionEnum action) =>
            MissionAutomaticWorkPermissionPolicy.IsAllowed(mission, action, _Now, out _);

        private static Mission NewMission() => new Mission("Permission-controlled work")
        {
            TenantId = "tenant",
            UserId = "owner",
            StartedUtc = _Now.AddMinutes(-2),
            CommitHash = new string('a', 40),
            AutomaticWorkPermission = new MissionAutomaticWorkPermission()
        };

        private static MissionAutomaticWorkGrant Grant(Mission mission) => new MissionAutomaticWorkGrant
        {
            MissionId = mission.Id,
            TenantId = mission.TenantId,
            UserId = mission.UserId,
            StartedUtc = mission.StartedUtc!.Value,
            CommitHash = mission.CommitHash!,
            GrantedByUserId = "operator",
            GrantedUtc = _Now.AddMinutes(-1),
            ExpiresUtc = _Now.AddMinutes(5)
        };
    }
}
