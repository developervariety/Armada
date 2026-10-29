namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// One battery task: the mission prompt and how it is graded.
    /// </summary>
    public class BatteryTaskDefinition
    {
        #region Public-Members

        /// <summary>
        /// Task identifier, unique in the pack.
        /// </summary>
        public string? Id { get; set; } = null;

        /// <summary>
        /// Title; also the mission title.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Optional difficulty label.
        /// </summary>
        public string? Difficulty { get; set; } = null;

        /// <summary>
        /// Mission description.
        /// </summary>
        public string? Prompt { get; set; } = null;

        /// <summary>
        /// Folder, relative to the pack, copied over the fixture when it is reset for this task (for example to add
        /// the failing test of a bug-fix task).
        /// </summary>
        public string? Setup { get; set; } = null;

        /// <summary>
        /// Names of the pack toolchain entries the task needs. Null means every entry; an empty list means none.
        /// </summary>
        public List<string>? Toolchain { get; set; } = null;

        /// <summary>
        /// Minutes the mission may run before it is cancelled and the task fails (1 to 1440), or null for the default.
        /// </summary>
        public int? TimeoutMinutes { get; set; } = null;

        /// <summary>
        /// How the task is graded.
        /// </summary>
        public BatteryGraderDefinition? Grader { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryTaskDefinition()
        {
        }

        #endregion
    }
}
