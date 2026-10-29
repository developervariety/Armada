namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One deterministic contract repair, stored as the JSON payload of a <c>mission.contract_repaired</c> event so repair
    /// frequency can be counted per captain and model.
    /// </summary>
    public class ContractRepairRecord
    {
        /// <summary>
        /// Repaired mission identifier.
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Persona of the repaired stage.
        /// </summary>
        public string? Persona { get; set; } = null;

        /// <summary>
        /// Captain that ran the stage.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// What was repaired.
        /// </summary>
        public ContractRepairKindEnum RepairKind { get; set; } = ContractRepairKindEnum.None;

        /// <summary>
        /// The evidence the repair relied on (the commit range, or the non-canonical line that was recognized).
        /// </summary>
        public string? Detail { get; set; } = null;
    }
}
