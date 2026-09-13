namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Bound optional dock evidence before storage and after deserialization.</summary>
    internal static class DockGitAnchorPersistence
    {
        internal const int MaxBytes = 32768;

        internal static string Serialize(DockGitAnchorSnapshot snapshot)
        {
            Validate(snapshot);
            string json = JsonSerializer.Serialize(snapshot);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidOperationException("Dock anchor snapshot exceeds its byte limit.");
            return json;
        }

        internal static DockGitAnchorSnapshot? Read(object value, string dockId, string vesselId)
        {
            if (value == DBNull.Value) return null;
            string json = (string)value;
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) return null;
            try
            {
                DockGitAnchorSnapshot? snapshot = JsonSerializer.Deserialize<DockGitAnchorSnapshot>(json);
                if (snapshot == null) return null;
                Validate(snapshot);
                if (snapshot.DockId != dockId || snapshot.VesselId != vesselId) return null;
                snapshot.StoredJson = json;
                return snapshot;
            }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        internal static void Add(DbCommand command, Dock dock)
        {
            DockGitAnchorSnapshot? snapshot = dock.GitAnchorsSnapshot;
            if (snapshot != null && (snapshot.DockId != dock.Id || snapshot.VesselId != dock.VesselId))
                throw new InvalidOperationException("Dock anchor association does not match the dock.");
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@git_anchors_json";
            parameter.DbType = DbType.String;
            parameter.Value = snapshot == null ? DBNull.Value : Serialize(snapshot);
            command.Parameters.Add(parameter);
        }

        internal static bool IsCommit(string? value) => value != null
            && Regex.IsMatch(value, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant);

        internal static bool IsRelativePath(string? value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 1024 || value.StartsWith('/') || value.StartsWith('\\')) return false;
            if (Regex.IsMatch(value, @"\A[A-Za-z]:", RegexOptions.CultureInvariant)) return false;
            foreach (char character in value) if (Char.IsControl(character)) return false;
            foreach (string segment in value.Replace('\\', '/').Split('/')) if (segment == "..") return false;
            return true;
        }

        private static void Validate(DockGitAnchorSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Version != 1 || !Enum.IsDefined(snapshot.State)
                || !Identifier(snapshot.DockId) || !Identifier(snapshot.MissionId) || !Identifier(snapshot.VesselId)
                || !IsCommit(snapshot.ProvisionedCommit) || snapshot.Anchors == null
                || snapshot.Anchors.BaseCommit != snapshot.ProvisionedCommit || snapshot.ProvisionedUtc == default
                || snapshot.Anchors.TargetBranch.Length > 1024
                || (snapshot.Anchors.TargetTip.Length > 0 && !IsCommit(snapshot.Anchors.TargetTip))
                || !ErrorCode(snapshot.ErrorCode) || !ErrorCode(snapshot.Anchors.ResolutionError))
                throw new InvalidOperationException("Invalid dock anchor snapshot.");
            if (snapshot.State != DockGitAnchorStateEnum.Seeded && snapshot.ResolvedUtc == null)
                throw new InvalidOperationException("Resolved dock anchors require an observation time.");
            if (snapshot.State == DockGitAnchorStateEnum.Complete
                && (snapshot.Truncated || snapshot.ErrorCode != null || snapshot.Anchors.ResolutionError != null))
                throw new InvalidOperationException("Incomplete dock anchors cannot be marked complete.");
            if (snapshot.Anchors.Files.Count > 8 || snapshot.Anchors.PriorArt.Count > 6)
                throw new InvalidOperationException("Dock anchor count limit exceeded.");
            foreach (GitAnchorFileHistory file in snapshot.Anchors.Files)
            {
                if (file == null || !IsRelativePath(file.Path)
                    || (file.RequestedPath.Length > 0 && !IsRelativePath(file.RequestedPath)) || file.Commits.Count > 5)
                    throw new InvalidOperationException("Invalid dock anchor path.");
                foreach (GitAnchorCommit commit in file.Commits)
                    if (commit == null || !IsCommit(commit.Sha) || commit.Subject.Length > 123 || commit.DateUtc.Length > 32)
                        throw new InvalidOperationException("Invalid dock anchor history.");
            }
            foreach (GitAnchorPriorArt prior in snapshot.Anchors.PriorArt)
            {
                if (prior == null || prior.Term.Length > 120 || prior.SampleLocations.Count > 3)
                    throw new InvalidOperationException("Invalid dock anchor search.");
                foreach (string location in prior.SampleLocations)
                {
                    int separator = location?.LastIndexOf(':') ?? -1;
                    if (separator < 1 || !IsRelativePath(location!.Substring(0, separator))
                        || !Int32.TryParse(location.Substring(separator + 1), out int line) || line < 1)
                        throw new InvalidOperationException("Invalid dock anchor sample location.");
                }
            }
        }

        private static bool Identifier(string? value) => !String.IsNullOrEmpty(value) && value.Length <= 900;
        private static bool ErrorCode(string? value) => value == null
            || Regex.IsMatch(value, @"\A[a-z0-9_-]{1,80}\z", RegexOptions.CultureInvariant);
    }
}
