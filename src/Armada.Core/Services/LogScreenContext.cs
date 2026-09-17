namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The bounded, read-only input one screening pass receives for one mission. It carries the
    /// tail that was read and the identity of the work it came from, and nothing else: a pass
    /// never reaches back to a record, a dock, or the filesystem.
    /// </summary>
    public class LogScreenContext
    {
        #region Public-Members

        /// <summary>
        /// Identifier of the mission whose log was read.
        /// </summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>
        /// Identifier of the voyage the mission belongs to, when it has one.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Identifier of the captain running the mission, when one is assigned.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// The bounded tail of the log, newline separated, exactly as it was read.
        /// </summary>
        public string Tail { get; set; } = String.Empty;

        /// <summary>
        /// Lowercase hexadecimal SHA-256 of <see cref="Tail"/>. The screen compares this against the
        /// previous sweep's value so an unchanged tail costs no pass call.
        /// </summary>
        public string TailSha256 { get; set; } = String.Empty;

        /// <summary>
        /// Size of <see cref="Tail"/> in UTF-8 bytes. Recorded with an outcome so a tail's size is
        /// measurable without retaining the tail itself.
        /// </summary>
        public int TailBytes { get; set; } = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public LogScreenContext()
        {
        }

        #endregion
    }
}
