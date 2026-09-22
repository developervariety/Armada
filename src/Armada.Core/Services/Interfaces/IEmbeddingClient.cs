namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;

    /// <summary>
    /// Embedding client abstraction for semantic vector generation.
    /// </summary>
    public interface IEmbeddingClient
    {
        /// <summary>
        /// Model name this client sends to its provider, when the client knows it. Null means the
        /// code-index settings describe the provider. Part of the vector provenance fingerprint, so a
        /// provider change invalidates vectors produced by the previous provider.
        /// </summary>
        string? EffectiveModel => null;

        /// <summary>
        /// Provider base URL this client sends to, when the client knows it. Null means the
        /// code-index settings describe the provider.
        /// </summary>
        string? EffectiveBaseUrl => null;

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
