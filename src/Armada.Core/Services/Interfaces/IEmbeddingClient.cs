namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Embedding client abstraction for semantic vector generation.
    /// </summary>
    public interface IEmbeddingClient
    {
        /// <summary>
        /// Generate an embedding vector for the supplied text.
        /// </summary>
        Task<float[]> EmbedAsync(string text, CancellationToken token = default);
    }
}
