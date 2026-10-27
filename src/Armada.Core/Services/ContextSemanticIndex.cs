namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Cosine similarity over stored vectors of already-authorized context candidates. A vector is reused
    /// only while its model, dimension and content hash match the candidate; any other vector is replaced.
    /// It never widens the candidate set, so it cannot return a record the caller could not already see.
    /// </summary>
    public sealed class ContextSemanticIndex
    {
        #region Public-Members

        /// <summary>Most candidate texts embedded by one query; the rest rank lexically until a later query.</summary>
        public const int MaxNewEmbeddingsPerQuery = 64;

        /// <summary>Longest candidate text, in characters, sent to the embedding endpoint.</summary>
        public const int MaxEmbeddedCharacters = 8000;

        /// <summary>Embedding model recorded with every stored vector.</summary>
        public string Model { get; }

        #endregion

        #region Private-Members

        private const int _EmbedBatch = 16;
        private readonly IContextEmbeddingMethods _Store;
        private readonly IEmbeddingClient _Client;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the index over one store and one embedding endpoint.</summary>
        /// <param name="store">Persistent vector store.</param>
        /// <param name="client">Embedding client for the selected local endpoint.</param>
        /// <param name="model">Model name recorded with stored vectors.</param>
        public ContextSemanticIndex(IContextEmbeddingMethods store, IEmbeddingClient client, string model)
        {
            _Store = store ?? throw new ArgumentNullException(nameof(store));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            Model = String.IsNullOrWhiteSpace(model) ? throw new ArgumentNullException(nameof(model)) : model;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Score candidates by cosine similarity to the query. Candidates without a current vector after
        /// the bounded embedding step are omitted. Returns null when the query itself cannot be embedded.
        /// </summary>
        /// <param name="tenantId">Tenant that owns the candidates.</param>
        /// <param name="query">Caller query.</param>
        /// <param name="candidates">Already-authorized candidates.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task<Dictionary<string, double>?> ScoreAsync(string tenantId, string query, IReadOnlyList<ContextSearchItem> candidates, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            float[] queryVector = await _Client.EmbedAsync(Truncate(query), token).ConfigureAwait(false);
            if (queryVector == null || queryVector.Length == 0 || !Normalize(queryVector)) return null;

            Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ContextSearchItem item in candidates) texts[item.Id] = EmbeddedText(item);
            Dictionary<string, float[]> current = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (ContextEmbedding stored in await _Store.ReadAsync(tenantId, Model, texts.Keys.ToList(), token).ConfigureAwait(false))
            {
                if (!texts.TryGetValue(stored.SourceKey, out string? text)) continue;
                if (stored.Vector.Length != queryVector.Length || stored.ContentSha256 != Hash(text)) continue;
                if (Normalize(stored.Vector)) current[stored.SourceKey] = stored.Vector;
            }

            List<string> missing = texts.Keys.Where(key => !current.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal)
                .Take(MaxNewEmbeddingsPerQuery).ToList();
            for (int offset = 0; offset < missing.Count; offset += _EmbedBatch)
            {
                List<string> batch = missing.Skip(offset).Take(_EmbedBatch).ToList();
                IReadOnlyList<float[]> vectors = await _Client.EmbedBatchAsync(batch.Select(key => texts[key]).ToList(), token).ConfigureAwait(false);
                if (vectors == null || vectors.Count != batch.Count) break;
                for (int index = 0; index < batch.Count; index++)
                {
                    float[] vector = vectors[index];
                    if (vector == null || vector.Length != queryVector.Length || !Normalize(vector)) continue;
                    await _Store.UpsertAsync(new ContextEmbedding
                    {
                        TenantId = tenantId, SourceKey = batch[index], Model = Model,
                        ContentSha256 = Hash(texts[batch[index]]), Vector = vector
                    }, token).ConfigureAwait(false);
                    current[batch[index]] = vector;
                }
            }

            Dictionary<string, double> scores = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, float[]> pair in current)
            {
                double dot = 0;
                for (int index = 0; index < queryVector.Length; index++) dot += queryVector[index] * pair.Value[index];
                scores[pair.Key] = dot;
            }
            return scores;
        }

        #endregion

        #region Private-Methods

        private static string EmbeddedText(ContextSearchItem item)
        {
            return Truncate(item.Title + "\n" + item.Text);
        }

        private static string Truncate(string text)
        {
            text ??= "";
            return text.Length <= MaxEmbeddedCharacters ? text : text.Substring(0, MaxEmbeddedCharacters);
        }

        private static string Hash(string text)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        private static bool Normalize(float[] vector)
        {
            double sum = 0;
            foreach (float value in vector)
            {
                if (Single.IsNaN(value) || Single.IsInfinity(value)) return false;
                sum += value * value;
            }
            if (sum <= 0) return false;
            float scale = (float)(1.0 / Math.Sqrt(sum));
            for (int index = 0; index < vector.Length; index++) vector[index] *= scale;
            return true;
        }

        #endregion
    }
}
