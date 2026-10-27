namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Tenant-scoped storage for derived context search vectors.</summary>
    public interface IContextEmbeddingMethods
    {
        /// <summary>Read the stored vectors for the named sources under one model. Absent sources are omitted.</summary>
        Task<List<ContextEmbedding>> ReadAsync(string tenantId, string model, IReadOnlyCollection<string> sourceKeys, CancellationToken token = default);

        /// <summary>Store a vector, replacing any earlier vector for the same tenant, source and model.</summary>
        Task UpsertAsync(ContextEmbedding embedding, CancellationToken token = default);

        /// <summary>Delete every vector derived from one source, under every model. Returns the rows removed.</summary>
        Task<int> DeleteSourceAsync(string tenantId, string sourceKey, CancellationToken token = default);
    }
}
