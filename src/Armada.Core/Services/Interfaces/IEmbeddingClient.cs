namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;

    /// <summary>
    /// Embedding client abstraction for semantic vector generation.
    /// </summary>
    public interface IEmbeddingClient
    {
        /// <summary>
        /// Generate an embedding vector for the supplied text.
        /// </summary>
        Task<float[]> EmbedAsync(string text, CancellationToken token = default);

        /// <summary>
        /// Generate embedding vectors for a batch of texts. Implementations may override this
        /// to use provider-side batching; the default preserves existing per-item behavior.
        /// </summary>
        async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken token = default)
        {
            List<float[]> vectors = new List<float[]>();
            foreach (string text in texts)
            {
                vectors.Add(await EmbedAsync(text, token).ConfigureAwait(false));
            }

            return vectors;
        }
    }
}
