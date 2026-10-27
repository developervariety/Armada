namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Context;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// One local, read-only retrieval path over external guidance, caller-visible native records and
    /// the existing vessel code index. Source stores retain their write and authority contracts.
    /// </summary>
    public sealed class ContextSearchService
    {
        /// <summary>Canonical captain recall tool name shared with generated instructions.</summary>
        public const string ToolName = "armada_search_context";

        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly MemoryService _Memories;
        private readonly ContextRetrievalService? _Rules;
        private readonly ICodeIndexService? _Code;
        private readonly LoggingModule? _Logging;
        private readonly ContextSemanticResolver? _Semantic;
        private const int _SemanticCandidates = 40;
        private const int _FusionOffset = 60;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create a query service using the installation's configured database driver.</summary>
        /// <param name="database">Existing persistent store, on any supported provider.</param>
        /// <param name="rules">Built external guidance snapshot, when configured.</param>
        /// <param name="code">Existing local code index, when enabled.</param>
        /// <param name="logging">Optional diagnostics. Query and result text are never logged.</param>
        /// <param name="semantic">Optional local semantic ranking for guidance and native memory.</param>
        public ContextSearchService(DatabaseDriver database, ContextRetrievalService? rules = null, ICodeIndexService? code = null, LoggingModule? logging = null,
            ContextSemanticResolver? semantic = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Memories = new MemoryService(database, logging);
            _Rules = rules;
            _Code = code;
            _Logging = logging;
            _Semantic = semantic;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Search one authorized vessel. A mission credential fixes both the vessel and the ordinary
        /// owner visibility, even when its owner is an administrator. Required guidance is never ranked away.
        /// </summary>
        /// <param name="caller">Authenticated identity; never accepted from query JSON.</param>
        /// <param name="request">Query, vessel and optional-result limits.</param>
        /// <param name="token">Caller cancellation.</param>
        /// <returns>Mandatory guidance, bounded recall and explicit source availability.</returns>
        public async Task<ContextSearchResponse> SearchAsync(AuthContext caller, ContextSearchRequest request, CancellationToken token = default)
        {
            if (caller == null || !caller.IsAuthenticated) throw new UnauthorizedAccessException("Authentication is required.");
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 4096)
                throw new ArgumentException("query must contain 1 through 4096 characters.");
            token.ThrowIfCancellationRequested();

            ContextRecallScope scope = await ContextRecallScope.ResolveAsync(_Database, caller, request.VesselId, token: token).ConfigureAwait(false);
            Vessel vessel = scope.Vessel!;

            ContextSearchResponse response = new ContextSearchResponse { VesselId = vessel.Id };
            List<ContextSearchItem> candidates = new List<ContextSearchItem>();
            AddRules(response, candidates, request.Query, vessel.Name, scope.Mission?.Persona, token);
            await AddMemoriesAsync(response, candidates, scope.Caller, vessel, token).ConfigureAwait(false);

            List<ContextSearchItem> lexical = ContextTextRanker.Rank(request.Query, candidates, token);
            List<ContextSearchItem> ranked = await FuseSemanticAsync(response, vessel.TenantId, request.Query, candidates, lexical, token).ConfigureAwait(false);
            AssignRanks(ranked);
            if (request.IncludeCode) await AddCodeAsync(response, ranked, request.Query, vessel.Id, token).ConfigureAwait(false);

            int limit = Math.Clamp(request.Limit, 1, 40);
            int budget = Math.Clamp(request.MaxBytes, 0, 256000);
            foreach (ContextSearchItem item in ranked.OrderByDescending(item => item.Score)
                .ThenBy(item => item.Source).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                int bytes = Encoding.UTF8.GetByteCount(item.Text);
                if (response.Results.Count >= limit || bytes > budget - response.ResultBytes)
                {
                    response.Truncated = true;
                    continue;
                }
                response.Results.Add(item);
                response.ResultBytes += bytes;
            }
            return response;
        }

        #endregion

        #region Private-Methods

        private void AddRules(ContextSearchResponse response, List<ContextSearchItem> candidates, string query, string vessel, string? persona, CancellationToken token)
        {
            if (_Rules == null)
            {
                response.Sources.Add(new ContextSearchStatus { Source = "rules", Reason = "not_configured" });
                return;
            }
            ContextRetrievalResult rules = _Rules.Retrieve(new ContextRetrievalRequest
            {
                Query = query, Vessel = vessel, RequestingPersona = persona, MaxLeafBytes = Int32.MaxValue
            });
            response.Sources.Add(new ContextSearchStatus { Source = "rules", Available = !rules.Degraded, Reason = rules.Degraded ? "degraded_snapshot" : null, SearchMode = "bm25" });
            foreach (ContextChunk chunk in rules.Core.Concat(rules.MustRetrieve))
            {
                token.ThrowIfCancellationRequested();
                response.Required.Add(FromChunk(chunk, true));
            }
            foreach (ContextChunk chunk in rules.Leaves)
            {
                token.ThrowIfCancellationRequested();
                candidates.Add(FromChunk(chunk, false));
            }
        }

        private async Task AddMemoriesAsync(ContextSearchResponse response, List<ContextSearchItem> candidates, AuthContext scope, Vessel vessel, CancellationToken token)
        {
            try
            {
                string tenantId = OwnershipPolicy.TenantOfRecord(vessel.TenantId);
                List<Memory> memories = await _Memories.LoadVisibleForVesselAsync(scope, tenantId, vessel.Id, token).ConfigureAwait(false);
                foreach (Memory memory in memories)
                {
                    token.ThrowIfCancellationRequested();
                    candidates.Add(new ContextSearchItem
                    {
                        Id = ContextEmbedding.MemorySourceKey(memory.Id), Source = ContextSearchSourceEnum.Memory,
                        Title = memory.Summary ?? memory.Key ?? memory.Topic ?? memory.Id,
                        Text = memory.Content, Reference = memory.Id,
                        Version = memory.Version.ToString(CultureInfo.InvariantCulture), Evidence = memory.SourceDetail,
                        SearchMetadata = memory.Key + " " + memory.Topic + " " + String.Join(" ", memory.Tags)
                    });
                }
                response.Sources.Add(new ContextSearchStatus { Source = "memory", Available = true, SearchMode = "bm25" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure(response, "memory", ex);
            }
        }

        private async Task AddCodeAsync(ContextSearchResponse response, List<ContextSearchItem> ranked, string query, string vesselId, CancellationToken token)
        {
            if (_Code == null)
            {
                response.Sources.Add(new ContextSearchStatus { Source = "code", Reason = "not_configured" });
                return;
            }
            try
            {
                CodeSearchResponse code = await _Code.SearchLexicalAsync(new CodeSearchRequest
                {
                    VesselId = vesselId, Query = query, Limit = 40, IncludeContent = true
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                response.Sources.Add(new ContextSearchStatus { Source = "code", Available = code.Available, Reason = code.UnavailableReason, SearchMode = "lexical", Freshness = code.Status.Freshness });
                if (!code.Available) return;
                int rank = 0;
                foreach (CodeSearchResult hit in code.Results)
                {
                    CodeIndexRecord record = hit.Record;
                    ranked.Add(new ContextSearchItem
                    {
                        Id = "code:" + vesselId + ":" + record.Path + ":" + record.StartLine.ToString(CultureInfo.InvariantCulture),
                        Source = ContextSearchSourceEnum.Code, Title = record.Path,
                        Text = record.Content, Reference = record.Path + ":" + record.StartLine.ToString(CultureInfo.InvariantCulture),
                        Version = record.CommitSha, Freshness = record.Freshness, Score = 1.0 / (60 + ++rank)
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure(response, "code", ex);
            }
        }

        private async Task<List<ContextSearchItem>> FuseSemanticAsync(ContextSearchResponse response, string? tenantId, string query,
            List<ContextSearchItem> candidates, List<ContextSearchItem> lexical, CancellationToken token)
        {
            if (_Semantic == null) return lexical;
            ContextSearchStatus status = new ContextSearchStatus { Source = "semantic", SearchMode = "cosine" };
            response.Sources.Add(status);
            Dictionary<string, double>? similarity;
            try
            {
                ContextSemanticResolution resolution = await _Semantic.ResolveAsync(token).ConfigureAwait(false);
                if (resolution.Index == null)
                {
                    status.Reason = resolution.Reason;
                    return lexical;
                }
                similarity = await resolution.Index.ScoreAsync(String.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId, query, candidates, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                status.Reason = "embedding_timeout";
                return lexical;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _Logging?.Warn("[ContextSearchService] semantic ranking unavailable: " + ex.GetType().Name);
                status.Reason = "source_failed";
                return lexical;
            }
            if (similarity == null)
            {
                status.Reason = "embedding_failed";
                return lexical;
            }

            status.Available = true;
            foreach (ContextSearchStatus source in response.Sources)
                if ((source.Source == "rules" || source.Source == "memory") && source.SearchMode == "bm25") source.SearchMode = "hybrid";
            Dictionary<string, double> fused = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int rank = 0; rank < lexical.Count; rank++)
                fused[lexical[rank].Id] = 1.0 / (_FusionOffset + rank + 1);
            List<KeyValuePair<string, double>> nearest = similarity.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(_SemanticCandidates).ToList();
            for (int rank = 0; rank < nearest.Count; rank++)
            {
                fused.TryGetValue(nearest[rank].Key, out double score);
                fused[nearest[rank].Key] = score + 1.0 / (_FusionOffset + rank + 1);
            }
            Dictionary<string, ContextSearchItem> byId = new Dictionary<string, ContextSearchItem>(StringComparer.Ordinal);
            foreach (ContextSearchItem item in candidates) byId[item.Id] = item;
            return fused.Where(pair => byId.ContainsKey(pair.Key))
                .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => byId[pair.Key]).ToList();
        }

        private void RecordFailure(ContextSearchResponse response, string source, Exception exception)
        {
            _Logging?.Warn("[ContextSearchService] " + source + " unavailable: " + exception.GetType().Name);
            response.Sources.Add(new ContextSearchStatus { Source = source, Reason = "source_failed" });
        }

        private static ContextSearchItem FromChunk(ContextChunk chunk, bool required)
        {
            return new ContextSearchItem
            {
                Id = "context:" + chunk.Id,
                Source = chunk.Path.StartsWith("AI-Memory/", StringComparison.OrdinalIgnoreCase) ? ContextSearchSourceEnum.Rule : ContextSearchSourceEnum.Documentation,
                Title = chunk.Summary, Text = chunk.Text, Reference = chunk.Path,
                Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Text))).ToLowerInvariant(),
                Required = required, SearchMetadata = chunk.Topic + " " + chunk.ReadWhen
            };
        }

        /// <summary>
        /// Guidance and native facts are one ranked list, so the best match leads whatever its source.
        /// Code results join that list by reciprocal rank.
        /// </summary>
        private static void AssignRanks(List<ContextSearchItem> ranked)
        {
            for (int rank = 0; rank < ranked.Count; rank++) ranked[rank].Score = 1.0 / (61 + rank);
        }

        #endregion
    }
}
