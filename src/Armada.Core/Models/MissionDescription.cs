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
