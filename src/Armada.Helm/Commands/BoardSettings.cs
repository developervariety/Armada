namespace Armada.Helm.Commands
{
    using System.ComponentModel;

    /// <summary>
    /// Settings for the board command.
    /// </summary>
    public class BoardSettings : BaseSettings
    {
        /// <summary>
        /// How many recent notes to show.
        /// </summary>
        [Description("Recent notes to show (1-200)")]
        [DefaultValue(25)]
        public int Limit { get; set; } = 25;
    }
}
