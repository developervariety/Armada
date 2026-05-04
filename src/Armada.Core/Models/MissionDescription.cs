namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Describes a mission's title and description for voyage dispatch.
    /// Replaces tuple (string Title, string Description) throughout the codebase.
    /// </summary>
    public class MissionDescription
    {
        #region Public-Members

        /// <summary>
        /// Mission title.
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Mission description.
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Optional list of file-copy operations the Admiral performs after dock
        /// creation but before captain spawn. Plumbed through to <see cref="Mission.PrestagedFiles"/>
        /// on the created mission.
        /// </summary>
        public List<PrestagedFile>? PrestagedFiles { get; set; } = null;

        /// <summary>
        /// Optional per-mission code context mode. Supported values are auto,
        /// off, and force. When null, the dispatch-level mode is used.
        /// </summary>
        public string? CodeContextMode { get; set; } = null;

        /// <summary>
        /// Optional query used to build the code-index context pack for this
        /// mission. When null or empty, the mission title and description are used.
        /// </summary>
        public string? CodeContextQuery { get; set; } = null;

        /// <summary>
        /// Optional captain identifier this mission must be assigned to. Plumbed
        /// through to <see cref="Mission.PreferredCaptainId"/> on the created mission.
        /// </summary>
        public string? PreferredCaptainId { get; set; } = null;

        /// <summary>
        /// Optional captain Model filter for assignment. Plumbed through to
        /// <see cref="Mission.PreferredModel"/> on the created mission.
        /// </summary>
        public string? PreferredModel { get; set; } = null;

        /// <summary>
        /// Optional ID of a mission this mission must wait for. The dependent
        /// mission stays Pending until the referenced mission reaches a
        /// completion state. Plumbed through to <see cref="Mission.DependsOnMissionId"/>
        /// on the created mission.
        /// </summary>
        public string? DependsOnMissionId { get; set; } = null;

        /// <summary>
        /// Optional logical alias for this mission within the dispatch batch.
        /// Other missions in the same batch may reference this name via
        /// <see cref="DependsOnMissionAlias"/>. Must be unique within the batch.
        /// Aliases are resolved before persistence; they are not stored on the
        /// created mission.
        /// </summary>
        public string? Alias { get; set; } = null;

        /// <summary>
        /// Optional alias of another mission in the same dispatch batch that
        /// this mission must wait for. When set, the dispatch handler resolves
        /// the alias to the concrete <c>msn_*</c> ID of the already-created
        /// dependency mission and persists that ID as
        /// <see cref="Mission.DependsOnMissionId"/>. If both
        /// <see cref="DependsOnMissionAlias"/> and <see cref="DependsOnMissionId"/>
        /// are supplied, the alias takes precedence.
        /// </summary>
        public string? DependsOnMissionAlias { get; set; } = null;

        /// <summary>
        /// Optional per-mission playbook selections. Merged with the voyage-level
        /// merged selections using <see cref="PlaybookMerge.MergeWithVesselDefaults"/>
        /// so callers can override delivery modes per mission without resupplying
        /// the full list.
        /// </summary>
        public List<SelectedPlaybook>? SelectedPlaybooks { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public MissionDescription()
        {
        }

        /// <summary>
        /// Instantiate with title and description.
        /// </summary>
        /// <param name="title">Mission title.</param>
        /// <param name="description">Mission description.</param>
        public MissionDescription(string title, string description)
        {
            Title = title ?? "";
            Description = description ?? "";
        }

        #endregion
    }
}
