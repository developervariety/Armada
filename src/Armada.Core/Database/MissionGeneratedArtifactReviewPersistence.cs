namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>Conditional storage of a regenerated candidate and its same-record Judge review.</summary>
    internal static class MissionGeneratedArtifactReviewPersistence
    {
        internal static async Task<bool> TryWriteAsync(DbConnection connection, DatabaseTypeEnum provider,
            Mission expected, GeneratedArtifactReviewBinding next, bool prepareReview, CancellationToken token)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (expected.AdmissionWriteStamp == null)
                throw new InvalidOperationException("generated_candidate_loaded_snapshot_required");
            if (!String.Equals(expected.Persona, "Judge", StringComparison.OrdinalIgnoreCase)
                || expected.HeldForOperatorReview || expected.ProcessId.HasValue)
                return false;
            GeneratedArtifactReviewBinding? prior = expected.GeneratedArtifactReview;
            if (prepareReview)
            {
                if (next.ApprovedUtc != null || next.PreparationAttempt != (prior?.PreparationAttempt ?? 0) + 1
                    || !Same(next.SourceCommit, expected.CommitHash)
                    || (expected.Status != MissionStatusEnum.WorkProduced && expected.Status != MissionStatusEnum.Complete
                        && expected.Status != MissionStatusEnum.LandingFailed)) return false;
            }
            else
            {
                if (prior == null || prior.ApprovedUtc != null || next.ApprovedUtc == null
                    || !Same(expected.CommitHash, prior.CandidateCommit)
                    || expected.Status != MissionStatusEnum.WorkProduced
                    || !Same(prior.SourceCommit, next.SourceCommit) || !Same(prior.CandidateCommit, next.CandidateCommit)
                    || !Same(prior.TargetBaseCommit, next.TargetBaseCommit)
                    || prior.PreparationAttempt != next.PreparationAttempt || prior.RequestedUtc != next.RequestedUtc)
                    return false;
            }
            if (String.IsNullOrWhiteSpace(next.SourceCommit) || String.IsNullOrWhiteSpace(next.CandidateCommit)
                || String.IsNullOrWhiteSpace(next.TargetBaseCommit) || next.PreparationAttempt < 1
                || next.RequestedUtc.Kind != DateTimeKind.Utc) return false;

            string? previous = prior == null ? null : JsonSerializer.Serialize(prior);
            string json = JsonSerializer.Serialize(next);
            string? preserved = prepareReview
                ? MissionService.AppendPreservedRun(expected.PreservedRunOutput, expected.AgentOutput, "generated_candidate_review")
                : expected.PreservedRunOutput;
            DateTime now = DateTime.UtcNow;
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE missions SET generated_artifact_review_json=@next, last_update_utc=@now";
                if (prepareReview)
                    command.CommandText += ", status='Pending', mission_assignment_state='Pending', commit_hash=@candidate, start_from_ref=@candidate,"
                        + " preserved_run_output=@preserved, agent_output=NULL, diff_snapshot=NULL, failure_reason=NULL,"
                        + " captain_id=NULL, process_id=NULL, process_started_utc=NULL, harbor_job_id=NULL,"
                        + " started_utc=NULL, completed_utc=NULL, total_runtime_ms=NULL";
                command.CommandText += " WHERE id=@id AND status=@status AND last_update_utc=@stamp"
                    + " AND process_id IS NULL AND held_for_operator_review=@not_held"
                    + " AND recovery_attempts=@recovery AND landing_retry_count=@landing"
                    + " AND " + EqualSql("generated_artifact_review_json", "@previous", provider)
                    + " AND " + EqualSql("commit_hash", "@source", provider)
                    + " AND " + EqualSql("branch_name", "@branch", provider)
                    + " AND " + EqualSql("tenant_id", "@tenant", provider)
                    + " AND " + EqualSql("user_id", "@user", provider) + ";";
                StoredParameters parameters = StoredValueBinder.For(provider).For(command, "missions");
                parameters.Text("@next", "generated_artifact_review_json", json)
                    .Text("@previous", "generated_artifact_review_json", previous)
                    .Text("@id", "id", expected.Id).Text("@status", "status", expected.Status.ToString())
                    .Text("@source", "commit_hash", expected.CommitHash).Text("@branch", "branch_name", expected.BranchName)
                    .Text("@tenant", "tenant_id", expected.TenantId).Text("@user", "user_id", expected.UserId)
                    .Int("@recovery", "recovery_attempts", expected.RecoveryAttempts)
                    .Int("@landing", "landing_retry_count", expected.LandingRetryCount)
                    .Bool("@not_held", "held_for_operator_review", false).Utc("@now", "last_update_utc", now);
                DbParameter stamp = command.CreateParameter();
                stamp.ParameterName = "@stamp";
                stamp.Value = expected.AdmissionWriteStamp;
                command.Parameters.Add(stamp);
                if (prepareReview)
                    parameters.Text("@candidate", "commit_hash", next.CandidateCommit)
                        .Text("@preserved", "preserved_run_output", preserved);
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = provider == DatabaseTypeEnum.Sqlite
                    ? await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false) : null)
                {
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        internal static bool SameBinding(Mission first, Mission second) =>
            String.Equals(JsonSerializer.Serialize(first.GeneratedArtifactReview),
                JsonSerializer.Serialize(second.GeneratedArtifactReview), StringComparison.Ordinal);

        private static bool Same(string? first, string? second) => String.Equals(first, second, StringComparison.Ordinal);

        private static string EqualSql(string column, string parameter, DatabaseTypeEnum provider)
        {
            if (provider == DatabaseTypeEnum.Postgresql) parameter = "CAST(" + parameter + " AS text)";
            string comparison = provider switch
            {
                DatabaseTypeEnum.Sqlite => "CAST(" + column + " AS BLOB)=CAST(" + parameter + " AS BLOB)",
                DatabaseTypeEnum.Postgresql => "convert_to(" + column + ", 'UTF8')=convert_to(" + parameter + ", 'UTF8')",
                DatabaseTypeEnum.Mysql => "BINARY " + column + "=BINARY " + parameter,
                DatabaseTypeEnum.SqlServer => "CONVERT(varbinary(max)," + column + ")=CONVERT(varbinary(max)," + parameter + ")",
                _ => throw new NotSupportedException()
            };
            return "((" + column + " IS NULL AND " + parameter + " IS NULL) OR " + comparison + ")";
        }
    }
}
