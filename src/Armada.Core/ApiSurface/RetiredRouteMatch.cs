namespace Armada.Core.ApiSurface
{
    using System;

    /// <summary>
    /// A request matched to a retired REST route (<see cref="RetiredSurface.Match"/>).
    /// </summary>
    public class RetiredRouteMatch
    {
        #region Public-Members

        /// <summary>
        /// The retired route, as "METHOD template".
        /// </summary>
        public string Route { get; }

        /// <summary>
        /// The paged replacement.
        /// </summary>
        public string Replacement { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="route">Retired route.</param>
        /// <param name="replacement">Replacement.</param>
        /// <exception cref="ArgumentNullException">route or replacement is null.</exception>
        public RetiredRouteMatch(string route, string replacement)
        {
            Route = route ?? throw new ArgumentNullException(nameof(route));
            Replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
        }

        #endregion
    }
}
