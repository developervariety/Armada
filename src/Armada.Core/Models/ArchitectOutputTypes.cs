namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Verdict from parsing an Architect captain's AgentOutput.</summary>
    public enum ArchitectParseVerdict
    {
        /// <summary>All blocks parsed and validated without errors.</summary>
        Valid,

        /// <summary>One or more structural errors found (missing fields, bad id shape, cycles, etc.).</summary>
        StructuralFailure,

        /// <summary>Architect emitted a BLOCKED marker with clarifying questions.</summary>
        Blocked,
    }

    /// <summary>Structured representation of the plan-level narrative an Architect produces.</summary>
    public sealed class ArchitectPlan
    {
        /// <summary>Goal statement extracted from the plan markdown.</summary>
        public string Goal { get; set; } = "";

        /// <summary>Architecture section extracted from the plan markdown.</summary>
        public string Architecture { get; set; } = "";

        /// <summary>Tech stack section extracted from the plan markdown.</summary>
        public string TechStack { get; set; } = "";

        /// <summary>File structure table markdown.</summary>
        public string FileStructureMarkdown { get; set; } = "";

        /// <summary>Task dispatch graph markdown.</summary>
        public string DispatchGraphMarkdown { get; set; } = "";

        /// <summary>Full plan markdown (everything before the first [ARMADA:MISSION] marker).</summary>
        public string FullMarkdown { get; set; } = "";
    }

    /// <summary>Single mission entry parsed from an [ARMADA:MISSION] block.</summary>
    public sealed class ArchitectMissionEntry
    {
        /// <summary>Architect-relative mission alias (M1, M2, ...). Orchestrator resolves to real msn_ ids at dispatch time.</summary>
        public string Id { get; set; } = "";

        /// <summary>Human-readable mission title.</summary>
        public string Title { get; set; } = "";

        /// <summary>Preferred model identifier for this mission.</summary>
        public string PreferredModel { get; set; } = "";

        /// <summary>M-alias of a mission this one depends on, or empty if none. Orchestrator resolves at dispatch.</summary>
        public string DependsOnMissionAlias { get; set; } = "";

        /// <summary>Files to prestage into the dock worktree before the captain starts.</summary>
        public List<PrestagedFile> PrestagedFiles { get; set; } = new List<PrestagedFile>();

        /// <summary>Playbooks to attach to the mission.</summary>
        public List<SelectedPlaybook> SelectedPlaybooks { get; set; } = new List<SelectedPlaybook>();

        /// <summary>Full mission brief markdown (the description: | block body).</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>Single structural-failure error from parsing an Architect's output.</summary>
    public sealed record ArchitectParseError(string Type, string MissionId, string Field, string Message);

    /// <summary>Result of parsing an Architect captain's AgentOutput.</summary>
    public sealed class ArchitectParseResult
    {
        /// <summary>Overall parse verdict.</summary>
        public ArchitectParseVerdict Verdict { get; set; }

        /// <summary>Populated when Verdict = Valid.</summary>
        public ArchitectPlan? Plan { get; set; }

        /// <summary>Parsed mission entries. Non-empty when Verdict = Valid.</summary>
        public List<ArchitectMissionEntry> Missions { get; set; } = new List<ArchitectMissionEntry>();

        /// <summary>Populated when Verdict = StructuralFailure.</summary>
        public List<ArchitectParseError> Errors { get; set; } = new List<ArchitectParseError>();

        /// <summary>Populated when Verdict = Blocked.</summary>
        public List<string> BlockedQuestions { get; set; } = new List<string>();
    }
}
