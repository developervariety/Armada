namespace Armada.Server.Routes
{
    /// <summary>
    /// Outcome of <see cref="RouteRecordId.ResolveAsync"/>: the id to read, or the refusal the route returns.
    /// </summary>
    public sealed class RouteRecordIdResult
    {
        #region Public-Members

        /// <summary>Id to read: the full id an abbreviation resolved to, else the id as given.</summary>
        public string Id { get; }

        /// <summary>Refusal body when the abbreviation is ambiguous; null when the route proceeds.</summary>
        public object? Refusal { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="id">Id to read.</param>
        /// <param name="refusal">Refusal body, or null when the route proceeds.</param>
        public RouteRecordIdResult(string id, object? refusal)
        {
            Id = id;
            Refusal = refusal;
        }

        #endregion
    }
}
