namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Where a resolved ecosystem command came from, in resolution order.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EcosystemCommandSourceEnum
    {
        /// <summary>
        /// The vessel's own command field (TestCommand, LintCommand, AuditCommand or DefinitionOfDoneBuildCommand).
        /// </summary>
        [EnumMember(Value = "VesselOverride")]
        VesselOverride,

        /// <summary>
        /// A script the repository declares: a package.json or composer.json script, a Makefile or justfile target, or the Gradle wrapper.
        /// </summary>
        [EnumMember(Value = "RepoScript")]
        RepoScript,

        /// <summary>
        /// The EcosystemCommandCatalog default for a detected ecosystem, or the universal fallback (osv-scanner for Audit, gitleaks for Secrets).
        /// </summary>
        [EnumMember(Value = "EcosystemDefault")]
        EcosystemDefault,

        /// <summary>
        /// No override, repo script or catalog default applies; the command is empty.
        /// </summary>
        [EnumMember(Value = "NotSupported")]
        NotSupported
    }
}
