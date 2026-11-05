namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Seeds review evidence and settles a real dispatched Challenger before rescue assertions.</summary>
    public static class SettledJudgeChallengeFixture
    {
        /// <summary>Give the failed review a scored blocker and distinct eligible Judge and Challenger models.</summary>
        public static async Task<string> PrepareAsync(TestDatabase database, Mission failed)
        {
            Captain judge = await database.Driver.Captains.CreateAsync(new Captain("review-source")
            {
                TenantId = failed.TenantId, UserId = failed.UserId,
                Model = "gpt-5.6-luna", Tier = CaptainTierEnum.Premium, State = CaptainStateEnum.Idle
            }).ConfigureAwait(false);
            Captain challenger = await database.Driver.Captains.CreateAsync(new Captain("review-challenger")
            {
                TenantId = failed.TenantId, UserId = failed.UserId,
                Model = "claude-opus-5-5", Tier = CaptainTierEnum.Premium, State = CaptainStateEnum.Idle,
                AllowedPersonas = JsonSerializer.Serialize(new[] { "Challenger" })
            }).ConfigureAwait(false);
            failed.CaptainId = judge.Id;
            failed.AgentOutput = "## Findings\n[ARMADA:FINDING] {\"id\":\"F1\",\"text\":\"Required behavior needs correction\",\"frequency\":2,\"severity\":3,\"criterion\":null,\"rule\":null}\n[ARMADA:VERDICT] NEEDS_REVISION";
            failed.ReviewComment = failed.AgentOutput;
            await database.Driver.Missions.UpdateAsync(failed).ConfigureAwait(false);
            JudgeFindingTriageResult decision = JudgeFindingTriage.Evaluate(failed.AgentOutput, 5);
            decision.EffectiveVerdict = "NeedsRevision";
            await database.Driver.Events.CreateAsync(new ArmadaEvent(JudgeFindingTriage.AppliedEvent, "Applied scored review")
            {
                TenantId = failed.TenantId, UserId = failed.UserId,
                EntityType = "mission", EntityId = failed.Id, MissionId = failed.Id,
                VesselId = failed.VesselId, VoyageId = failed.VoyageId, Payload = JsonSerializer.Serialize(decision)
            }).ConfigureAwait(false);
            return challenger.Id;
        }

        /// <summary>Complete only an actually dispatched Challenger with one evidenced upheld ruling.</summary>
        public static async Task CompleteAsync(TestDatabase database, Mission dispatched, string captainId)
        {
            Mission challenger = await database.Driver.Missions.ReadAsync(dispatched.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Dispatched Challenger is missing");
            if (challenger.Persona != "Challenger") throw new InvalidOperationException("Expected a real Challenger dispatch before rescue");
            challenger.CaptainId = captainId;
            challenger.Status = MissionStatusEnum.Complete;
            challenger.CompletedUtc = DateTime.UtcNow;
            challenger.AgentOutput = "[ARMADA:CHALLENGE] {\"findings\":[{\"index\":1,\"kind\":\"DEFECT\",\"ruling\":\"UPHOLD\",\"confidence\":0.9,\"evidence\":\"The reviewed change lacks the required behavior\"}]}\n[ARMADA:RESULT] COMPLETE";
            await database.Driver.Missions.UpdateAsync(challenger).ConfigureAwait(false);
        }
    }
}
