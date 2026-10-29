namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Out-of-the-box workflow profiles: templates are matched from a repository's files, a vessel without an applicable
    /// profile gets one scoped to it, a generic one is upgraded once the toolchain is recognizable, and profiles a person
    /// created or edited are never touched.
    /// </summary>
    public sealed class DefaultWorkflowProfileSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.DefaultWorkflowProfile";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("templates_match_toolchains", "Templates are matched from the files in the repository", () =>
            {
                AssertEqual(WorkflowProfileTemplates.DotNet, Match("README.md", "src/Armada.sln", "src/Armada.Core/Armada.Core.csproj"));
                AssertEqual(WorkflowProfileTemplates.Node, Match("package.json", "src/index.ts"));
                AssertEqual(WorkflowProfileTemplates.Go, Match("go.mod", "main.go"));
                AssertEqual(WorkflowProfileTemplates.Rust, Match("Cargo.toml", "src/main.rs"));
                AssertEqual(WorkflowProfileTemplates.Maven, Match("pom.xml"));
                AssertEqual(WorkflowProfileTemplates.Gradle, Match("build.gradle.kts", "gradlew"));
                AssertEqual(WorkflowProfileTemplates.Python, Match("pyproject.toml", "pkg/__init__.py"));
                AssertEqual(WorkflowProfileTemplates.Generic, Match("README.md", "docs/index.md"));
                AssertEqual(WorkflowProfileTemplates.Generic, WorkflowProfileTemplates.Match(null));
            }));

            cases.Add(Case("armada_targets_test_project", "The Armada default excludes solution-wide test discovery", () =>
            {
                RepositoryLayout layout = new RepositoryLayout(new[] { "src/Armada.sln", "src/Armada.Core/Armada.Core.csproj", "src/Test.Xunit/Test.Xunit.csproj", "src/Test.Automated/Test.Automated.csproj" }, "repository");
                WorkflowProfile profile = WorkflowProfileTemplates.Build(new Vessel("Armada", "https://example.invalid/Armada"), layout, WorkflowProfileTemplates.DotNet);
                AssertEqual("dotnet run --project \"src/Test.Automated\" -c Release -f net10.0 -- --suites Services", profile.UnitTestCommand);
            }));

            cases.Add(Case("dotnet_targets_the_solution", "The .NET profile builds and tests the shallowest solution, quoted", () =>
            {
                Vessel vessel = new Vessel("Armada", "https://github.com/jchristn/Armada");
                RepositoryLayout layout = new RepositoryLayout(new[] { "src/Armada.sln", "src/Test/Test.sln", "src/Armada.Core/Armada.Core.csproj" }, "the repository");
                WorkflowProfile profile = WorkflowProfileTemplates.Build(vessel, layout, WorkflowProfileTemplates.DotNet);
                AssertEqual("dotnet build \"src/Armada.sln\" -c Release", profile.BuildCommand);
                AssertEqual("dotnet test \"src/Armada.sln\" -c Release", profile.UnitTestCommand);
                AssertEqual(WorkflowProfileScopeEnum.Vessel, profile.Scope);
                AssertEqual(vessel.Id, profile.VesselId);
                AssertTrue(profile.IsDefault && profile.Active, "default and active");
                AssertEqual(WorkflowProfileTemplates.DotNet, profile.SourceTemplate);
            }));

            cases.Add(CaseAsync("vessel_gets_profile_then_upgrade", "A vessel without a profile gets one; a generic one is upgraded once recognizable; a person's profile is left alone", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                DefaultWorkflowProfileProvisioner provisioner = new DefaultWorkflowProfileProvisioner(testDb.Driver, profiles, logging);
                string dir = Path.Combine(Path.GetTempPath(), "armada-defprof-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                if (await testDb.Driver.Tenants.ReadAsync("ten_defprof").ConfigureAwait(false) == null)
                    await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "ten_defprof", Name = "ten_defprof" }).ConfigureAwait(false);
                try
                {
                    Vessel vessel = new Vessel("Plain", "https://example.invalid/plain.git") { TenantId = "ten_defprof", WorkingDirectory = dir };
                    await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), "hello").ConfigureAwait(false);
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile? generic = await provisioner.EnsureAsync(vessel).ConfigureAwait(false);
                    AssertNotNull(generic, "a generic profile is attached");
                    AssertEqual(WorkflowProfileTemplates.Generic, generic.SourceTemplate);
                    AssertNull(generic.BuildCommand, "generic has no build command");
                    AssertEqual("git diff --check", generic.LintCommand);
                    WorkflowProfileValidationResult validation = await profiles.ValidateAsync(generic).ConfigureAwait(false);
                    AssertTrue(validation.IsValid, "the generic profile passes the server's validation: " + String.Join("; ", validation.Errors));
                    AssertNull(await provisioner.EnsureAsync(vessel).ConfigureAwait(false), "nothing changes while it is still unrecognized");

                    foreach (string key in new[] { WorkflowProfileTemplates.DotNet, WorkflowProfileTemplates.Node, WorkflowProfileTemplates.Python, WorkflowProfileTemplates.Go, WorkflowProfileTemplates.Rust, WorkflowProfileTemplates.Maven, WorkflowProfileTemplates.Gradle, WorkflowProfileTemplates.Generic })
                    {
                        WorkflowProfileValidationResult each = await profiles.ValidateAsync(WorkflowProfileTemplates.Build(vessel, null, key)).ConfigureAwait(false);
                        AssertTrue(each.IsValid, key + " passes validation: " + String.Join("; ", each.Errors));
                    }

                    await File.WriteAllTextAsync(Path.Combine(dir, "go.mod"), "module x").ConfigureAwait(false);
                    WorkflowProfile? upgraded = await provisioner.EnsureAsync(vessel).ConfigureAwait(false);
                    AssertNotNull(upgraded, "upgraded once go.mod appears");
                    AssertEqual(generic.Id, upgraded.Id, "the same profile is upgraded in place");
                    AssertEqual("go build ./...", upgraded.BuildCommand);

                    upgraded.SourceTemplate = null;
                    upgraded.BuildCommand = "make build";
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(upgraded).ConfigureAwait(false);
                    AssertNull(await provisioner.EnsureAsync(vessel).ConfigureAwait(false), "a profile a person edited is never replaced");

                    Vessel second = await testDb.Driver.Vessels.CreateAsync(new Vessel("Second", "https://example.invalid/second.git") { TenantId = "ten_defprof" }).ConfigureAwait(false);
                    AssertEqual(1, await provisioner.SweepAsync().ConfigureAwait(false), "the sweep attaches a profile to the vessel without one");
                    WorkflowProfile? resolved = await profiles.ResolveForVesselAsync(new AuthContext { IsAuthenticated = true, IsAdmin = true }, second).ConfigureAwait(false);
                    AssertNotNull(resolved, "every vessel now resolves a profile");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Default Workflow Profiles", cases: cases);
        }

        #endregion

        #region Private-Methods

        private static string Match(params string[] paths)
        {
            return WorkflowProfileTemplates.Match(new RepositoryLayout(paths, "test"));
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, Action body)
        {
            return CaseAsync(caseId, displayName, () =>
            {
                body();
                return Task.CompletedTask;
            });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { TestTags.Positive });
        }

        #endregion
    }
}
