namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for the one rule that lets the autonomy layer clear a scheduler pause it did not
    /// set. The case this exists for: a deploying session paused the scheduler, its deploy was
    /// satisfied by a peer, the session never returned, and the next autonomous cycle could
    /// measure the stranded pause exactly and could not act on it.
    /// </summary>
    public class StalePauseRuleTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Stale Pause Rule";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            DateTime now = new DateTime(2026, 8, 24, 20, 0, 0, DateTimeKind.Utc);

            await RunTest("A scheduler that is not paused has nothing to clear", () =>
            {
                StalePauseDecision d = StalePauseRule.Evaluate(false, "peer", now.AddHours(-2), null, now, 30);
                AssertFalse(d.CanClear);
                AssertContains("not paused", d.Reason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A pause with no recorded owner is never cleared by the autonomy layer", () =>
            {
                StalePauseDecision noKey = StalePauseRule.Evaluate(true, null, now.AddHours(-2), null, now, 30);
                StalePauseDecision noTime = StalePauseRule.Evaluate(true, "peer", null, null, now, 30);
                AssertFalse(noKey.CanClear, "No participant key: an operator must clear it.");
                AssertFalse(noTime.CanClear, "No set time: the absence cannot be measured.");
                AssertContains("operator", noKey.Reason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An owner inside the absence threshold keeps its pause, at every minute up to and including the threshold", () =>
            {
                for (int minutes = 0; minutes <= 30; minutes++)
                {
                    StalePauseDecision d = StalePauseRule.Evaluate(true, "peer", now.AddHours(-3), now.AddMinutes(-minutes), now, 30);
                    AssertFalse(d.CanClear, "Absent " + minutes + " minutes must not clear a 30-minute threshold.");
                    AssertEqual(minutes, (int)Math.Floor(d.MeasuredAbsence!.Value.TotalMinutes));
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An owner absent longer than the threshold has a stale pause", () =>
            {
                StalePauseDecision d = StalePauseRule.Evaluate(true, "peer", now.AddHours(-3), now.AddMinutes(-31), now, 30);
                AssertTrue(d.CanClear);
                AssertContains("peer", d.Reason);
                AssertContains("stale", d.Reason);
                AssertEqual(31, (int)Math.Floor(d.MeasuredAbsence!.Value.TotalMinutes));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An owner never seen on the board is measured from the pause time itself", () =>
            {
                StalePauseDecision fresh = StalePauseRule.Evaluate(true, "peer", now.AddMinutes(-10), null, now, 30);
                StalePauseDecision old = StalePauseRule.Evaluate(true, "peer", now.AddMinutes(-45), null, now, 30);
                AssertFalse(fresh.CanClear, "A 10-minute-old pause by a never-seen owner is not stale.");
                AssertTrue(old.CanClear, "A 45-minute-old pause by a never-seen owner is stale.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A heartbeat older than the pause does not shorten the measured absence", () =>
            {
                // The owner was seen an hour before it paused; the pause is the later signal.
                StalePauseDecision d = StalePauseRule.Evaluate(true, "peer", now.AddMinutes(-20), now.AddMinutes(-80), now, 30);
                AssertFalse(d.CanClear);
                AssertEqual(20, (int)Math.Floor(d.MeasuredAbsence!.Value.TotalMinutes));
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
