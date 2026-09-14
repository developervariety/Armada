namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Reads the applied database schema version without running migrations.
    /// </summary>
    public interface ISelfDeploySchemaVersionReader
    {
        /// <summary>
        /// Read the highest applied migration version.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Schema version.</returns>
        Task<int> ReadAsync(CancellationToken token = default);
    }
}
