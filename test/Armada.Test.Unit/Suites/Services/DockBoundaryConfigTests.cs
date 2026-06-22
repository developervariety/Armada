namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for <see cref="DockBoundaryConfig"/>, the serializable model written to
    /// <c>.armada/boundary.json</c>. Verifies default lists are non-null, null assignment
    /// coalesces to an empty list (so the dock hook never reads a null section), and the
    /// JSON property names match the camelCase keys the hook script parses.
    /// </summary>
    public sealed class DockBoundaryConfigTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Dock Boundary Config";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Default config exposes non-null empty lists", () =>
            {
                DockBoundaryConfig config = new DockBoundaryConfig();
                AssertNotNull(config.ProtectedPaths, "ProtectedPaths must default to a non-null list");
                AssertNotNull(config.SecretPatterns, "SecretPatterns must default to a non-null list");
                AssertNotNull(config.PrivateIdentifiers, "PrivateIdentifiers must default to a non-null list");
                AssertEqual(0, config.ProtectedPaths.Count, "ProtectedPaths must default to empty");
                AssertEqual(0, config.SecretPatterns.Count, "SecretPatterns must default to empty");
                AssertEqual(0, config.PrivateIdentifiers.Count, "PrivateIdentifiers must default to empty");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Null assignment coalesces to empty list, never null", () =>
            {
                DockBoundaryConfig config = new DockBoundaryConfig();
                config.ProtectedPaths = null!;
                config.SecretPatterns = null!;
                config.PrivateIdentifiers = null!;
                AssertNotNull(config.ProtectedPaths, "ProtectedPaths must coalesce null to a list");
                AssertNotNull(config.SecretPatterns, "SecretPatterns must coalesce null to a list");
                AssertNotNull(config.PrivateIdentifiers, "PrivateIdentifiers must coalesce null to a list");
                AssertEqual(0, config.ProtectedPaths.Count, "Coalesced ProtectedPaths must be empty");
                AssertEqual(0, config.SecretPatterns.Count, "Coalesced SecretPatterns must be empty");
                AssertEqual(0, config.PrivateIdentifiers.Count, "Coalesced PrivateIdentifiers must be empty");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Serialization emits camelCase keys the hook parses", () =>
            {
                DockBoundaryConfig config = new DockBoundaryConfig();
                config.ProtectedPaths.Add("**/CLAUDE.md");
                config.SecretPatterns.Add("-----BEGIN .*PRIVATE KEY-----");
                config.PrivateIdentifiers.Add("ACME-INTERNAL-[0-9]+");

                string json = JsonSerializer.Serialize(config);
                AssertContains("\"protectedPaths\"", json, "JSON must use the protectedPaths key");
                AssertContains("\"secretPatterns\"", json, "JSON must use the secretPatterns key");
                AssertContains("\"privateIdentifiers\"", json, "JSON must use the privateIdentifiers key");
                AssertContains("**/CLAUDE.md", json, "Serialized JSON must carry the configured protected path");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Round-trip deserialization preserves all three sections", () =>
            {
                DockBoundaryConfig original = new DockBoundaryConfig();
                original.ProtectedPaths.Add("**/secrets.json");
                original.SecretPatterns.Add("password\\s*=");
                original.PrivateIdentifiers.Add("EMP-[0-9]+");

                string json = JsonSerializer.Serialize(original);
                DockBoundaryConfig? restored = JsonSerializer.Deserialize<DockBoundaryConfig>(json);

                AssertNotNull(restored, "Deserialized config must not be null");
                AssertEqual(1, restored!.ProtectedPaths.Count, "ProtectedPaths must round-trip");
                AssertEqual(1, restored.SecretPatterns.Count, "SecretPatterns must round-trip");
                AssertEqual(1, restored.PrivateIdentifiers.Count, "PrivateIdentifiers must round-trip");
                AssertEqual("**/secrets.json", restored.ProtectedPaths[0], "ProtectedPaths value must round-trip");
                AssertEqual("EMP-[0-9]+", restored.PrivateIdentifiers[0], "PrivateIdentifiers value must round-trip");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
