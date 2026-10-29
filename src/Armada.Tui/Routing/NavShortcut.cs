namespace Armada.Tui.Routing
{
    using System;

    /// <summary>
    /// A destination that is a hub tab rather than a sidebar item (for example Jobs on the Activity hub), kept as a Go
    /// command so it stays reachable by name and by its g-letter key. Immutable.
    /// </summary>
    public class NavShortcut
    {
        #region Public-Members

        /// <summary>
        /// Stable command id suffix (the Go command is <c>go.</c> plus this), for example <c>jobs</c>.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Sidebar section label the destination belongs to, for example <c>OPERATIONS</c>.
        /// </summary>
        public string Section { get; }

        /// <summary>
        /// The destination (its target is the hub tab path and query).
        /// </summary>
        public NavItem Item { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="id">Command id suffix.</param>
        /// <param name="section">Section label.</param>
        /// <param name="item">Destination.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public NavShortcut(string id, string section, NavItem item)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Section = section ?? throw new ArgumentNullException(nameof(section));
            Item = item ?? throw new ArgumentNullException(nameof(item));
        }

        #endregion
    }
}
