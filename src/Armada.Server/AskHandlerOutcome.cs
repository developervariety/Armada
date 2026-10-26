namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal sealed class AskHandlerOutcome
    {
        internal bool Failed { get; set; }
        internal string? JobId { get; set; }
        internal List<AskWorkReference> WorkReferences { get; } = new List<AskWorkReference>();

        internal static AskHandlerOutcome Read(object? result)
        {
            AskHandlerOutcome outcome = new AskHandlerOutcome();
            if (result is LongRunningJob job)
            {
                outcome.JobId = job.JobId;
                outcome.Add(AskWorkKindEnum.Job, job.JobId);
                return outcome;
            }
            if (result is Mission mission) { outcome.Add(AskWorkKindEnum.Mission, mission.Id); return outcome; }
            if (result is Voyage voyage)
            {
                outcome.Add(AskWorkKindEnum.Voyage, voyage.Id);
                if (voyage is VoyageDispatchResponse dispatched)
                    foreach (DispatchedMissionStartRef reference in dispatched.MissionStartRefs.Take(32))
                        outcome.Add(AskWorkKindEnum.Mission, reference.MissionId);
                return outcome;
            }
            if (result == null) return outcome;
            string text = JsonSerializer.Serialize(result, result.GetType());
            if (text.Length > 1048576) throw new InvalidOperationException("The tool result exceeds the work-link limit.");
            AskReturnedIds? returned = JsonSerializer.Deserialize<AskReturnedIds>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (returned == null) return outcome;
            outcome.Failed = returned.Success == false || !String.IsNullOrWhiteSpace(returned.Error);
            outcome.Add(AskWorkKindEnum.Mission, returned.MissionId);
            outcome.Add(AskWorkKindEnum.Voyage, returned.VoyageId);
            if (returned.Id?.StartsWith("msn_", StringComparison.Ordinal) == true) outcome.Add(AskWorkKindEnum.Mission, returned.Id);
            if (returned.Id?.StartsWith("vyg_", StringComparison.Ordinal) == true) outcome.Add(AskWorkKindEnum.Voyage, returned.Id);
            if (!String.IsNullOrWhiteSpace(returned.JobId))
            {
                outcome.JobId = returned.JobId;
                outcome.Add(AskWorkKindEnum.Job, returned.JobId);
            }
            foreach (DispatchedMissionStartRef reference in (returned.MissionStartRefs ?? new List<DispatchedMissionStartRef>()).Take(32))
                outcome.Add(AskWorkKindEnum.Mission, reference.MissionId);
            return outcome;
        }

        internal void Add(AskWorkKindEnum kind, string? id)
        {
            string prefix = kind == AskWorkKindEnum.Mission ? "msn_" : kind == AskWorkKindEnum.Voyage ? "vyg_" : "job_";
            if (WorkReferences.Count >= 64 || String.IsNullOrWhiteSpace(id) || id.Length > 128 || !id.StartsWith(prefix, StringComparison.Ordinal)
                || !id.All(character => Char.IsAsciiLetterOrDigit(character) || character == '_' || character == '-')) return;
            if (!WorkReferences.Any(item => item.Kind == kind && item.Id == id))
                WorkReferences.Add(new AskWorkReference { Kind = kind, Id = id });
        }
    }

    internal sealed class AskReturnedIds
    {
        public string? Id { get; set; }
        public string? MissionId { get; set; }
        public string? VoyageId { get; set; }
        public string? JobId { get; set; }
        public string? Error { get; set; }
        public bool? Success { get; set; }
        public List<DispatchedMissionStartRef>? MissionStartRefs { get; set; }
    }
}
