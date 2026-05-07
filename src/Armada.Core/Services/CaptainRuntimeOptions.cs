namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Helper methods for serializing and deserializing runtime-specific captain options.
    /// </summary>
    public static class CaptainRuntimeOptions
    {
        #region Public-Members

        /// <summary>
        /// Reasoning-effort tiers accepted by Codex.
        /// </summary>
        public static readonly IReadOnlySet<string> CodexReasoningEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "low", "medium", "high"
        };

        /// <summary>
        /// Reasoning-effort tiers accepted by Anthropic ClaudeCode.
        /// </summary>
        public static readonly IReadOnlySet<string> ClaudeCodeReasoningEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "low", "medium", "high"
        };

        /// <summary>
        /// Reasoning-effort tiers accepted for Cursor agent validation. cursor-agent CLI
        /// v2026.04.29-c83a488 does not expose a --thinking-effort / --reasoning-effort flag;
        /// values in this set are validated and stored but NOT forwarded to cursor-agent
        /// invocations (parked until cursor-agent CLI gains the flag).
        /// </summary>
        public static readonly IReadOnlySet<string> CursorReasoningEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "low", "medium", "high"
        };

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Serialize a runtime options payload to JSON.
        /// </summary>
        public static string? Serialize<T>(T? value) where T : class
        {
            if (value == null) return null;
            return JsonSerializer.Serialize(value, _SerializerOptions);
        }

        /// <summary>
        /// Deserialize a runtime options payload from JSON.
        /// </summary>
        public static T? Deserialize<T>(string? json) where T : class
        {
            if (String.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, _SerializerOptions);
        }

        /// <summary>
        /// Retrieve typed Mux options for a captain (legacy shim).
        /// Returns null when the captain is null or has no runtime options payload.
        /// </summary>
        public static MuxCaptainOptions? GetMuxOptions(Captain? captain)
        {
            if (captain == null) return null;
            return Deserialize<MuxCaptainOptions>(captain.RuntimeOptionsJson);
        }

        /// <summary>
        /// Retrieve the unified captain options (Approach A flat schema) for a captain.
        /// Composes runtime-agnostic settings (reasoningEffort) with runtime-specific
        /// settings (Mux endpoint config). Returns null when the captain is null or
        /// has no runtime options payload.
        /// </summary>
        public static CaptainOptions? GetCaptainOptions(Captain? captain)
        {
            if (captain == null) return null;
            return Deserialize<CaptainOptions>(captain.RuntimeOptionsJson);
        }

        /// <summary>
        /// Retrieve the parsed reasoning-effort value for a captain. Returns null when the
        /// captain has no runtime options payload or no reasoningEffort key set.
        /// </summary>
        public static string? GetReasoningEffort(Captain? captain)
        {
            CaptainOptions? options = GetCaptainOptions(captain);
            return options?.ReasoningEffort;
        }

        /// <summary>
        /// Validate a reasoning-effort value against the captain's runtime.
        /// Returns null when the value is acceptable (including when it's null/empty).
        /// Returns a human-readable error message when the value is rejected.
        /// </summary>
        /// <param name="runtime">Captain runtime.</param>
        /// <param name="reasoningEffort">Candidate reasoning-effort value.</param>
        public static string? ValidateReasoningEffort(AgentRuntimeEnum runtime, string? reasoningEffort)
        {
            if (String.IsNullOrWhiteSpace(reasoningEffort)) return null;

            string trimmed = reasoningEffort.Trim();

            switch (runtime)
            {
                case AgentRuntimeEnum.Codex:
                    if (!CodexReasoningEfforts.Contains(trimmed))
                        return $"reasoningEffort '{trimmed}' is not supported for Codex captains. Accepted values: low, medium, high.";
                    return null;

                case AgentRuntimeEnum.ClaudeCode:
                    if (!ClaudeCodeReasoningEfforts.Contains(trimmed))
                        return $"reasoningEffort '{trimmed}' is not supported for ClaudeCode captains. Accepted values: low, medium, high.";
                    return null;

                case AgentRuntimeEnum.Cursor:
                    if (!CursorReasoningEfforts.Contains(trimmed))
                        return $"reasoningEffort '{trimmed}' is not supported for Cursor captains. Accepted values: low, medium, high.";
                    return null;

                default:
                    // Mux / Custom / Gemini: no reasoning-effort plumbing yet; ignore silently.
                    return null;
            }
        }

        #endregion
    }
}
