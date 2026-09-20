namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The inventory guard over the shipped typed decisions. A decision that ships in a mode and that
    /// nothing consults reads as enforced on every status surface, which is the most dangerous shape a
    /// gate can take. This suite proves each shipped decision either reaches a decision point or is on
    /// the declared unwired list with a reason, and that the declared list carries no entry for a
    /// decision that is in fact consulted.
    /// <para>
    /// The wired set is derived from types and behaviour, never from source text: an adapter is asked,
    /// through the same member its own call path reads, which decision it resolves, and the captain
    /// tools are registered and called so the decision point is observed arriving at the client. A
    /// decision point the discovery cannot see is reported unwired, so an unrecognised shape fails the
    /// suite instead of passing silently.
    /// </para>
    /// </summary>
    public class TypedDecisionWiringTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "Typed Decision Wiring";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // The two decisions no adapter class owns: the captain helpers consult them directly, so the
        // probe is the only place their wiring can be observed.
        private const string _PremiseHelperDecision = "premise_check";
        private const string _MemoryHelperDecision = "memory_record";

        private static readonly Assembly[] _DecisionAssemblies =
        {
            typeof(TypedDecisionSettings).Assembly,
            typeof(McpTypedDecisionTools).Assembly
        };

        // Where a consumer can hold a decision. An adapter lives in Armada.Core, but the thing that holds
        // it may live anywhere: a service in Core, the server that wires it, or a runtime that consults it
        // mid-run. A consumer assembly missing from this list makes its decisions read as unconsulted,
        // which fails loudly; one that declares an adapter and never holds it must NOT pass.
        private static readonly Assembly[] _ConsumerAssemblies =
        {
            typeof(TypedDecisionSettings).Assembly,
            typeof(McpTypedDecisionTools).Assembly,
            typeof(Armada.Runtimes.AgentRuntimeFactory).Assembly
        };

        #endregion

        #region Protected-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                CaptainToolProbe probe = await ProbeCaptainToolsAsync(testDb).ConfigureAwait(false);
                Dictionary<Type, string> declared = DiscoverDeclaredAdapters();
                HashSet<string> wired = DiscoverConsumedDecisionPoints(declared, _ConsumerAssemblies);
                foreach (string observed in probe.Observed) wired.Add(observed);

                await RunTest("CaptainToolProbe_ObservesTheHelperDecisionsItDrives", () =>
                {
                    // The probe is the only evidence for a decision consulted without an adapter class, so a
                    // probe that observed nothing would report those decisions unwired. Prove it drives the
                    // tools before the inventory leans on it.
                    AssertTrue(
                        probe.Observed.Contains(_PremiseHelperDecision),
                        "the premise-check helper was observed consulting its decision; probe notes: " + probe.Notes);
                    AssertTrue(
                        probe.Observed.Contains(_MemoryHelperDecision),
                        "the memory-triage helper was observed consulting its decision; probe notes: " + probe.Notes);
                });

                await RunTest("TheWiredSet_ComesFromConsumers_NotFromAdapterClassesExisting", () =>
                {
                    // The guard used to read the adapter CLASSES, so adding an adapter marked its decision
                    // wired with nothing calling it. Two measurements prove it now reads consumers instead.
                    AssertTrue(declared.Count > 0, "adapter classes are discovered");

                    // One: with no consumer assemblies, every declared adapter is unconsulted. If the answer
                    // still came from the classes existing, this set would be full.
                    HashSet<string> withoutConsumers = DiscoverConsumedDecisionPoints(declared, Array.Empty<Assembly>());
                    AssertEqual(0, withoutConsumers.Count,
                        "with nothing scanned for consumption, no decision counts as wired: "
                            + String.Join(", ", withoutConsumers));

                    // Scanning Core alone must not mark every declared adapter wired just because the
                    // class exists there. Scanning every consumer must find at least as many.
                    HashSet<string> coreOnly = DiscoverConsumedDecisionPoints(
                        declared, new[] { typeof(TypedDecisionSettings).Assembly });
                    HashSet<string> everyConsumer = DiscoverConsumedDecisionPoints(declared, _ConsumerAssemblies);
                    AssertTrue(everyConsumer.Count >= coreOnly.Count, "scanning more consumers never finds fewer decisions");
                    AssertTrue(everyConsumer.Count > 0, "at least one shipped decision is held by a consumer");
                });

                await RunTest("EveryShippedDecision_IsConsultedOrDeclaredUnwiredWithAReason", () =>
                {
                    List<string> undeclared = new List<string>();
                    List<string> reasonless = new List<string>();
                    foreach (string decision in TypedDecisionSettings.ShippedDecisionNames)
                    {
                        if (wired.Contains(decision)) continue;
                        string? reason = TypedDecisionWiring.UnwiredReason(decision);
                        if (reason == null) undeclared.Add(decision);
                        else if (String.IsNullOrWhiteSpace(reason)) reasonless.Add(decision);
                    }

                    AssertEqual(
                        0,
                        undeclared.Count,
                        "shipped decision reaches no decision point and is not on the unwired list: "
                            + String.Join(", ", undeclared)
                            + "; wire it, or add it to TypedDecisionWiring.UnwiredDecisions with the reason it is inert. Probe notes: "
                            + probe.Notes);
                    AssertEqual(0, reasonless.Count, "unwired decision carries no reason: " + String.Join(", ", reasonless));
                });

                await RunTest("EveryDeclaredUnwiredDecision_IsShippedAndStillUnconsulted", () =>
                {
                    List<string> stale = new List<string>();
                    List<string> unknown = new List<string>();
                    foreach (string decision in TypedDecisionWiring.UnwiredDecisions.Keys)
                    {
                        if (!TypedDecisionSettings.ShippedDecisionNames.Contains(decision)) unknown.Add(decision);
                        else if (wired.Contains(decision)) stale.Add(decision);
                    }

                    AssertEqual(
                        0,
                        stale.Count,
                        "decision is consulted but still listed as unwired: "
                            + String.Join(", ", stale)
                            + "; delete its line from TypedDecisionWiring.UnwiredDecisions");
                    AssertEqual(0, unknown.Count, "unwired list names a decision that does not ship: " + String.Join(", ", unknown));
                });

                await RunTest("Status_ReportsTheUnwiredReasonBesideTheMode", () =>
                {
                    // An operator reading a bare Gate on a decision that consults nothing is misled, so the
                    // reason travels with the mode on the status surface.
                    TypedDecisionSettings settings = new TypedDecisionSettings();
                    TypedDecisionStatus status = TypedDecisionStatusBuilder.Build(settings, new TypedDecisionKeyStore(Path.GetTempPath(), _ => null));

                    AssertTrue(status.Decisions.Count > 0, "the status carries the shipped decisions");
                    foreach (TypedDecisionStatusEntry entry in status.Decisions)
                    {
                        string? expected = TypedDecisionWiring.UnwiredReason(entry.Key);
                        AssertEqual(expected, entry.UnwiredReason, entry.Key + " reports its wiring state");
                    }

                    int reported = status.Decisions.Count(e => !String.IsNullOrWhiteSpace(e.UnwiredReason));
                    AssertEqual(TypedDecisionWiring.UnwiredDecisions.Count, reported, "every unwired decision is visible on the status surface");
                });
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Every decision point an adapter type resolves. An adapter built on the shared skeleton is
        /// asked through the same property its own call path reads; a stand-alone adapter is asked
        /// through the same constant it passes to the settings lookup.
        /// </summary>
        /// <summary>
        /// Every decision an adapter class DECLARES, mapped to the adapter type that declares it. This is
        /// the inventory of what exists, which is not the same question as what is consulted.
        /// </summary>
        private static Dictionary<Type, string> DiscoverDeclaredAdapters()
        {
            Dictionary<Type, string> declared = new Dictionary<Type, string>();
            foreach (Assembly assembly in _DecisionAssemblies)
            {
                foreach (Type type in SafeTypes(assembly))
                {
                    if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters) continue;
                    string? decision = BuiltOnAdapterSkeleton(type) ? SkeletonDecisionPoint(type) : DeclaredDecisionPoint(type);
                    if (!String.IsNullOrWhiteSpace(decision)) declared[type] = decision!;
                }
            }
            return declared;
        }

        /// <summary>
        /// The decisions some consumer actually HOLDS, which is the question this guard exists to answer.
        /// <para>
        /// This used to report every decision an adapter class declared, so adding an adapter marked its
        /// decision wired whether or not anything called it — a decision could ship in <c>Gate</c>, report
        /// as enforced on every status surface, and consult nothing. That is the exact shape the unwired
        /// list was built to expose, and the guard could not see it (2026-09-18).
        /// </para>
        /// <para>
        /// Consumption is read as a consumer TYPE naming the adapter type: a field, a property, a method or
        /// constructor parameter, or a return type, including one level of generic argument. An adapter
        /// naming itself does not count. The residual limit is honest and narrow: a consumer that holds an
        /// adapter it never calls still counts as wiring. It cannot be fooled by the case that matters —
        /// an adapter class nothing holds at all.
        /// </para>
        /// </summary>
        /// <param name="declared">Adapter type to declared decision, from <see cref="DiscoverDeclaredAdapters"/>.</param>
        /// <param name="consumerAssemblies">Assemblies whose types may hold an adapter.</param>
        /// <returns>The decisions a consumer holds.</returns>
        private static HashSet<string> DiscoverConsumedDecisionPoints(
            Dictionary<Type, string> declared,
            IEnumerable<Assembly> consumerAssemblies)
        {
            HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
            foreach (Assembly assembly in consumerAssemblies)
            {
                foreach (Type type in SafeTypes(assembly))
                {
                    // An adapter holding its own type is not a consumer of itself - and neither is anything
                    // nested inside it. An async method compiles to a hidden state-machine type nested in the
                    // adapter, with a field of the adapter's type, which would otherwise make every adapter with
                    // an async method report itself as wired.
                    if (declared.ContainsKey(OutermostType(type))) continue;

                    foreach (Type referenced in ReferencedTypes(type))
                        if (declared.TryGetValue(referenced, out string? decision)) consumed.Add(decision);
                }
            }
            return consumed;
        }

        private static Type OutermostType(Type type)
        {
            Type outer = type;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            return outer;
        }

        /// <summary>Every type one consumer type names on its own members, one generic level deep.</summary>
        /// <param name="type">The candidate consumer.</param>
        /// <returns>The named types; empty when the type cannot be reflected over.</returns>
        private static IEnumerable<Type> ReferencedTypes(Type type)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            List<Type> named = new List<Type>();
            try
            {
                foreach (FieldInfo field in type.GetFields(all)) Expand(named, field.FieldType);
                foreach (PropertyInfo property in type.GetProperties(all)) Expand(named, property.PropertyType);
                foreach (MethodInfo method in type.GetMethods(all))
                {
                    Expand(named, method.ReturnType);
                    foreach (ParameterInfo parameter in method.GetParameters()) Expand(named, parameter.ParameterType);
                }
                foreach (ConstructorInfo constructor in type.GetConstructors(all))
                    foreach (ParameterInfo parameter in constructor.GetParameters()) Expand(named, parameter.ParameterType);
            }
            catch (Exception)
            {
                // A type whose members cannot be loaded names nothing here; it can only make the guard
                // stricter, never looser, because an unseen reference reports the decision unconsulted.
            }
            return named;
        }

        private static void Expand(List<Type> into, Type type)
        {
            if (type == null) return;
            into.Add(type);
            if (!type.IsGenericType) return;
            foreach (Type argument in type.GetGenericArguments()) into.Add(argument);
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null)!;
            }
        }

        private static bool BuiltOnAdapterSkeleton(Type type)
        {
            for (Type? current = type.BaseType; current != null; current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(TypedDecisionAdapterBase<,,>)) return true;
            return false;
        }

        private static string? SkeletonDecisionPoint(Type type)
        {
            PropertyInfo? property = type.GetProperty(
                "DecisionPoint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || property.PropertyType != typeof(string)) return null;

            // The property is the adapter's own answer to "which decision do I resolve": the skeleton
            // reads it on every call. It is read off an instance, never off the file it is written in.
            object instance = RuntimeHelpers.GetUninitializedObject(type);
            return property.GetValue(instance) as string;
        }

        private static string? DeclaredDecisionPoint(Type type)
        {
            FieldInfo? field = type.GetField(
                "DecisionPoint",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(string)) return null;
            return (field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null)) as string;
        }

        /// <summary>
        /// Register the captain typed-decision tools and call each one, recording the decision point
        /// every call carries to the client. This is the wiring evidence for a decision that has no
        /// adapter class: the tool is driven and the decision point is observed in flight.
        /// </summary>
        private static async Task<CaptainToolProbe> ProbeCaptainToolsAsync(TestDatabase testDb)
        {
            CaptainToolProbe probe = new CaptainToolProbe();
            FakeTypedDecisionClient client = new FakeTypedDecisionClient(request =>
            {
                if (!String.IsNullOrWhiteSpace(request.DecisionPoint)) probe.Observed.Add(request.DecisionPoint);
                return new TypedDecisionResult { Available = false, UnavailableReason = "probe" };
            });

            ArmadaSettings settings = new ArmadaSettings();
            settings.TypedDecisions.CaptainTool.Enabled = true;
            foreach (string decision in TypedDecisionSettings.ShippedDecisionNames)
                settings.TypedDecisions.Decisions[decision].Mode = TypedDecisionModeEnum.Gate;

            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
            McpTypedDecisionTools.Register(
                (name, _, _, handler) => { handlers[name] = handler; },
                testDb.Driver,
                client,
                new TypedDecisionRecorder(testDb.Driver, new LoggingModule()),
                settings,
                new LoggingModule());

            // One argument object carrying every field the helpers read. A helper ignores the fields it
            // does not know, so a helper added later is driven by the same call.
            JsonElement args = JsonSerializer.SerializeToElement(
                new
                {
                    restatement = "Restate the task before starting it.",
                    candidate = "A candidate memory record for triage.",
                    plan = "Add one type and one method.",
                    diff = "--- a/file\n+++ b/file\n@@ -1 +1 @@\n-one\n+two\n",
                    context = new { diff = "one line" },
                    record = new { input_type = "mission_failure", failure_reason = "a run failed" },
                    claim = "This listed item matches the claim.",
                    items = new[] { "first real item", "second real item" }
                },
                _JsonOptions);

            AuthContext caller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
            StringBuilder notes = new StringBuilder();
            List<string> silent = new List<string>();
            foreach (KeyValuePair<string, Func<JsonElement?, Task<object>>> handler in handlers)
            {
                int before = probe.Observed.Count;
                try
                {
                    using (McpCallerContext.Begin(caller))
                        await handler.Value(args).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A tool the probe cannot drive is not silently dropped: its decision then reads as
                    // unwired and the inventory test fails carrying this reason.
                    notes.Append(handler.Key).Append(": ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("; ");
                    continue;
                }

                // A tool that ran but consulted nothing is the probe's own blind spot, not evidence that
                // the decision is unwired: a helper whose arguments this payload does not satisfy answers
                // invalid without ever reaching the client. Name it, so the failure reads as "the probe
                // could not drive this tool" instead of "this decision is inert".
                // Two tools are expected to observe no SHIPPED decision: the general tool records under
                // its own decision point, and the custom runner follows user-defined decisions. Listing
                // them every time would bury the one name that matters.
                if (probe.Observed.Count == before && !_ToolsWithoutAShippedDecision.Contains(handler.Key))
                    silent.Add(handler.Key);
            }

            if (silent.Count > 0)
                notes.Append("drove but observed no decision point (check this tool's arguments in the probe payload): ")
                    .Append(String.Join(", ", silent)).Append("; ");

            probe.Notes = notes.Length == 0 ? "every registered tool was driven" : notes.ToString();
            probe.Observed.IntersectWith(TypedDecisionSettings.ShippedDecisionNames);
            return probe;
        }

        #endregion

        private static readonly HashSet<string> _ToolsWithoutAShippedDecision = new HashSet<string>(StringComparer.Ordinal)
        {
            "armada_typed_decision",
            "armada_score_items",
            "armada_run_custom_decision"
        };

        private sealed class CaptainToolProbe
        {
            public HashSet<string> Observed { get; } = new HashSet<string>(StringComparer.Ordinal);

            public string Notes { get; set; } = String.Empty;
        }
    }
}
