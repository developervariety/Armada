namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>
    /// Provider-backed proof that the reconciliation-marker migration backfill marks exactly the Failed and
    /// Cancelled missions whose failure reason the terminal-voyage reason rule recognises, records the code that
    /// closes the reason, and changes nothing when it runs again.
    /// </summary>
    internal sealed class TerminalVoyageReconciledMarkerBackfillTests
    {
        #region Private-Members

        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        private static readonly string[] _Reasons =
        {
            "Voyage ended Failed and the mission's work is not on the default branch (terminal_voyage_commit_absent)",
            "Voyage ended Failed and the mission's work is not on the default branch (terminal_voyage_work_unlanded)",
            "Voyage ended Cancelled and the mission's work is not on the default branch (terminal_voyage_no_commit)",
            "Voyage ended Failed and the mission's work is not on the default branch (terminal_voyage_work_unlanded); previous reason: Judge verdict: FAIL",
            "Voyage ended Failed and the mission's work is not on the default branch (terminal_voyage_no_commit); previous reason: a; previous reason: (terminal_voyage_commit_absent)",
            "Agent process exited with code 1",
            "terminal_voyage_work_unlanded",
            "(terminal_voyage_no_commit)",
            "Voyage ended Failed (TERMINAL_VOYAGE_NO_COMMIT)",
            "Voyage ended Failed (terminal_voyage_no_commit) ",
            "Judge verdict: FAIL; previous reason: Voyage ended Failed and the mission's work is not on the default branch (terminal_voyage_no_commit)"
        };

        #endregion

        #region Constructors-and-Factories

        internal TerminalVoyageReconciledMarkerBackfillTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        #endregion

        #region Internal-Methods

        internal async Task VerifyAsync(CancellationToken token)
        {
            List<string> backfill = BackfillStatements();
            DatabaseAssert.Equal(3, backfill.Count, "One backfill statement per unlanded reason code for " + _Settings.Type);

            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("reconciled-marker", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "reconciled-marker", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "reconciled-marker", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "reconciled-marker", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "reconciled-marker", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "reconciled-marker", token).ConfigureAwait(false);
                DateTime completed = DateTime.UtcNow.AddHours(-3);

                MissionStatusEnum[] statuses = { MissionStatusEnum.Failed, MissionStatusEnum.Cancelled, MissionStatusEnum.LandingFailed, MissionStatusEnum.Complete };
                List<Mission> seeded = new List<Mission>();
                foreach (string reason in _Reasons)
                {
                    foreach (MissionStatusEnum status in statuses)
                    {
                        Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "reconciled-marker", token,
                            configure: item =>
                            {
                                item.Status = status;
                                item.FailureReason = reason;
                                item.CompletedUtc = status == MissionStatusEnum.Cancelled ? null : completed;
                            }).ConfigureAwait(false);
                        Mission stored = DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false), "Seeded mission");
                        DatabaseAssert.True(!stored.ReconciledUtc.HasValue, "A seeded mission carries no marker before the backfill");
                        seeded.Add(stored);
                    }
                }

                Dictionary<string, DateTime?> firstPass = new Dictionary<string, DateTime?>();
                for (int pass = 0; pass < 2; pass++)
                {
                    await ExecuteAsync(backfill, token).ConfigureAwait(false);
                    foreach (Mission seed in seeded)
                    {
                        Mission after = DatabaseAssert.NotNull(await _Driver.Missions.ReadAsync(seed.Id, token).ConfigureAwait(false), "Backfilled mission");
                        bool expected = (seed.Status == MissionStatusEnum.Failed || seed.Status == MissionStatusEnum.Cancelled)
                            && TerminalVoyageMissionRule.IsReconciledFailureReason(seed.FailureReason);
                        string label = _Settings.Type + " " + seed.Status + " / " + seed.FailureReason;
                        DatabaseAssert.Equal(expected, after.ReconciledUtc.HasValue, "Backfill agrees with the reason rule: " + label);
                        DatabaseAssert.Equal(expected, TerminalVoyageMissionRule.IsReconciledOutcome(after), "Backfilled outcome agrees with the reason rule: " + label);
                        if (expected)
                        {
                            string head = seed.FailureReason!.Split(TerminalVoyageMissionRule.PreviousReasonSeparator)[0];
                            DatabaseAssert.True(head.EndsWith("(" + after.ReconciledReason + ")", StringComparison.Ordinal), "The recorded code closes the reason: " + label);
                            DatabaseAssert.Equal(after.CompletedUtc ?? after.LastUpdateUtc, after.ReconciledUtc!.Value, "The marker time is the completion time, or the last update when none: " + label);
                        }
                        else
                        {
                            DatabaseAssert.True(after.ReconciledReason == null, "An unmarked row keeps no reason code: " + label);
                        }
                        DatabaseAssert.Equal(seed.FailureReason, after.FailureReason, "The backfill never changes the reason text: " + label);

                        if (pass == 0) firstPass[seed.Id] = after.ReconciledUtc;
                        else DatabaseAssert.Equal(firstPass[seed.Id], after.ReconciledUtc, "A repeated backfill changes nothing: " + label);
                    }
                }
            }
            finally
            {
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        #endregion

        #region Private-Methods

        private List<string> BackfillStatements()
        {
            string[] statements = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => Armada.Core.Database.Sqlite.Queries.TableQueries.MigrationV100Statements,
                DatabaseTypeEnum.Postgresql => Armada.Core.Database.Postgresql.Queries.TableQueries.MigrationV101Statements,
                DatabaseTypeEnum.Mysql => Armada.Core.Database.Mysql.Queries.TableQueries.MigrationV92Statements,
                DatabaseTypeEnum.SqlServer => Armada.Core.Database.SqlServer.Queries.TableQueries.MigrationV95Statements,
                _ => throw new NotSupportedException("Unsupported database type " + _Settings.Type)
            };
            return statements.Where(statement => statement.StartsWith("UPDATE ", StringComparison.Ordinal)).ToList();
        }

        private async Task ExecuteAsync(List<string> statements, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                foreach (string statement in statements)
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = statement;
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        #endregion
    }
}
