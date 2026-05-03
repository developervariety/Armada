namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Pure parser over an Architect captain's AgentOutput markdown.
    /// Extracts the plan-level narrative + N [ARMADA:MISSION] blocks into
    /// structured form. No I/O, no DI dependencies.
    /// </summary>
    public sealed class ArchitectOutputParser : IArchitectOutputParser
    {
        private static readonly HashSet<string> _KnownTierValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PreferredModelTierSelector.LowTier,
            PreferredModelTierSelector.MidTier,
            PreferredModelTierSelector.HighTier,
            "quick",
            "medium",
        };

        private static readonly Regex _MissionIdShape = new Regex(@"^M[0-9]+$", RegexOptions.Compiled);

        /// <summary>Parses the Architect captain's AgentOutput. Returns structured result with verdict.</summary>
        public ArchitectParseResult Parse(string agentOutput)
        {
            ArchitectParseResult result = new ArchitectParseResult();

            if (string.IsNullOrWhiteSpace(agentOutput))
            {
                result.Verdict = ArchitectParseVerdict.StructuralFailure;
                result.Errors.Add(new ArchitectParseError("empty_output", "", "", "AgentOutput was null/empty"));
                return result;
            }

            int blockedIdx = agentOutput.IndexOf("[ARMADA:RESULT] BLOCKED", StringComparison.Ordinal);
            if (blockedIdx >= 0)
            {
                result.Verdict = ArchitectParseVerdict.Blocked;
                string tail = agentOutput.Substring(blockedIdx);
                foreach (string line in tail.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r').Trim();
                    if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                        result.BlockedQuestions.Add(trimmed.Substring(2).Trim());
                }
                return result;
            }

            int firstMission = agentOutput.IndexOf("[ARMADA:MISSION]", StringComparison.Ordinal);
            if (firstMission < 0)
            {
                result.Verdict = ArchitectParseVerdict.StructuralFailure;
                result.Errors.Add(new ArchitectParseError("no_mission_blocks", "", "", "No [ARMADA:MISSION] markers found"));
                return result;
            }

            string planMarkdown = agentOutput.Substring(0, firstMission).TrimEnd();
            string missionsRegion = agentOutput.Substring(firstMission);

            ArchitectPlan plan = new ArchitectPlan { FullMarkdown = planMarkdown };
            ExtractPlanSections(planMarkdown, plan);
            result.Plan = plan;

            List<ArchitectMissionEntry> missions = new List<ArchitectMissionEntry>();
            int cursor = 0;
            while (true)
            {
                int blockStart = missionsRegion.IndexOf("[ARMADA:MISSION]", cursor, StringComparison.Ordinal);
                if (blockStart < 0) break;
                int blockEnd = missionsRegion.IndexOf("[ARMADA:MISSION-END]", blockStart, StringComparison.Ordinal);
                if (blockEnd < 0)
                {
                    result.Errors.Add(new ArchitectParseError("unterminated_block", "", "", "[ARMADA:MISSION] without matching [ARMADA:MISSION-END]"));
                    break;
                }
                int bodyStart = blockStart + "[ARMADA:MISSION]".Length;
                string blockBody = missionsRegion.Substring(bodyStart, blockEnd - bodyStart);
                ArchitectMissionEntry entry = ParseBlockBody(blockBody);
                missions.Add(entry);
                cursor = blockEnd + "[ARMADA:MISSION-END]".Length;
            }

            HashSet<string> declaredIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArchitectMissionEntry entry in missions)
            {
                if (string.IsNullOrEmpty(entry.Id))
                    result.Errors.Add(new ArchitectParseError("missing_field", "?", "id", "Mission block missing id"));
                else if (!_MissionIdShape.IsMatch(entry.Id))
                    result.Errors.Add(new ArchitectParseError("bad_id_shape", entry.Id, "id", "id must match M<N>"));
                else
                    declaredIds.Add(entry.Id);

                if (string.IsNullOrEmpty(entry.Title))
                    result.Errors.Add(new ArchitectParseError("missing_field", entry.Id, "title", "missing title"));
                if (string.IsNullOrEmpty(entry.PreferredModel))
                    result.Errors.Add(new ArchitectParseError("missing_field", entry.Id, "preferredModel", "missing preferredModel"));
                else if (!_KnownTierValues.Contains(entry.PreferredModel))
                    result.Errors.Add(new ArchitectParseError("invalid_tier", entry.Id, "preferredModel", "preferredModel must be low, mid, or high (got: " + entry.PreferredModel + ")"));
                if (string.IsNullOrEmpty(entry.Description))
                    result.Errors.Add(new ArchitectParseError("missing_field", entry.Id, "description", "missing description"));
            }

            foreach (ArchitectMissionEntry entry in missions)
            {
                if (!string.IsNullOrEmpty(entry.DependsOnMissionAlias) && !declaredIds.Contains(entry.DependsOnMissionAlias))
                {
                    result.Errors.Add(new ArchitectParseError("unknown_depends_on", entry.Id, "dependsOnMissionId",
                        "references undefined mission " + entry.DependsOnMissionAlias));
                }
            }

            if (HasCycle(missions))
                result.Errors.Add(new ArchitectParseError("dispatch_graph_cycle", "", "dependsOnMissionId", "Cycle in dispatch graph"));

            result.Missions = missions;
            result.Verdict = result.Errors.Count > 0
                ? ArchitectParseVerdict.StructuralFailure
                : ArchitectParseVerdict.Valid;
            return result;
        }

        private static void ExtractPlanSections(string planMarkdown, ArchitectPlan plan)
        {
            Match goalM = Regex.Match(planMarkdown, @"\*\*Goal:\*\*\s+(.+)$", RegexOptions.Multiline);
            if (goalM.Success) plan.Goal = goalM.Groups[1].Value.Trim();

            Match archM = Regex.Match(planMarkdown, @"\*\*Architecture:\*\*\s+(.+?)(?=^\s*\*\*|\Z)", RegexOptions.Multiline | RegexOptions.Singleline);
            if (archM.Success) plan.Architecture = archM.Groups[1].Value.Trim();

            Match tsM = Regex.Match(planMarkdown, @"\*\*Tech Stack:\*\*\s+(.+?)(?=^\s*\*\*|\Z)", RegexOptions.Multiline | RegexOptions.Singleline);
            if (tsM.Success) plan.TechStack = tsM.Groups[1].Value.Trim();

            Match fsM = Regex.Match(planMarkdown, @"##\s+File structure\s*\n(.+?)(?=^##\s|\Z)", RegexOptions.Multiline | RegexOptions.Singleline);
            if (fsM.Success) plan.FileStructureMarkdown = fsM.Groups[1].Value.Trim();

            Match dgM = Regex.Match(planMarkdown, @"##\s+Task dispatch graph\s*\n(.+?)(?=^##\s|\Z)", RegexOptions.Multiline | RegexOptions.Singleline);
            if (dgM.Success) plan.DispatchGraphMarkdown = dgM.Groups[1].Value.Trim();
        }

        private static ArchitectMissionEntry ParseBlockBody(string blockBody)
        {
            ArchitectMissionEntry entry = new ArchitectMissionEntry();
            string[] lines = blockBody.Split('\n');

            int? descStartIdx = null;
            int prestagedListIdx = -1;
            int playbooksListIdx = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].TrimEnd('\r');
                string line = raw.TrimStart();

                if (descStartIdx == null)
                {
                    if (line.StartsWith("id:", StringComparison.Ordinal))
                        entry.Id = line.Substring(3).Trim();
                    else if (line.StartsWith("title:", StringComparison.Ordinal))
                        entry.Title = line.Substring(6).Trim();
                    else if (line.StartsWith("preferredModel:", StringComparison.Ordinal))
                        entry.PreferredModel = line.Substring("preferredModel:".Length).Trim();
                    else if (line.StartsWith("dependsOnMissionId:", StringComparison.Ordinal))
                        entry.DependsOnMissionAlias = line.Substring("dependsOnMissionId:".Length).Trim();
                    else if (line.StartsWith("prestagedFiles:", StringComparison.Ordinal))
                        prestagedListIdx = i;
                    else if (line.StartsWith("selectedPlaybooks:", StringComparison.Ordinal))
                        playbooksListIdx = i;
                    else if (line.StartsWith("description:", StringComparison.Ordinal))
                    {
                        string after = line.Substring("description:".Length).Trim();
                        if (after == "|")
                        {
                            descStartIdx = i + 1;
                            break;
                        }
                        else
                        {
                            entry.Description = after;
                        }
                    }
                }
            }

            if (descStartIdx.HasValue)
            {
                List<string> descLines = new List<string>();
                for (int i = descStartIdx.Value; i < lines.Length; i++)
                    descLines.Add(lines[i].TrimEnd('\r'));

                int minLead = int.MaxValue;
                foreach (string l in descLines)
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    int lead = 0;
                    while (lead < l.Length && l[lead] == ' ') lead++;
                    if (lead < minLead) minLead = lead;
                }
                if (minLead == int.MaxValue) minLead = 0;

                List<string> stripped = new List<string>();
                foreach (string l in descLines)
                {
                    if (l.Length >= minLead) stripped.Add(l.Substring(minLead));
                    else stripped.Add(l);
                }
                entry.Description = string.Join("\n", stripped).Trim();
            }

            ParseListEntries(lines, prestagedListIdx, (kvs) =>
            {
                string? sp = null;
                string? dp = null;
                kvs.TryGetValue("sourcePath", out sp);
                kvs.TryGetValue("destPath", out dp);
                if (!string.IsNullOrEmpty(sp))
                    entry.PrestagedFiles.Add(new PrestagedFile { SourcePath = sp, DestPath = dp ?? "" });
            });

            ParseListEntries(lines, playbooksListIdx, (kvs) =>
            {
                string? pid = null;
                string? dm = null;
                kvs.TryGetValue("playbookId", out pid);
                kvs.TryGetValue("deliveryMode", out dm);
                if (!string.IsNullOrEmpty(pid))
                {
                    PlaybookDeliveryModeEnum mode = PlaybookDeliveryModeEnum.InlineFullContent;
                    Enum.TryParse<PlaybookDeliveryModeEnum>(dm ?? "", out mode);
                    entry.SelectedPlaybooks.Add(new SelectedPlaybook { PlaybookId = pid, DeliveryMode = mode });
                }
            });

            return entry;
        }

        private static void ParseListEntries(string[] lines, int headerIdx, Action<Dictionary<string, string>> consumer)
        {
            if (headerIdx < 0) return;
            Dictionary<string, string>? current = null;
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                string raw = lines[i].TrimEnd('\r');
                string trimmed = raw.TrimStart();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    if (current != null) consumer(current);
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                    string kv = trimmed.Substring(2);
                    int colon = kv.IndexOf(':');
                    if (colon > 0) current[kv.Substring(0, colon).Trim()] = kv.Substring(colon + 1).Trim();
                    continue;
                }
                int leadSpaces = 0;
                while (leadSpaces < raw.Length && raw[leadSpaces] == ' ') leadSpaces++;
                if (current != null && leadSpaces >= 2)
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon > 0) current[trimmed.Substring(0, colon).Trim()] = trimmed.Substring(colon + 1).Trim();
                    continue;
                }
                break;
            }
            if (current != null) consumer(current);
        }

        private static bool HasCycle(List<ArchitectMissionEntry> missions)
        {
            Dictionary<string, string> deps = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ArchitectMissionEntry m in missions)
            {
                if (!string.IsNullOrEmpty(m.Id))
                    deps[m.Id] = m.DependsOnMissionAlias ?? "";
            }
            foreach (string start in deps.Keys)
            {
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                string current = start;
                while (!string.IsNullOrEmpty(current))
                {
                    if (!visited.Add(current)) return true;
                    string? next = null;
                    if (!deps.TryGetValue(current, out next)) break;
                    current = next ?? "";
                }
            }
            return false;
        }
    }
}
