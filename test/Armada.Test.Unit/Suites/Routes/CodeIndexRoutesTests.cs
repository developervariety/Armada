namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Server.Routes;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for CodeIndexRoutes -- argument validation on construction. The HTTP request
    /// pipeline itself is exercised by integration tests; this suite pins the constructor
    /// contract so accidental null wiring in ArmadaServer fails fast.
    /// </summary>
    public class CodeIndexRoutesTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Code Index Routes";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Constructor rejects null code index service", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                AssertThrows<ArgumentNullException>(() => new CodeIndexRoutes(null!, jsonOptions));
                return Task.CompletedTask;
            });

            await RunTest("Constructor rejects null json options", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                AssertThrows<ArgumentNullException>(() => new CodeIndexRoutes(service, null!));
                return Task.CompletedTask;
            });

            await RunTest("Constructor accepts non-null arguments", () =>
            {
                RecordingCodeIndexService service = new RecordingCodeIndexService();
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                CodeIndexRoutes routes = new CodeIndexRoutes(service, jsonOptions);
                AssertNotNull(routes);
                return Task.CompletedTask;
            });
        }
    }
}
