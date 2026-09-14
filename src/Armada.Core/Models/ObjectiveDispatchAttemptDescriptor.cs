namespace Armada.Core.Models
{
    /// <summary>
    /// What a dispatch attempt is about to create. Recorded with the durable attempt so a crash
    /// between voyage creation and the voyage id being recorded can still be matched to its voyage.
    /// </summary>
    public class ObjectiveDispatchAttemptDescriptor
    {
        /// <summary>
        /// Title of the voyage the attempt creates.
        /// </summary>
        public string? Title { get; set; } = null;

        /// <summary>
        /// Vessel the voyage's missions target, when known.
        /// </summary>
        public string? VesselId { get; set; } = null;
    }
}
