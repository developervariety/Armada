namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>Reads and validates the opt-in versioned JSON plan format.</summary>
    public static partial class ArchitectVersionedPlanParser
    {
        private const string FenceInfo = "armada-plan";
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>Returns true when output contains an armada-plan fence, including an unclosed fence.</summary>
        /// <param name="output">Architect output.</param>
        public static bool ContainsPlanFence(string? output)
        {
            if (String.IsNullOrWhiteSpace(output)) return false;
            foreach (string line in output.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal)
                    && String.Equals(trimmed.Substring(3).Trim(), FenceInfo, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Parses the last versioned plan fence. Invalid structured output never falls back.</summary>
        /// <param name="output">Architect output containing a plan fence.</param>
        /// <param name="maxMissions">Mission cap applied after structural validation.</param>
        public static ArchitectParseResult Parse(string output, int maxMissions)
        {
            List<string> blocks = ExtractPlanBlocks(output, out bool unclosed);
            if (blocks.Count == 0 || unclosed)
                return Failure("invalid_plan_json", "", "armada-plan", "The final armada-plan fence is not closed or has no JSON body");
            if (maxMissions < 1)
                return Failure("invalid_mission_cap", "", "maxMissions", "Mission cap must be at least one");

            VersionedArchitectPlan? plan;
            try
            {
                plan = JsonSerializer.Deserialize<VersionedArchitectPlan>(blocks[blocks.Count - 1], _JsonOptions);
            }
            catch (JsonException)
            {
                return Failure("invalid_plan_json", "", "armada-plan", "The final armada-plan revision contains invalid JSON");
            }

            if (plan == null) return Failure("invalid_plan_json", "", "armada-plan", "The final armada-plan revision is empty");
            NormalizeCollections(plan);
            List<string> errors = Validate(plan);
            if (errors.Count > 0) return Failure("invalid_plan_contract", "", "armada-plan", String.Join("; ", errors));
            return Adapt(plan, maxMissions, blocks[blocks.Count - 1]);
        }

        private static ArchitectParseResult Failure(string type, string mission, string field, string message)
        {
            ArchitectParseResult result = new ArchitectParseResult { Verdict = ArchitectParseVerdict.StructuralFailure };
            result.Errors.Add(new ArchitectParseError(type, mission, field, message));
            return result;
        }

        private static List<string> ExtractPlanBlocks(string output, out bool unclosed)
        {
            List<string> blocks = new List<string>();
            StringBuilder? current = null;
            unclosed = false;
            foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (current == null && IsFence(line)) current = new StringBuilder();
                else if (current != null && line.StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(current.ToString());
                    current = null;
                }
                else if (current != null) current.AppendLine(raw);
            }
            if (current != null) unclosed = true;
            return blocks;
        }

        private static bool IsFence(string line) => line.StartsWith("```", StringComparison.Ordinal)
            && String.Equals(line.Substring(3).Trim(), FenceInfo, StringComparison.OrdinalIgnoreCase);
    }
}
