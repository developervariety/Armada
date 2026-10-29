namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What an ecosystem command is for. Each intent maps to one command per ecosystem in the EcosystemCommandCatalog.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EcosystemCommandIntentEnum
    {
        /// <summary>
        /// Compile or package the project.
        /// </summary>
        [EnumMember(Value = "Build")]
        Build,

        /// <summary>
        /// Run the unit tests.
        /// </summary>
        [EnumMember(Value = "Test")]
        Test,

        /// <summary>
        /// Run the linter or formatter check.
        /// </summary>
        [EnumMember(Value = "Lint")]
        Lint,

        /// <summary>
        /// Report known-vulnerable dependencies.
        /// </summary>
        [EnumMember(Value = "Audit")]
        Audit,

        /// <summary>
        /// Report dependencies with newer versions.
        /// </summary>
        [EnumMember(Value = "Outdated")]
        Outdated,

        /// <summary>
        /// Report dependency licenses.
        /// </summary>
        [EnumMember(Value = "Licenses")]
        Licenses,

        /// <summary>
        /// Scan the working tree for committed secrets.
        /// </summary>
        [EnumMember(Value = "Secrets")]
        Secrets
    }
}
