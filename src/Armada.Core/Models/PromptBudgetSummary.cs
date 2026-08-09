namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// Strongly-typed projection of the <c>mission.prompt_budget</c> and
    /// <c>mission.launch_prompt_budget</c> ArmadaEvent payloads. Exposes what the admiral
    /// actually sent to a captain - instruction-file bytes per module, the file total, the
    /// configured budget, the over-budget flag, and the launch-prompt byte count - measured
    /// at generation time, never estimated by the captain. Constructed via
    /// <see cref="FromEventPayloads"/>.
    /// </summary>
    public sealed class PromptBudgetSummary
    {
        #region Public-Members

        /// <summary>Mission identifier this summary belongs to.</summary>
        public string MissionId { get; set; } = "";

        /// <summary>Captain runtime that received the instruction file, when recorded.</summary>
        public string? Runtime { get; set; }

        /// <summary>Relative path of the generated instruction file inside the dock.</summary>
        public string? InstructionsRelativePath { get; set; }

        /// <summary>UTF-8 byte count of the generated instruction file (matches wc -c).</summary>
        public int InstructionFileBytes { get; set; }

        /// <summary>Sum of the per-module byte counts tracked by the prompt-module ledger.</summary>
        public int TrackedModuleBytes { get; set; }

        /// <summary>Number of distinct modules written to the file.</summary>
        public int ModuleCount { get; set; }

        /// <summary>Configured CaptainInstructionByteBudget; 0 means the warning is disabled.</summary>
        public int ByteBudget { get; set; }

        /// <summary>True when InstructionFileBytes exceeds ByteBudget (and the budget is enabled).</summary>
        public bool OverBudget { get; set; }

        /// <summary>Per-module UTF-8 byte counts, largest module first.</summary>
        public Dictionary<string, int> Modules { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// UTF-8 byte count of the launch prompt sent to the agent at process start.
        /// Null when no <c>mission.launch_prompt_budget</c> event exists for the mission.
        /// </summary>
        public int? LaunchPromptBytes { get; set; }

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _DeserializeOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Parameterless constructor for System.Text.Json deserialization of mission-status payloads.
        /// Instances for internal use are built through <see cref="FromEventPayloads"/>.
        /// </summary>
        [System.Text.Json.Serialization.JsonConstructor]
        private PromptBudgetSummary()
        {
        }

        /// <summary>
        /// Deserialize the <c>mission.prompt_budget</c> and optional
        /// <c>mission.launch_prompt_budget</c> ArmadaEvent payload JSON strings into a
        /// <see cref="PromptBudgetSummary"/>. Returns null on malformed or empty payloads
        /// without throwing.
        /// </summary>
        /// <param name="payloadJson">Raw <c>mission.prompt_budget</c> payload. May be null or empty.</param>
        /// <param name="launchPayloadJson">Raw <c>mission.launch_prompt_budget</c> payload. May be null or empty.</param>
        /// <returns>Populated summary, or null when the budget payload is absent, empty, or unreadable.</returns>
        public static PromptBudgetSummary? FromEventPayloads(string? payloadJson, string? launchPayloadJson)
        {
            if (String.IsNullOrWhiteSpace(payloadJson)) return null;

            try
            {
                PromptBudgetPayload? dto = JsonSerializer.Deserialize<PromptBudgetPayload>(
                    payloadJson, _DeserializeOptions);
                if (dto == null) return null;

                PromptBudgetSummary summary = new PromptBudgetSummary();
                summary.MissionId = dto.MissionId ?? "";
                summary.Runtime = dto.Runtime;
                summary.InstructionsRelativePath = dto.InstructionsRelativePath;
                summary.InstructionFileBytes = dto.InstructionFileBytes;
                summary.TrackedModuleBytes = dto.TrackedModuleBytes;
                summary.ModuleCount = dto.ModuleCount;
                summary.ByteBudget = dto.ByteBudget;
                summary.OverBudget = dto.OverBudget;
                if (dto.Modules != null)
                {
                    foreach (KeyValuePair<string, int> entry in dto.Modules)
                        summary.Modules[entry.Key] = entry.Value;
                }

                if (!String.IsNullOrWhiteSpace(launchPayloadJson))
                {
                    try
                    {
                        LaunchPromptBudgetPayload? launch = JsonSerializer.Deserialize<LaunchPromptBudgetPayload>(
                            launchPayloadJson, _DeserializeOptions);
                        if (launch != null) summary.LaunchPromptBytes = launch.LaunchPromptBytes;
                    }
                    catch (Exception)
                    {
                        // Non-fatal: the launch-prompt figure is supplementary.
                    }
                }

                return summary;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Private-Types

        /// <summary>Internal DTO matching the anonymous-type shape emitted by RecordPromptBudgetAsync.</summary>
        private sealed class PromptBudgetPayload
        {
            /// <summary>Mission identifier.</summary>
            public string? MissionId { get; set; }

            /// <summary>Captain runtime, when recorded.</summary>
            public string? Runtime { get; set; }

            /// <summary>Relative instruction-file path.</summary>
            public string? InstructionsRelativePath { get; set; }

            /// <summary>Instruction-file UTF-8 byte count.</summary>
            public int InstructionFileBytes { get; set; }

            /// <summary>Tracked per-module byte total.</summary>
            public int TrackedModuleBytes { get; set; }

            /// <summary>Distinct module count.</summary>
            public int ModuleCount { get; set; }

            /// <summary>Configured byte budget.</summary>
            public int ByteBudget { get; set; }

            /// <summary>Over-budget flag.</summary>
            public bool OverBudget { get; set; }

            /// <summary>Per-module byte counts.</summary>
            public Dictionary<string, int>? Modules { get; set; }
        }

        /// <summary>Internal DTO matching the anonymous-type shape emitted by RecordLaunchPromptBytesAsync.</summary>
        private sealed class LaunchPromptBudgetPayload
        {
            /// <summary>Launch-prompt UTF-8 byte count.</summary>
            public int LaunchPromptBytes { get; set; }
        }

        #endregion
    }
}
