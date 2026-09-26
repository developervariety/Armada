namespace Armada.Core.Services
{
    using System;
    using System.Threading;

    /// <summary>
    /// One in-flight definition-of-done evaluation for a mission. Its token is cancelled when the caller's token is
    /// cancelled or when the mission is cancelled through <see cref="DefinitionOfDoneGateRuns.Cancel"/>. Disposing the
    /// run removes it from the registry.
    /// </summary>
    public sealed class DefinitionOfDoneGateRun : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Mission the evaluation belongs to.
        /// </summary>
        public string MissionId { get; }

        /// <summary>
        /// Token the evaluation observes: the caller's token linked with the mission's cancellation.
        /// </summary>
        public CancellationToken Token => _Source.Token;

        /// <summary>
        /// True when the run was stopped because its mission was cancelled, as distinct from the caller's token.
        /// </summary>
        public bool CancelledByMission => Volatile.Read(ref _CancelledByMission) == 1;

        #endregion

        #region Private-Members

        private readonly CancellationTokenSource _Source;
        private int _CancelledByMission = 0;
        private int _Disposed = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a run linked to the caller's token.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="token">Caller's token.</param>
        internal DefinitionOfDoneGateRun(string missionId, CancellationToken token)
        {
            MissionId = missionId ?? throw new ArgumentNullException(nameof(missionId));
            _Source = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Remove the run from the registry and release its token source.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) != 0) return;
            DefinitionOfDoneGateRuns.Remove(this);
            _Source.Dispose();
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Stop the run because its mission was cancelled.
        /// </summary>
        internal void CancelForMission()
        {
            if (Volatile.Read(ref _Disposed) != 0) return;
            Interlocked.Exchange(ref _CancelledByMission, 1);
            try
            {
                _Source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run finished and was disposed between the check and the cancel; nothing is left to stop.
            }
        }

        #endregion
    }
}
