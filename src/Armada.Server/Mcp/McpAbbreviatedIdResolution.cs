namespace Armada.Server.Mcp
{
    using System;

    /// <summary>
    /// Outcome of resolving a record id that may be abbreviated: the id to read, and whether an abbreviation
    /// named more than one record the caller can see.
    /// </summary>
    public sealed class McpAbbreviatedIdResolution
    {
        #region Public-Members

        /// <summary>
        /// Id to read: the full id an abbreviation resolved to, else the id as given.
        /// </summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>
        /// True when the abbreviation named more than one record the caller can see; nothing is read then.
        /// </summary>
        public bool Ambiguous { get; set; } = false;

        #endregion
    }
}
