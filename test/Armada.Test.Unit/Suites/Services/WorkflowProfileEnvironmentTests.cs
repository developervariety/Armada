namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests that a workflow profile can declare environment variables and that a check run
    /// actually receives them.
    /// </summary>
    /// <remarks>
    /// A command can export a variable inline, but then every command string that needs it must
    /// repeat it, and a guard that depends on one is unrunnable in any dock whose profile forgot.
    /// Declaring them on the profile is what makes such a guard runnable in every dock.
    /// </remarks>
    public class WorkflowProfileEnvironmentTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Workflow Profile Environment";

        /// <summary>Run all workflow profile environment tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Declared environment variables survive a database round trip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    WorkflowProfile profile = new WorkflowProfile
                    {
                        Name = "env-profile",
                        Scope = WorkflowProfileScopeEnum.Fleet,
                        BuildCommand = "true",
                        Active = true,
                        EnvironmentVariables = new Dictionary<string, string>
                        {
                            ["ECULINK_PORT_ROOT"] = "/srv/armada/source-drops/example"
                        }
                    };

                    WorkflowProfile created = await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);
                    WorkflowProfile? reloaded = await testDb.Driver.WorkflowProfiles.ReadAsync(created.Id).ConfigureAwait(false);

                    AssertNotNull(reloaded, "Profile should remain readable");
                    AssertTrue(
                        reloaded!.EnvironmentVariables.ContainsKey("ECULINK_PORT_ROOT"),
                        "A declared variable must survive persistence, or the dock never sees it");
                    AssertEqual(
                        "/srv/armada/source-drops/example",
                        reloaded.EnvironmentVariables["ECULINK_PORT_ROOT"],
                        "The variable's value must round-trip unchanged");
                }
            });

            await RunTest("A check command receives the profile's environment variables", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    string workingDir = Path.Combine(Path.GetTempPath(), "armada_env_check_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(workingDir);

                    try
                    {
                        Vessel vessel = new Vessel("env-vessel", "https://github.com/test/env.git");
                        vessel.WorkingDirectory = workingDir;
                        vessel.LocalPath = workingDir;
                        vessel.DefaultBranch = "main";
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        WorkflowProfile profile = new WorkflowProfile
                        {
                            TenantId = vessel.TenantId,
                            Name = "env-exec-profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vessel.Id,
                            // Fails unless the variable is present, so a green result cannot be
                            // produced by anything except the variable actually arriving.
                            SecurityScanCommand = "test \"$ARMADA_ENV_PROBE\" = \"present\"",
                            IsDefault = true,
                            Active = true,
                            EnvironmentVariables = new Dictionary<string, string>
                            {
                                ["ARMADA_ENV_PROBE"] = "present"
                            }
                        };
                        profile = await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                        WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                        VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, profiles, logging);
                        CheckRunService checkRuns = new CheckRunService(testDb.Driver, profiles, readiness, logging);

                        AuthContext auth = AuthContext.Authenticated(
                            vessel.TenantId ?? "default", "usr_env", true, true, "UnitTest");

                        CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                        {
                            VesselId = vessel.Id,
                            Type = CheckRunTypeEnum.SecurityScan,
                            Label = "env probe"
                        }).ConfigureAwait(false);

                        AssertEqual(
                            CheckRunStatusEnum.Passed,
                            run.Status,
                            "The command asserts the variable is set, so a pass proves it arrived. Output: " + (run.Output ?? ""));
                    }
                    finally
                    {
                        try { Directory.Delete(workingDir, true); } catch { }
                    }
                }
            });
        }
    }
}
