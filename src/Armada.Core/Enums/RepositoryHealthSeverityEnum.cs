namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Severity of one advisory repository-health finding. A finding never blocks dispatch,
    /// readiness, preflight, exact-commit Checks, or the definition-of-done gate.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RepositoryHealthSeverityEnum
    {
        /// <summary>The checkout should be reviewed. Dispatch stays open.</summary>
        Warning,

        /// <summary>The criterion failed. Dispatch stays open.</summary>
        Error
    }
}
