namespace Armada.Test.Unit.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class ArmadaEventModelTests : TestSuite
    {
        public override string Name => "ArmadaEvent Model";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ArmadaEvent DefaultConstructor GeneratesIdWithPrefix", () =>
            {
                ArmadaEvent evt = new ArmadaEvent();
                AssertStartsWith("evt_", evt.Id);
            });

            await RunTest("ArmadaEvent TypeMessageConstructor SetsProperties", () =>
            {
                ArmadaEvent evt = new ArmadaEvent("mission.created", "Mission created");
                AssertEqual("mission.created", evt.EventType);
                AssertEqual("Mission created", evt.Message);
            });

            await RunTest("ArmadaEvent DefaultValues AreCorrect", () =>
            {
                ArmadaEvent evt = new ArmadaEvent();
                AssertEqual("", evt.EventType);
                AssertEqual("", evt.Message);
                AssertNull(evt.EntityType);
                AssertNull(evt.EntityId);
                AssertNull(evt.CaptainId);
                AssertNull(evt.MissionId);
                AssertNull(evt.VesselId);
                AssertNull(evt.VoyageId);
                AssertNull(evt.Payload);
            });

            await RunTest("ArmadaEvent SetId Null Throws", () =>
            {
                ArmadaEvent evt = new ArmadaEvent();
                AssertThrows<ArgumentNullException>(() => evt.Id = null!);
            });

            await RunTest("ArmadaEvent Serialization RoundTrip", () =>
            {
                ArmadaEvent evt = new ArmadaEvent("captain.launched", "Captain launched");
                evt.CaptainId = "cpt_test";
                evt.MissionId = "msn_test";
                evt.VesselId = "vsl_test";
                evt.VoyageId = "vyg_test";
                evt.EntityType = "captain";
                evt.EntityId = "cpt_test";
                evt.Payload = "{\"processId\":12345}";

                string json = JsonSerializer.Serialize(evt);
                ArmadaEvent deserialized = JsonSerializer.Deserialize<ArmadaEvent>(json)!;

                AssertEqual(evt.Id, deserialized.Id);
                AssertEqual(evt.EventType, deserialized.EventType);
                AssertEqual(evt.Message, deserialized.Message);
                AssertEqual(evt.CaptainId, deserialized.CaptainId);
                AssertEqual(evt.Payload, deserialized.Payload);
            });

            await RunTest("ArmadaEvent UniqueIds AcrossInstances", () =>
            {
                ArmadaEvent e1 = new ArmadaEvent();
                ArmadaEvent e2 = new ArmadaEvent();
                AssertNotEqual(e1.Id, e2.Id);
            });

            await RunTest("Every documented event type has a production emission site", () =>
            {
                string root = FindRepositoryRoot();
                List<string> documented = ReadDocumentedEventTypes(Path.Combine(root, "docs", "REST_API.md"));
                AssertTrue(documented.Count > 0, "docs/REST_API.md lists its known event types");

                HashSet<string> emitted = CollectEmittedEventTypes(Path.Combine(root, "src"));
                List<string> missing = documented.Where(type => !emitted.Contains(type)).ToList();
                AssertTrue(missing.Count == 0,
                    "docs/REST_API.md lists event types that no production code writes to the event store: " + String.Join(", ", missing));
            });
        }

        #region Private-Methods

        // An emission is an event type passed to an event constructor or to an emit helper, or assigned as the
        // EventType of a new event, either as a literal or through a named constant. A type that appears only in a
        // doc comment, a filter comparison or a constant nobody passes to an emission is not emitted.
        private static readonly Regex _LiteralEmission = new Regex(
            @"(?:\bnew\s+ArmadaEvent\s*\(|\b_?(?:[A-Za-z]*Emit\w*|emit\w*)\s*\()\s*""(?<type>[a-z0-9_]+(?:\.[a-z0-9_]+)+)""",
            RegexOptions.Compiled);

        private static readonly Regex _LiteralAssignment = new Regex(
            @"(?<!const\s+string\s+)(?<![Qq]uery\w*\.)\bEventType\s*=\s*""(?<type>[a-z0-9_]+(?:\.[a-z0-9_]+)+)""",
            RegexOptions.Compiled);

        private static readonly Regex _NamedEmission = new Regex(
            @"(?:\bnew\s+ArmadaEvent\s*\(|\b_?(?:[A-Za-z]*Emit\w*|emit\w*)\s*\(|\bEventType\s*=)\s*(?<arg>[^,;()""=]+)[,);]",
            RegexOptions.Compiled);

        private static readonly Regex _ArgumentName = new Regex(
            @"(?:\b(?<owner>[A-Z]\w*)\.)?\b(?<name>[A-Za-z_]\w*)\b(?!\s*\.)",
            RegexOptions.Compiled);

        private static readonly Regex _ConstantDeclaration = new Regex(
            @"\bconst\s+string\s+(?<name>\w+)\s*=\s*""(?<type>[a-z0-9_]+(?:\.[a-z0-9_]+)+)""",
            RegexOptions.Compiled);

        private static readonly Regex _TypeDeclaration = new Regex(
            @"\b(?:class|struct|record)\s+(?<name>[A-Z]\w*)",
            RegexOptions.Compiled);

        private static List<string> ReadDocumentedEventTypes(string path)
        {
            List<string> types = new List<string>();
            bool inList = false;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("**Known Event Types:**", StringComparison.Ordinal))
                {
                    inList = true;
                    continue;
                }

                if (!inList) continue;
                Match item = Regex.Match(line, @"^-\s+`(?<type>[^`]+)`");
                if (item.Success)
                {
                    types.Add(item.Groups["type"].Value);
                    continue;
                }

                if (types.Count > 0) break;
            }

            return types;
        }

        private static HashSet<string> CollectEmittedEventTypes(string sourceRoot)
        {
            List<string> files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(IsProductionSource)
                .ToList();

            HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, string>> constantsByFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            Dictionary<string, List<string>> filesByType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            Dictionary<string, string> contents = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                contents[file] = text;
                foreach (Match match in _LiteralEmission.Matches(text)) emitted.Add(match.Groups["type"].Value);
                foreach (Match match in _LiteralAssignment.Matches(text)) emitted.Add(match.Groups["type"].Value);

                Dictionary<string, string> constants = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Match match in _ConstantDeclaration.Matches(text)) constants[match.Groups["name"].Value] = match.Groups["type"].Value;
                constantsByFile[file] = constants;

                foreach (Match match in _TypeDeclaration.Matches(text))
                {
                    string typeName = match.Groups["name"].Value;
                    if (!filesByType.TryGetValue(typeName, out List<string>? declaring))
                    {
                        declaring = new List<string>();
                        filesByType[typeName] = declaring;
                    }
                    declaring.Add(file);
                }
            }

            foreach (string file in files)
            {
                // The first argument may name a constant directly or choose between two (a ? A : B).
                foreach (Match emission in _NamedEmission.Matches(contents[file]))
                foreach (Match match in _ArgumentName.Matches(emission.Groups["arg"].Value))
                {
                    string name = match.Groups["name"].Value;
                    string owner = match.Groups["owner"].Value;
                    IEnumerable<string> declaringFiles = String.IsNullOrEmpty(owner)
                        ? new[] { file }
                        : (filesByType.TryGetValue(owner, out List<string>? ownerFiles) ? ownerFiles : new List<string>());
                    foreach (string declaring in declaringFiles)
                    {
                        if (constantsByFile[declaring].TryGetValue(name, out string? value)) emitted.Add(value);
                    }
                }
            }

            return emitted;
        }

        private static bool IsProductionSource(string path)
        {
            string normalized = path.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.Ordinal) || normalized.Contains("/obj/", StringComparison.Ordinal)) return false;
            if (Path.GetFileName(normalized).StartsWith("._", StringComparison.Ordinal)) return false;
            return normalized.Contains("/src/Armada.", StringComparison.Ordinal);
        }

        private static string FindRepositoryRoot()
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                DirectoryInfo? current = new DirectoryInfo(start);
                while (current != null)
                {
                    if (File.Exists(Path.Combine(current.FullName, "docs", "REST_API.md"))
                        && Directory.Exists(Path.Combine(current.FullName, "src", "Armada.Core")))
                        return current.FullName;
                    current = current.Parent;
                }
            }

            throw new DirectoryNotFoundException("Repository root not found from " + AppContext.BaseDirectory + " or " + Environment.CurrentDirectory);
        }

        #endregion
    }
}
