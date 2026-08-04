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
        }
    }
}
