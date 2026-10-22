namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Receives every database row that data expiry is about to delete, after the row is written to
    /// the archive export and before any row of its table is deleted. A trace writer uses it to keep a
    /// compact record of the row after the row itself is gone. An observer that throws stops the
    /// expiry of that table, so no row leaves without its observers having seen it.
    /// </summary>
    public interface IDataExpiryObserver
    {
        /// <summary>
        /// Observe one expiring row.
        /// </summary>
        /// <param name="table">Table name, for example <c>missions</c>.</param>
        /// <param name="row">Column values by column name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        Task OnRowExpiringAsync(string table, IReadOnlyDictionary<string, object?> row, CancellationToken token);
    }
}
