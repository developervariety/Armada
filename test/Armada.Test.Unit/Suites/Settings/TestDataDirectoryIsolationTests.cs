namespace Armada.Test.Unit.Suites.Settings
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Guards the test process against writing into the live Armada home.
    ///
    /// A settings object built without an explicit DataDirectory resolves its repos, docks, logs,
    /// and database from <see cref="Constants.DefaultDataDirectory"/>. That default used to be the
    /// real user profile even inside a test run, so a test driving dock or mission services wrote
    /// into the tree holding the production bare repos, docks, settings.json, and merge queue.
    /// It did: a barrier-vessel bare-repo shell and a 2.8 MB dock of 100 generated mission briefs
    /// accumulated in the live home across six days of unit runs before anyone noticed.
    ///
    /// These tests fail if that isolation is lost, whether by removing the redirect or by touching
    /// Constants before it runs.
    /// </summary>
    public class TestDataDirectoryIsolationTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Test Data Directory Isolation";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Default data directory resolves under the test temp root", () =>
            {
                AssertNotNull(TestDataDirectory.Root, "TestDataDirectory.Root");
                AssertStartsWith(TestDataDirectory.Root!, Constants.DefaultDataDirectory, "DefaultDataDirectory");
            }).ConfigureAwait(false);

            await RunTest("Default data directory is not the real Armada home", () =>
            {
                string realHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".armada");

                AssertNotEqual(realHome, Constants.DefaultDataDirectory, "DefaultDataDirectory");
            }).ConfigureAwait(false);

            // The path that actually caused the leak: a bare settings object, which is how 192 call
            // sites across the test tree build their configuration.
            await RunTest("Bare ArmadaSettings resolves every derived path under the temp root", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();

                AssertStartsWith(TestDataDirectory.Root!, settings.DataDirectory, "DataDirectory");
                AssertStartsWith(TestDataDirectory.Root!, settings.ReposDirectory, "ReposDirectory");
                AssertStartsWith(TestDataDirectory.Root!, settings.DocksDirectory, "DocksDirectory");
                AssertStartsWith(TestDataDirectory.Root!, settings.LogDirectory, "LogDirectory");
                AssertStartsWith(TestDataDirectory.Root!, settings.DatabasePath, "DatabasePath");
                AssertStartsWith(TestDataDirectory.Root!, settings.CodeIndex.IndexDirectory, "CodeIndex.IndexDirectory");
            }).ConfigureAwait(false);

            // A server given its own data directory keeps its code index there, whatever the process-wide
            // default is; an index directory set explicitly (as a deployed settings file does) is kept.
            await RunTest("Code index directory follows the settings' own data directory unless set explicitly", () =>
            {
                string dataDirectory = Path.Combine(Path.GetTempPath(), "armada-code-index-home-" + Guid.NewGuid().ToString("N"));
                string explicitIndex = Path.Combine(Path.GetTempPath(), "armada-code-index-explicit-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DataDirectory = dataDirectory;
                    settings.InitializeDirectories();
                    AssertEqual(Path.GetFullPath(Path.Combine(dataDirectory, "code-index")), settings.CodeIndex.IndexDirectory,
                        "an unset index directory follows the data directory");

                    string json = "{\"dataDirectory\":" + System.Text.Json.JsonSerializer.Serialize(dataDirectory)
                        + ",\"codeIndex\":{\"indexDirectory\":" + System.Text.Json.JsonSerializer.Serialize(explicitIndex) + "}}";
                    ArmadaSettings loaded = ArmadaSettings.FromJson(json, Path.Combine(dataDirectory, "settings.json"));
                    AssertEqual(Path.GetFullPath(explicitIndex), loaded.CodeIndex.IndexDirectory,
                        "an index directory set in the settings file is kept");
                }
                finally
                {
                    if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
                }
            }).ConfigureAwait(false);

            // Production must be unaffected: with the override absent the default is the user
            // profile, which is what the deployed Admiral relies on.
            await RunTest("Override variable is what redirects the default, and it is opt-in", () =>
            {
                AssertEqual("ARMADA_DATA_DIRECTORY", Constants.DataDirectoryOverrideVariable, "override variable name");

                string? current = Environment.GetEnvironmentVariable(Constants.DataDirectoryOverrideVariable);
                AssertNotNull(current, "override variable value");
                AssertEqual(TestDataDirectory.Root, current, "override points at the temp root");
            }).ConfigureAwait(false);

            // ARMADA_DATA_DIR is accepted as an alias. ARMADA_DATA_DIRECTORY wins when both are set, so the
            // test redirect still isolates a run whose shell also exports the alias.
            await RunTest("ARMADA_DATA_DIR relocates the default data directory, and ARMADA_DATA_DIRECTORY wins over it", () =>
            {
                System.Reflection.MethodInfo resolve = typeof(Constants).GetMethod(
                    "ResolveDefaultDataDirectory",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                AssertNotNull(resolve, "the default data directory resolver");

                string? savedPrimary = Environment.GetEnvironmentVariable("ARMADA_DATA_DIRECTORY");
                string? savedAlias = Environment.GetEnvironmentVariable("ARMADA_DATA_DIR");
                string aliasPath = Path.Combine(Path.GetTempPath(), "armada-alias-" + Guid.NewGuid().ToString("N"));
                string primaryPath = Path.Combine(Path.GetTempPath(), "armada-primary-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Environment.SetEnvironmentVariable("ARMADA_DATA_DIRECTORY", null);
                    Environment.SetEnvironmentVariable("ARMADA_DATA_DIR", aliasPath);
                    AssertEqual(aliasPath, (string)resolve.Invoke(null, null)!, "the alias alone sets the data directory");

                    Environment.SetEnvironmentVariable("ARMADA_DATA_DIRECTORY", primaryPath);
                    AssertEqual(primaryPath, (string)resolve.Invoke(null, null)!, "ARMADA_DATA_DIRECTORY wins when both are set");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ARMADA_DATA_DIRECTORY", savedPrimary);
                    Environment.SetEnvironmentVariable("ARMADA_DATA_DIR", savedAlias);
                }
            }).ConfigureAwait(false);
        }
    }
}
