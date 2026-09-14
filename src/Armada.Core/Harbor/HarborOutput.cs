namespace Armada.Core.Harbor
{
    /// <summary>
    /// Harbor-to-server event: a chunk of captain output on standard output or standard error.
    /// </summary>
    public class HarborOutput : HarborMessage
    {
        #region Public-Members

        /// <summary>
        /// Job identifier the output belongs to.
        /// </summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// Zero-based position of this chunk in the job's output. The Admiral accepts only the next expected
        /// value, so a replayed or skipped chunk is rejected instead of reordering the output.
        /// </summary>
        public long Sequence { get; set; } = 0;

        /// <summary>
        /// Which standard stream the chunk came from.
        /// </summary>
        public HarborOutputStreamEnum Stream { get; set; } = HarborOutputStreamEnum.Stdout;

        /// <summary>
        /// The output chunk.
        /// </summary>
        public string Data { get; set; } = string.Empty;

        #endregion
    }
}
