namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>Inference defaults and execution restrictions shared by local and Harbor API captains.</summary>
    public class InferenceOptions
    {
        /// <summary>Advertise and execute tools. False uses plain chat with no tools.</summary>
        public bool ToolsEnabled { get; set; } = true;
        /// <summary>Allow only built-in read-only tools; excludes shell, file mutations, and unclassified MCP tools.</summary>
        public bool ReadOnlyTools { get; set; } = false;
        /// <summary>Permit discovery of tools from the session's scoped MCP server.</summary>
        public bool McpToolsEnabled { get; set; } = true;
        /// <summary>Exact allowed tool names. Null allows all otherwise permitted tools; an empty list allows none.</summary>
        public List<string>? AllowedTools { get; set; } = null;
        /// <summary>Maximum model turns in an API captain run, from 1 through 1000.</summary>
        public int MaxIterations { get; set; } = 100;
        /// <summary>Optional maximum output tokens, from 1 through 10000000.</summary>
        public int? MaxTokens { get; set; } = null;
        /// <summary>Optional temperature, from 0 through 2; null uses provider defaults.</summary>
        public double? Temperature { get; set; } = null;
        /// <summary>Optional nucleus sampling threshold, from 0 through 1.</summary>
        public double? TopP { get; set; } = null;
        /// <summary>Require an eligible Harbor for any launch of this endpoint's API captain.</summary>
        public bool RequireHarbor { get; set; } = false;

        /// <summary>Whether launching on a Harbor requires policy-aware execution support.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool RequiresPolicySupport => !ToolsEnabled || ReadOnlyTools || !McpToolsEnabled || AllowedTools != null
            || MaxIterations != 100 || MaxTokens.HasValue || Temperature.HasValue || TopP.HasValue || RequireHarbor;

        /// <summary>Validate supplied settings without silently changing them.</summary>
        public void Validate()
        {
            if (MaxIterations < 1 || MaxIterations > 1000) throw new ArgumentException("InferenceOptions.MaxIterations must be between 1 and 1000.");
            if (MaxTokens.HasValue && (MaxTokens < 1 || MaxTokens > 10000000)) throw new ArgumentException("InferenceOptions.MaxTokens must be between 1 and 10000000.");
            if (Temperature.HasValue && (!Double.IsFinite(Temperature.Value) || Temperature < 0 || Temperature > 2)) throw new ArgumentException("InferenceOptions.Temperature must be between 0 and 2.");
            if (TopP.HasValue && (!Double.IsFinite(TopP.Value) || TopP < 0 || TopP > 1)) throw new ArgumentException("InferenceOptions.TopP must be between 0 and 1.");
            if (AllowedTools != null && (AllowedTools.Count > 256 || AllowedTools.Any(n => String.IsNullOrWhiteSpace(n) || n.Length > 256 || n != n.Trim()) || AllowedTools.Distinct(StringComparer.Ordinal).Count() != AllowedTools.Count))
                throw new ArgumentException("InferenceOptions.AllowedTools must contain at most 256 distinct, nonblank exact tool names (maximum 256 characters each).");
        }
    }
}
