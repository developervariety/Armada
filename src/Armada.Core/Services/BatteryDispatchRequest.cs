namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// One battery task to dispatch to a captain on a fixture vessel.
    /// </summary>
    public class BatteryDispatchRequest
    {
        /// <summary>
        /// Fixture vessel.
        /// </summary>
        public Vessel Vessel { get; }

        /// <summary>
        /// Captain that must run the mission.
        /// </summary>
        public Captain Captain { get; }

        /// <summary>
        /// Mission title.
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// Mission description (the task prompt).
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// How long to wait for the mission to finish, in milliseconds.
        /// </summary>
        public int TimeoutMs { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="vessel">Fixture vessel.</param>
        /// <param name="captain">Captain.</param>
        /// <param name="title">Mission title.</param>
        /// <param name="description">Mission description.</param>
        /// <param name="timeoutMs">Timeout in milliseconds.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public BatteryDispatchRequest(Vessel vessel, Captain captain, string title, string description, int timeoutMs)
        {
            Vessel = vessel ?? throw new ArgumentNullException(nameof(vessel));
            Captain = captain ?? throw new ArgumentNullException(nameof(captain));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            TimeoutMs = timeoutMs;
        }
    }
}
