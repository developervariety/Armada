namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for <see cref="DefinitionOfDoneFailureClassifier"/>: a single deterministic
    /// assertion failure is a test failure even when the run also printed a word that an
    /// environment fault would print, while genuine environment signatures still win.
    /// </summary>
    public class DefinitionOfDoneFailureClassifierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Definition Of Done Failure Classifier";

        private const string _SingleAssertionRun =
            "Determining projects to restore...\n" +
            "/work/src/ExamplePortal/ExamplePortal.Api/ExamplePortal.Api.csproj : warning NU1510: PackageReference Microsoft.Extensions.Logging will not be pruned. Consider removing this package from your dependencies, as it is likely unnecessary.\n" +
            "  ExamplePortal.Api -> /work/src/ExamplePortal/ExamplePortal.Api/bin/Debug/net10.0/ExamplePortal.Api.dll\n" +
            "  DependencyInjectionSmokeTests: resolving the dependency graph\n" +
            "Passed!  - Failed:     0, Passed:  2588, Skipped:     0, Total:  2588, Duration: 330 ms - ExamplePortal.Web.Tests.dll (net10.0)\n" +
            "  Failed ExamplePortal.Api.Tests.CatalogueContractMaterialiseTests.Materialise_Produces_Operations_For_Supported_Families [1 ms]\n" +
            "  Error Message:\n" +
            "   Assert.Equal() Failure: Values differ\n" +
            "Expected: 154\n" +
            "Actual:   270\n" +
            "Failed!  - Failed:     1, Passed:  2560, Skipped:    14, Total:  2575, Duration: 1 m 49 s - ExamplePortal.Api.Tests.dll (net10.0)\n" +
            "probe: /work/output/vendor-export/catalog: No such file or directory\n";

        /// <summary>Output of a test command whose host has no matching .NET runtime installed.</summary>
        internal const string MissingRuntimeInstallOutput =
            "You must install or update .NET to run this application.\n" +
            "\n" +
            "App: /work/test/Example.Tests/bin/Debug/net10.0/testhost.dll\n" +
            "Architecture: x64\n" +
            "Framework: 'Microsoft.NETCore.App', version '10.0.0' (x64)\n" +
            ".NET location: /usr/share/dotnet/\n" +
            "\n" +
            "The following frameworks were found:\n" +
            "  8.0.8 at [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n";

        /// <summary>Older host wording for the same missing-runtime fault.</summary>
        internal const string MissingFrameworkOutput =
            "It was not possible to find any compatible framework version\n" +
            "The framework 'Microsoft.NETCore.App', version '10.0.0' (x64) was not found.\n" +
            "  - The following frameworks were found:\n" +
            "      8.0.8 at [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n";

        /// <summary>Output of a test run whose test host process exited before the run finished.</summary>
        internal const string TesthostExitOutput =
            "Starting test execution, please wait...\n" +
            "  Failed Example.Tests.ParserTests.Parse_ReadsHeader [3 ms]\n" +
            "Testhost process for source(s) '/work/test/Example.Tests/bin/Debug/net10.0/Example.Tests.dll' exited with error: Stack overflow.\n" +
            "Please check the diagnostic logs for more information.\n" +
            "Test Run Aborted.\n";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("A single deterministic assertion failure is TestFail even beside weak environment words", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                DefinitionOfDoneFailureClassEnum result = classifier.Classify("unit-test", 1, _SingleAssertionRun);
                AssertEqual(DefinitionOfDoneFailureClassEnum.TestFail, result, "one named failed test with Expected/Actual is a test failure, not host trouble");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A restore failure is Infra even when tests also failed", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                string output = "error NU1101: Unable to find package Foo.\nrestore failed\nFailed!  - Failed: 12, Passed: 0, Total: 12";
                AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, classifier.Classify("unit-test", 1, output), "a restore failure explains every failed test");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A dead container runtime is Infra even when tests also failed", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                string output = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?\nFailed!  - Failed: 40, Passed: 0, Total: 40";
                AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, classifier.Classify("unit-test", 1, output), "a missing container runtime explains every failed test");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A test-host crash is Infra even when tests also failed", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                string output = "MSBUILD : error MSB4166: Child node exited prematurely. OutOfProcNode\nFailed!  - Failed: 300, Passed: 12, Total: 312";
                AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, classifier.Classify("unit-test", 1, output), "a crashed test host explains the failures");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A timeout is Timeout and a compiler diagnostic is Compile", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                AssertEqual(DefinitionOfDoneFailureClassEnum.Timeout, classifier.Classify("unit-test", 1, _SingleAssertionRun, timedOut: true), "timedOut wins over everything");
                AssertEqual(DefinitionOfDoneFailureClassEnum.Compile, classifier.Classify("build", 1, "Foo.cs(12,5): error CS0103: The name 'x' does not exist"), "a compiler diagnostic is Compile");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A weak environment word with no test evidence is still Infra", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, classifier.Classify("build", 1, "bash: ./scripts/gate.sh: No such file or directory"), "with nothing else to explain the exit, the environment word stands");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A missing .NET runtime is Infra, not TestFail", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, classifier.Classify("unit-test", 150, MissingRuntimeInstallOutput),
                    "the host cannot start the test runner without its framework; no captain can repair that");
                AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, classifier.Classify("unit-test", 150, MissingFrameworkOutput),
                    "a framework that was not found is an environment fault");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The recorded gate class is read only from a gate failure reason", () =>
            {
                DefinitionOfDoneFailureClassEnum parsed;
                AssertTrue(DefinitionOfDoneFailureClassifier.TryReadRecordedClass("DoD gate failed: classification=Timeout; unit-test command exited -1\nunit-test command timed out after 1800 seconds.", out parsed), "a gate reason carries its class");
                AssertEqual(DefinitionOfDoneFailureClassEnum.Timeout, parsed, "the recorded class is returned");
                AssertFalse(DefinitionOfDoneFailureClassifier.TryReadRecordedClass("Agent process exited with code 1", out parsed), "a reason with no recorded class has none");
                AssertFalse(DefinitionOfDoneFailureClassifier.TryReadRecordedClass("Judge verdict: NEEDS_REVISION. Quoted: DoD gate failed: classification=Infra; build command exited 1", out parsed), "a gate reason quoted inside another failure is not this mission's class");
                AssertFalse(DefinitionOfDoneFailureClassifier.TryReadRecordedClass("DoD gate failed: classification=Unknown; build command exited 1", out parsed), "an unknown class name is not read");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A testhost process exit is Infra, not TestFail", () =>
            {
                DefinitionOfDoneFailureClassifier classifier = new DefinitionOfDoneFailureClassifier();
                AssertEqual(DefinitionOfDoneFailureClassEnum.Infra, classifier.Classify("unit-test", 1, TesthostExitOutput),
                    "a test host that exited mid-run makes its failed tests victims, not evidence");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
