namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The patterns the Slop check classifies in a reviewed .NET diff.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SlopRuleEnum
    {
        /// <summary>
        /// A test was skipped or disabled: a Skip argument, an Ignore attribute, a dynamic skip
        /// assertion, or a conditional-compilation block that compiles a test file out.
        /// </summary>
        SkippedTest = 0,

        /// <summary>
        /// A NoWarn element in an MSBuild project or props file, which silences a warning for the
        /// whole project.
        /// </summary>
        ProjectWideNoWarn = 1,

        /// <summary>
        /// An inline package version, a VersionOverride, or a central-management opt-out in a
        /// repository that manages package versions centrally.
        /// </summary>
        CentralPackageVersionBypass = 2,

        /// <summary>
        /// A catch block whose body is empty or holds only a comment.
        /// </summary>
        EmptyCatch = 3,

        /// <summary>
        /// A Task.Delay or Thread.Sleep with a literal duration.
        /// </summary>
        ArbitraryDelay = 4,

        /// <summary>
        /// A pragma that disables a compiler warning, or a SuppressMessage attribute.
        /// </summary>
        WarningSuppression = 5
    }
}
