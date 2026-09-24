namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A voyage created without a vessel or missions is one operation on REST and WebSocket: it belongs to the caller,
    /// keeps the playbooks the request selected, and refuses a selection that names no playbook before anything is
    /// created.
    /// </summary>
    public class BareVoyageParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Bare Voyage Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateBareVoyage_KeepsTheSelectedPlaybooksOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Playbook playbook = await harness.Driver.Playbooks.CreateAsync(new Playbook("bare-voyage.md", "# rules")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);

                    foreach (string surface in new[] { "REST", "WebSocket" })
                    {
                        string title = "bare " + surface;
                        object body = new
                        {
                            Title = title,
                            SelectedPlaybooks = new[] { new { PlaybookId = playbook.Id, DeliveryMode = "InlineFullContent" } }
                        };
                        SurfaceReply reply = surface == "REST"
                            ? await harness.RestAsync(HttpMethod.Post, "/api/v1/voyages", body).ConfigureAwait(false)
                            : await harness.WebSocketAsync("create_voyage", null, body).ConfigureAwait(false);

                        AssertFalse(reply.Refused, surface + ": the bare voyage is created: " + reply);
                        Voyage? stored = (await harness.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).FirstOrDefault(v => v.Title == title);
                        AssertNotNull(stored, surface + ": the voyage exists");
                        List<SelectedPlaybook> selections = await harness.Driver.Playbooks.GetVoyageSelectionsAsync(stored!.Id).ConfigureAwait(false);
                        AssertEqual(1, selections.Count, surface + ": the selected playbook is stored on the voyage");
                        AssertEqual(playbook.Id, selections[0].PlaybookId, surface + ": the selection names the playbook");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("CreateBareVoyage_UnknownPlaybook_IsRefusedAndCreatesNothingOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in new[] { "REST", "WebSocket" })
                    {
                        string title = "bare unknown " + surface;
                        object body = new
                        {
                            Title = title,
                            SelectedPlaybooks = new[] { new { PlaybookId = "pbk_missing_" + surface, DeliveryMode = "InlineFullContent" } }
                        };
                        SurfaceReply reply = surface == "REST"
                            ? await harness.RestAsync(HttpMethod.Post, "/api/v1/voyages", body).ConfigureAwait(false)
                            : await harness.WebSocketAsync("create_voyage", null, body).ConfigureAwait(false);

                        AssertTrue(reply.Refused, surface + ": a selection naming no playbook is refused: " + reply);
                        AssertContains("Playbook not found", reply.Body, surface + ": the refusal names the playbook: " + reply);
                        AssertFalse((await harness.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Any(v => v.Title == title),
                            surface + ": no voyage is created");
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
