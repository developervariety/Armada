namespace Armada.Core.Database.Interfaces
{
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Receives the rows a retention purge is about to delete, before it deletes them. The purge
    /// deletes a table's expired rows only after the exporter returns; an exporter that throws stops
    /// the run, so no row it did not take is deleted.
    /// </summary>
    public interface IDataExpiryRowExporter
    {
        /// <summary>
        /// Take every row of one delete statement.
        /// </summary>
        /// <param name="table">Table the rows are deleted from.</param>
        /// <param name="rows">Open reader over the rows, positioned before the first row.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of rows taken.</returns>
        Task<int> ExportAsync(string table, DbDataReader rows, CancellationToken token);
    }
}
