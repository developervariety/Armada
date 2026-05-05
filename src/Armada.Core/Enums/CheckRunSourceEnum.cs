namespace Armada.Core.Enums
{
    /// <summary>
    /// Origin of a structured check run.
    /// </summary>
    public enum CheckRunSourceEnum
    {
        /// <summary>
        /// Executed directly by Armada on the host.
        /// </summary>
        Armada = 0,

        /// <summary>
        /// Imported from an external provider or CI system.
        /// </summary>
        External = 1
    }
}
