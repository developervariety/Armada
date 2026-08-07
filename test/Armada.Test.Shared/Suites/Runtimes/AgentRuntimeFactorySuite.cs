namespace Armada.Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using SyslogLogging;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="AgentRuntimeFactory"/> creation and registration. Cases verify that
    /// each built-in runtime enum resolves to the expected runtime with the correct display name, that
    /// creating an unregistered custom runtime throws, that a runtime registered by name can be created,
    /// and that registering with a null name or null factory is rejected.
    /// </summary>
    public sealed class AgentRuntimeFactorySuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Runtimes.AgentRuntimeFactory";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Agent Runtime Factory suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("create_claude_code_returns_claude_code_runtime", "Create ClaudeCode Returns ClaudeCodeRuntime", TestTags.Positive, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                IAgentRuntime runtime = factory.Create(AgentRuntimeEnum.ClaudeCode);
                AssertNotNull(runtime);
                AssertEqual("Claude Code", runtime.Name);
            }));

            cases.Add(Case("create_codex_returns_codex_runtime", "Create Codex Returns CodexRuntime", TestTags.Positive, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                IAgentRuntime runtime = factory.Create(AgentRuntimeEnum.Codex);
                AssertNotNull(runtime);
                AssertEqual("Codex", runtime.Name);
            }));

            cases.Add(Case("create_gemini_returns_gemini_runtime", "Create Gemini Returns GeminiRuntime", TestTags.Positive, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                IAgentRuntime runtime = factory.Create(AgentRuntimeEnum.Gemini);
                AssertNotNull(runtime);
                AssertEqual("Gemini", runtime.Name);
            }));

            cases.Add(Case("create_cursor_returns_cursor_runtime", "Create Cursor Returns CursorRuntime", TestTags.Positive, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                IAgentRuntime runtime = factory.Create(AgentRuntimeEnum.Cursor);
                AssertNotNull(runtime);
                AssertEqual("Cursor", runtime.Name);
            }));

            cases.Add(Case("create_mux_returns_mux_runtime", "Create Mux Returns MuxRuntime", TestTags.Positive, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                IAgentRuntime runtime = factory.Create(AgentRuntimeEnum.Mux);
                AssertNotNull(runtime);
                AssertEqual("Mux", runtime.Name);
            }));

            cases.Add(Case("create_custom_without_registration_throws", "Create Custom Without Registration Throws", TestTags.Negative, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                AssertThrows<InvalidOperationException>(() => factory.Create(AgentRuntimeEnum.Custom));
            }));

            cases.Add(Case("create_custom_by_name_with_registration_succeeds", "Create Custom By Name With Registration Succeeds", TestTags.Positive, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;

                factory.Register("test-runtime", () => new ClaudeCodeRuntime(logging));

                IAgentRuntime runtime = factory.Create("test-runtime");
                AssertNotNull(runtime);
            }));

            cases.Add(Case("create_custom_by_name_not_registered_throws", "Create Custom By Name Not Registered Throws", TestTags.Negative, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                AssertThrows<InvalidOperationException>(() => factory.Create("nonexistent"));
            }));

            cases.Add(Case("register_null_name_throws", "Register Null Name Throws", TestTags.Negative, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                AssertThrows<ArgumentNullException>(() => factory.Register(null!, () => null!));
            }));

            cases.Add(Case("register_null_factory_throws", "Register Null Factory Throws", TestTags.Negative, () =>
            {
                AgentRuntimeFactory factory = CreateFactory();
                AssertThrows<ArgumentNullException>(() => factory.Register("test", null!));
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Agent Runtime Factory",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static AgentRuntimeFactory CreateFactory()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new AgentRuntimeFactory(logging);
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
