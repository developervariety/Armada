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
        }
    }
}
