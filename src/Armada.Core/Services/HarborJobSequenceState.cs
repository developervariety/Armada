namespace Armada.Core.Services
{
    /// <summary>
    /// What the Admiral has received from one Harbor's job outbox: the outbox epoch the Harbor announced in its handshake
    /// and the highest job-message sequence received in it. Used to drop job messages the Harbor replays after a reconnect
    /// and to acknowledge what arrived. Callers lock the instance while reading or changing it.
    /// </summary>
    public class HarborJobSequenceState
    {
        #region Public-Members

        /// <summary>
        /// The Harbor's job outbox epoch, or null before a handshake announced one.
        /// </summary>
        public string? Epoch { get; set; } = null;

        /// <summary>
        /// Highest job-message sequence received in <see cref="Epoch"/>; zero before the first.
        /// </summary>
        public long LastSequence { get; set; } = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with nothing received.
        /// </summary>
        public HarborJobSequenceState()
        {
        }

        #endregion
    }
}
