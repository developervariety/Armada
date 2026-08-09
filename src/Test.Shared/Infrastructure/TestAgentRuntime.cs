namespace Test.Shared.Infrastructure
{
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using SyslogLogging;

    /// <summary>
    /// Minimal concrete implementation for testing <see cref="BaseAgentRuntime"/>. Uses a simple
    /// cross-platform command (<c>dotnet --version</c>) by default, with overridable command and
    /// argument list so suites can exercise valid launches, invalid commands, and output streaming.
    /// </summary>
    public class TestAgentRuntime : BaseAgentRuntime
    {
        /// <summary>
        /// Runtime name.
        /// </summary>
        public override string Name => "TestRuntime";

        /// <summary>
        /// Whether the runtime supports resuming a prior session.
        /// </summary>
        public override bool SupportsResume => false;

        /// <summary>
        /// Command to launch; overridable to test invalid-command handling.
        /// </summary>
        public string CommandOverride { get; set; } = "dotnet";

        /// <summary>
        /// Argument list passed to the launched command.
        /// </summary>
        public List<string> ArgsOverride { get; set; } = new List<string> { "--version" };

        /// <summary>
        /// Initialize the test runtime.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public TestAgentRuntime(LoggingModule logging) : base(logging)
        {
        }

        /// <summary>
        /// Resolve the launch command.
        /// </summary>
        /// <returns>The command.</returns>
        protected override string GetCommand() => CommandOverride;

        /// <summary>
        /// Build the argument list for the launched command.
        /// </summary>
        /// <param name="workingDirectory">Working directory.</param>
        /// <param name="prompt">Prompt text.</param>
        /// <param name="model">Optional model.</param>
        /// <param name="finalMessageFilePath">Optional final message artifact path.</param>
        /// <param name="captain">Optional captain.</param>
        /// <returns>The argument list.</returns>
        protected override List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain) => ArgsOverride;
    }
}
