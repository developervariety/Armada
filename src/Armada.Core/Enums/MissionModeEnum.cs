namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// What kind of work a mission is, which decides both the instruction modules it receives and how
    /// its completion is judged.
    ///
    /// Armada previously had one mission shape: implement, test, commit. A read-only diagnostic mission
    /// still received commit, merge-conflict, and test-writing instructions that contradicted its own
    /// brief, and the completion gate then failed it for producing no commit. A correct read-only
    /// mission (msn_ms6ujibm_V5dj7XUow1s, 2026-07-30) was marked Failed with
    /// "worker_produced_no_commits" even though its branch matched main exactly as intended.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MissionModeEnum
    {
        /// <summary>
        /// Changes code and is expected to commit. The historical behaviour and the default when a
        /// mission does not state a mode, so existing dispatches are unaffected.
        /// </summary>
        Implementation,

        /// <summary>
        /// Inspects the repository or the system and reports findings. Produces no commit, and a
        /// missing commit is a success condition rather than a failure.
        /// </summary>
        Audit,

        /// <summary>
        /// Investigates an open question and reports an answer. Produces no commit, and a missing
        /// commit is a success condition rather than a failure.
        /// </summary>
        Research
    }
}
