namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Which objective a voyage serves when an umbrella objective and its child both list the voyage.
    /// </summary>
    public class VoyageObjectiveResolverTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Voyage Objective Resolver";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("The objective the voyage brief names wins over an umbrella that also lists the voyage", () =>
            {
                Objective umbrella = Umbrella();
                Objective child = Child(umbrella);
                Objective? chosen = VoyageObjectiveResolver.Choose(new[] { umbrella, child }, "vyg_shared", child.Id);
                AssertEqual(child.Id, chosen!.Id, "the brief names the child");
                return Task.CompletedTask;
            });

            await RunTest("Without a brief marker the most specific listing objective wins over its parent", () =>
            {
                Objective umbrella = Umbrella();
                Objective child = Child(umbrella);
                Objective? chosen = VoyageObjectiveResolver.Choose(new[] { umbrella, child }, "vyg_shared", null);
                AssertEqual(child.Id, chosen!.Id, "the umbrella is the parent of another listing objective, so the child serves the voyage");
                return Task.CompletedTask;
            });

            await RunTest("A brief marker naming a missing objective falls back to the listing rule", () =>
            {
                Objective umbrella = Umbrella();
                Objective child = Child(umbrella);
                Objective? chosen = VoyageObjectiveResolver.Choose(new[] { umbrella, child }, "vyg_shared", "obj_gone");
                AssertEqual(child.Id, chosen!.Id);
                AssertNull(VoyageObjectiveResolver.Choose(new[] { umbrella }, "vyg_other", null), "no objective lists another voyage");
                return Task.CompletedTask;
            });

            await RunTest("The brief marker is read from the voyage description", () =>
            {
                string description = "<!-- armada-objective-brief:obj_childexample -->\n\n# Objective Brief\n<!-- /armada-objective-brief -->";
                AssertEqual("obj_childexample", VoyageObjectiveResolver.ReadBriefObjectiveId(description));
                AssertNull(VoyageObjectiveResolver.ReadBriefObjectiveId("a voyage written by hand"), "no marker, no id");
                return Task.CompletedTask;
            });

            await RunTest("ResolveAsync reads the voyage's brief marker from the database", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective umbrella = await testDb.Driver.Objectives.CreateAsync(Umbrella()).ConfigureAwait(false);
                    Objective child = Child(umbrella);
                    child.ParentObjectiveId = null;
                    child = await testDb.Driver.Objectives.CreateAsync(child).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Deliver the child scope")
                    {
                        Description = "<!-- armada-objective-brief:" + child.Id + " -->\n\nbrief\n<!-- /armada-objective-brief -->"
                    };
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    umbrella.VoyageIds = new List<string> { voyage.Id };
                    child.VoyageIds = new List<string> { voyage.Id };
                    await testDb.Driver.Objectives.UpdateAsync(umbrella).ConfigureAwait(false);
                    await testDb.Driver.Objectives.UpdateAsync(child).ConfigureAwait(false);

                    Objective? resolved = await VoyageObjectiveResolver.ResolveAsync(testDb.Driver, voyage.Id).ConfigureAwait(false);
                    AssertEqual(child.Id, resolved!.Id, "the brief marker decides even without a parent link");
                }
            });
        }

        private static Objective Umbrella()
        {
            return new Objective
            {
                Title = "Umbrella migration",
                AcceptanceCriteria = new List<string> { "Remove the embedded resources." },
                VoyageIds = new List<string> { "vyg_shared" },
                CreatedUtc = DateTime.UtcNow.AddDays(-1)
            };
        }

        private static Objective Child(Objective umbrella)
        {
            return new Objective
            {
                Title = "Child adapter step",
                ParentObjectiveId = umbrella.Id,
                AcceptanceCriteria = new List<string> { "Deliver the adapter only." },
                VoyageIds = new List<string> { "vyg_shared" },
                CreatedUtc = DateTime.UtcNow
            };
        }
    }
}
