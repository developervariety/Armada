namespace Armada.Core.Enums
{
    /// <summary>
    /// Where responsibility for tests sits for one mission, resolved from the stages the dispatch
    /// actually created rather than from the pipeline named in prompt text.
    ///
    /// A prompt that defers test work to a Test Engineer stage is wrong whenever no such stage exists:
    /// single-stage pipelines then have no test owner at all, while the Judge still expects coverage.
    /// </summary>
    public enum TestOwnershipEnum
    {
        /// <summary>
        /// The pipeline could not be resolved. Treated the same as <see cref="SoleTestOwner"/> for a
        /// producing persona, because assuming a stage that may not exist is the failure being fixed.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// No Test Engineer stage exists in the resolved pipeline, so this mission owns the tests for
        /// its own change.
        /// </summary>
        SoleTestOwner = 1,

        /// <summary>
        /// A Test Engineer stage runs after this mission.
        /// </summary>
        TestEngineerFollows = 2,

        /// <summary>
        /// This mission is the Test Engineer stage.
        /// </summary>
        TestEngineerIsMe = 3,

        /// <summary>
        /// A Test Engineer stage already ran before this mission.
        /// </summary>
        TestEngineerPreceded = 4
    }
}
