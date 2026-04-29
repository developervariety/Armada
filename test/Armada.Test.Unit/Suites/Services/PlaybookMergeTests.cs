namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for PlaybookMerge.MergeWithVesselDefaults.
    /// </summary>
    public class PlaybookMergeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "PlaybookMerge";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CallerOnly_NoDefaults_CallerWins", async () =>
            {
                List<SelectedPlaybook> caller = new List<SelectedPlaybook>
                {
                    new SelectedPlaybook { PlaybookId = "pbk_a", DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree },
                    new SelectedPlaybook { PlaybookId = "pbk_b", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                };

                List<SelectedPlaybook> result = PlaybookMerge.MergeWithVesselDefaults(null, caller);

                AssertEqual(2, result.Count, "Should have caller entries");
                AssertEqual("pbk_a", result[0].PlaybookId);
                AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, result[0].DeliveryMode);
                AssertEqual("pbk_b", result[1].PlaybookId);
                await Task.CompletedTask;
            });

            await RunTest("DefaultsOnly_EmptyCaller_DefaultsWin", async () =>
            {
                List<SelectedPlaybook> defaults = new List<SelectedPlaybook>
                {
                    new SelectedPlaybook { PlaybookId = "pbk_x", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent },
                    new SelectedPlaybook { PlaybookId = "pbk_y", DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference }
                };

                List<SelectedPlaybook> result = PlaybookMerge.MergeWithVesselDefaults(defaults, new List<SelectedPlaybook>());

                AssertEqual(2, result.Count, "Should have default entries");
                AssertEqual("pbk_x", result[0].PlaybookId);
                AssertEqual(PlaybookDeliveryModeEnum.InlineFullContent, result[0].DeliveryMode);
                AssertEqual("pbk_y", result[1].PlaybookId);
                AssertEqual(PlaybookDeliveryModeEnum.InstructionWithReference, result[1].DeliveryMode);
                await Task.CompletedTask;
            });

            await RunTest("BothWithCollision_CallerDeliveryModeOverridesDefault", async () =>
            {
                List<SelectedPlaybook> defaults = new List<SelectedPlaybook>
                {
                    new SelectedPlaybook { PlaybookId = "pbk_a", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent },
                    new SelectedPlaybook { PlaybookId = "pbk_b", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                };
                List<SelectedPlaybook> caller = new List<SelectedPlaybook>
                {
                    new SelectedPlaybook { PlaybookId = "pbk_a", DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree }
                };

                List<SelectedPlaybook> result = PlaybookMerge.MergeWithVesselDefaults(defaults, caller);

                AssertEqual(2, result.Count, "Should have 2 entries: both defaults, pbk_a overridden");
                AssertEqual("pbk_a", result[0].PlaybookId);
                AssertEqual(PlaybookDeliveryModeEnum.AttachIntoWorktree, result[0].DeliveryMode, "Caller deliveryMode overrides default on collision");
                AssertEqual("pbk_b", result[1].PlaybookId);
                AssertEqual(PlaybookDeliveryModeEnum.InlineFullContent, result[1].DeliveryMode, "Non-colliding default is unchanged");
                await Task.CompletedTask;
            });

            await RunTest("BothNonColliding_DefaultsFirstThenCallerAppended", async () =>
            {
                List<SelectedPlaybook> defaults = new List<SelectedPlaybook>
                {
                    new SelectedPlaybook { PlaybookId = "pbk_1", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                };
                List<SelectedPlaybook> caller = new List<SelectedPlaybook>
                {
                    new SelectedPlaybook { PlaybookId = "pbk_2", DeliveryMode = PlaybookDeliveryModeEnum.AttachIntoWorktree }
                };

                List<SelectedPlaybook> result = PlaybookMerge.MergeWithVesselDefaults(defaults, caller);

                AssertEqual(2, result.Count, "Should have default then caller");
                AssertEqual("pbk_1", result[0].PlaybookId, "Default should appear first");
                AssertEqual("pbk_2", result[1].PlaybookId, "Caller (non-colliding) should be appended after defaults");
                await Task.CompletedTask;
            });
        }
    }
}
