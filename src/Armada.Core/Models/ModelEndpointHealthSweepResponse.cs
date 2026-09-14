namespace Armada.Core.Models
{
    /// <summary>
    /// Summary returned when a model endpoint health sweep is triggered on demand.
    /// </summary>
    public class ModelEndpointHealthSweepResponse
    {
        #region Public-Members

        /// <summary>
        /// Number of enabled endpoints probed during the sweep. Endpoints sharing a base URL are probed independently.
        /// </summary>
        public int EndpointsProbed { get; set; } = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ModelEndpointHealthSweepResponse()
        {
        }

        #endregion
    }
}
