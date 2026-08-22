namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Coverage for the reverse sibling index that tells a producer's gate which vessels compile
    /// against it. The edge only exists on the consumer's record, so these cases pin that it is
    /// read in the consumer-to-producer direction and that a vessel is never its own consumer.
    /// </summary>
    public class ConsumerVesselResolverTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Consumer Vessel Resolver";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A vessel declaring the producer by ID is resolved as its consumer", () =>
            {
                Vessel producer = MakeVessel("vsl_producer", "ExampleLibrary", null);
                Vessel consumer = MakeVessel("vsl_consumer", "ExampleConsumer", new List<SiblingRepo>
                {
                    MakeSibling("vsl_producer", "../ExampleLibrary")
                });

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer, consumer });

                AssertEqual(1, result.Count, "Exactly one consumer must be resolved.");
                AssertEqual("vsl_consumer", result[0].Consumer.Id);
                AssertEqual("../ExampleLibrary", result[0].Declaration.RelativePath, "The declaration must carry the path the consumer expects.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A vessel declaring the producer by NAME is resolved as its consumer", () =>
            {
                Vessel producer = MakeVessel("vsl_producer", "ExampleLibrary", null);
                Vessel consumer = MakeVessel("vsl_consumer", "ExampleConsumer", new List<SiblingRepo>
                {
                    MakeSibling("ExampleLibrary", "../ExampleLibrary")
                });

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer, consumer });

                AssertEqual(1, result.Count, "A name reference must resolve, since the model allows ID or name.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The dependency edge is NOT read backwards", () =>
            {
                // The producer declares the OTHER vessel as its sibling. That makes the producer a
                // consumer of it, not the reverse, so resolving the producer's consumers finds none.
                Vessel producer = MakeVessel("vsl_producer", "ExampleLibrary", new List<SiblingRepo>
                {
                    MakeSibling("vsl_other", "../ExampleToolchain")
                });
                Vessel other = MakeVessel("vsl_other", "ExampleToolchain", null);

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer, other });

                AssertEqual(0, result.Count, "Declaring a dependency must not make the dependency a consumer.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A self-referential declaration is not a consumer edge", () =>
            {
                Vessel producer = MakeVessel("vsl_producer", "ExampleLibrary", new List<SiblingRepo>
                {
                    MakeSibling("vsl_producer", "../ExampleLibrary")
                });

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer });

                AssertEqual(0, result.Count, "A vessel must never be resolved as its own consumer.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Several consumers of one producer are all resolved", () =>
            {
                Vessel producer = MakeVessel("vsl_producer", "ExampleToolchain", null);
                Vessel a = MakeVessel("vsl_a", "ExampleLibrary", new List<SiblingRepo> { MakeSibling("vsl_producer", "../ExampleToolchain") });
                Vessel b = MakeVessel("vsl_b", "ExampleConsumer", new List<SiblingRepo> { MakeSibling("vsl_producer", "../ExampleToolchain") });
                Vessel unrelated = MakeVessel("vsl_c", "ExampleUnrelated", null);

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer, a, b, unrelated });

                AssertEqual(2, result.Count, "Both declaring vessels must be resolved and the unrelated one excluded.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A duplicate declaration yields one consumer, not two builds", () =>
            {
                Vessel producer = MakeVessel("vsl_producer", "ExampleLibrary", null);
                Vessel consumer = MakeVessel("vsl_consumer", "ExampleConsumer", new List<SiblingRepo>
                {
                    MakeSibling("vsl_producer", "../ExampleLibrary"),
                    MakeSibling("ExampleLibrary", "../ExampleLibrary-second")
                });

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer, consumer });

                AssertEqual(1, result.Count, "One consumer means one verification build, however many times it declares the producer.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A declaration with no relative path is ignored", () =>
            {
                // Without a path there is nowhere to materialize the producer, so the edge cannot
                // be acted on and must not be reported as verifiable.
                Vessel producer = MakeVessel("vsl_producer", "ExampleLibrary", null);
                Vessel consumer = MakeVessel("vsl_consumer", "ExampleConsumer", new List<SiblingRepo>
                {
                    MakeSibling("vsl_producer", "")
                });

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer, consumer });

                AssertEqual(0, result.Count, "A pathless declaration cannot be provisioned and must be skipped.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Malformed or absent sibling JSON yields no consumers rather than throwing", () =>
            {
                Vessel producer = MakeVessel("vsl_producer", "ExampleLibrary", null);
                Vessel broken = new Vessel { Id = "vsl_broken", Name = "Broken", SiblingRepos = "{not json" };
                Vessel empty = new Vessel { Id = "vsl_empty", Name = "Empty", SiblingRepos = null };

                IReadOnlyList<ConsumerDeclaration> result = ConsumerVesselResolver.Resolve(
                    producer.Id, producer.Name, new List<Vessel> { producer, broken, empty });

                AssertEqual(0, result.Count, "Bad data must degrade to no edges, never to an exception in the gate.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Null or empty inputs resolve to no consumers", () =>
            {
                AssertEqual(0, ConsumerVesselResolver.Resolve("vsl_producer", "ExampleLibrary", null).Count);
                AssertEqual(0, ConsumerVesselResolver.Resolve("", "ExampleLibrary", new List<Vessel>()).Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static Vessel MakeVessel(string id, string name, List<SiblingRepo>? siblings)
        {
            return new Vessel
            {
                Id = id,
                Name = name,
                SiblingRepos = siblings == null ? null : JsonSerializer.Serialize(siblings)
            };
        }

        private static SiblingRepo MakeSibling(string vesselRef, string relativePath)
        {
            return new SiblingRepo { VesselRef = vesselRef, RelativePath = relativePath };
        }

        #endregion
    }
}
