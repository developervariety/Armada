namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using Microsoft.Extensions.FileSystemGlobbing;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Local Admiral-owned implementation of code indexing, lexical search, and context-pack generation.
    /// </summary>
    public class CodeIndexService : ICodeIndexService
    {
        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private string _Header = "[CodeIndexService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;
        private IEmbeddingClient? _EmbeddingClient;
        private IInferenceClient? _InferenceClient;
        private PolyglotSymbolExtractor _SymbolExtractor = new PolyglotSymbolExtractor();
        private static readonly ConcurrentDictionary<string, CodeIndexActiveUpdate> _ActiveUpdates = new ConcurrentDictionary<string, CodeIndexActiveUpdate>(StringComparer.OrdinalIgnoreCase);
        private const int _DefaultGraphSearchLimit = 20;
        private const int _DefaultGraphNeighborLimit = 25;
        private const int _DefaultImpactDepth = 3;
        private const int _DefaultImpactResultLimit = 50;
        private const int _DefaultAffectedTestsLimit = 20;
        private const int _DefaultExploreDepth = 2;
        private const int _DefaultExploreResultLimit = 25;
        private const int _DefaultFileStructureLimit = 100;
        private const int _MaxGraphDepth = 8;
        private const int _MaxGraphResults = 200;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="git">Git service.</param>
        /// <param name="embeddingClient">Optional embedding client for semantic indexing and search.</param>
        /// <param name="inferenceClient">Optional inference client (reserved for later layers).</param>
        public CodeIndexService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            IEmbeddingClient? embeddingClient = null,
            IInferenceClient? inferenceClient = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _EmbeddingClient = embeddingClient;
            _InferenceClient = inferenceClient;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));

            Vessel vessel = await ReadVesselOrThrowAsync(vesselId, token).ConfigureAwait(false);
            CodeIndexStatus status = await ReadPersistedStatusAsync(vessel).ConfigureAwait(false)
                ?? BuildMissingStatus(vessel);

            status.IndexDirectory = GetVesselIndexDirectory(vessel.Id);
            status.CurrentCommitSha = await TryResolveCurrentCommitAsync(vessel, token).ConfigureAwait(false);
            status.Freshness = ResolveFreshness(status);
            ApplyActiveUpdateStatus(status);
            return status;
        }

        /// <inheritdoc />
        public async Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
        {
            if (!_Settings.CodeIndex.Enabled) throw new InvalidOperationException("Code indexing is disabled.");
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));

            Vessel vessel = await ReadVesselOrThrowAsync(vesselId, token).ConfigureAwait(false);
            CodeIndexActiveUpdate activeUpdate = new CodeIndexActiveUpdate
            {
                StartedUtc = DateTime.UtcNow,
                HeartbeatUtc = DateTime.UtcNow,
                Stage = "starting"
            };
            if (!_ActiveUpdates.TryAdd(vessel.Id, activeUpdate))
            {
                CodeIndexStatus activeStatus = await GetStatusAsync(vessel.Id, token).ConfigureAwait(false);
                throw new InvalidOperationException(BuildUpdateInProgressMessage(activeStatus));
            }

            try
            {
                SetActiveUpdateProgress(vessel.Id, "resolving repository", null, null);
                string repoPath = await ResolveRepositoryPathAsync(vessel, token).ConfigureAwait(false);

                if (!String.IsNullOrWhiteSpace(vessel.RepoUrl))
                {
                    try
                    {
                        SetActiveUpdateProgress(vessel.Id, "fetching", null, null);
                        await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "fetch failed for vessel " + vessel.Id + ": " + ex.Message);
                    }
                }

                SetActiveUpdateProgress(vessel.Id, "resolving commit", null, null);
                string commitSha = await ResolveDefaultBranchCommitAsync(repoPath, vessel.DefaultBranch, token).ConfigureAwait(false);
                string vesselIndexDirectory = GetVesselIndexDirectory(vessel.Id);
                Directory.CreateDirectory(vesselIndexDirectory);
                string indexSettingsFingerprint = BuildIndexSettingsFingerprint();
                string embeddingSettingsFingerprint = BuildEmbeddingSettingsFingerprint();
                CodeIndexStatus? previousStatus = await ReadPersistedStatusAsync(vessel).ConfigureAwait(false);

                if (CanReusePersistedIndex(previousStatus, commitSha, indexSettingsFingerprint, embeddingSettingsFingerprint))
                {
                    previousStatus!.CurrentCommitSha = commitSha;
                    previousStatus.Freshness = "Fresh";
                    previousStatus.IndexDirectory = vesselIndexDirectory;
                    await WriteStatusAsync(vesselIndexDirectory, previousStatus, token).ConfigureAwait(false);
                    return previousStatus;
                }

                List<CodeIndexRecord> previousRecords = await ReadRecordsAsync(vessel.Id, "Fresh", token).ConfigureAwait(false);
                bool canReuseFileRecords = CanReuseFileRecords(previousStatus, indexSettingsFingerprint, previousRecords);
                bool canReuseEmbeddings = CanReuseEmbeddings(previousStatus, embeddingSettingsFingerprint, previousRecords);
                HashSet<string>? changedPaths = canReuseFileRecords
                    ? await TryGetChangedPathsAsync(repoPath, previousStatus!.IndexedCommitSha!, commitSha, token).ConfigureAwait(false)
                    : null;

                string tempDirectory = Path.Combine(Path.GetTempPath(), "armada-code-index-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                try
                {
                    SetActiveUpdateProgress(vessel.Id, "extracting archive", null, null);
                    await ExtractCommitArchiveAsync(repoPath, commitSha, tempDirectory, token).ConfigureAwait(false);

                    SetActiveUpdateProgress(vessel.Id, "scanning files", null, null);
                    List<CodeIndexRecord> records;
                    if (canReuseFileRecords && changedPaths != null)
                    {
                        DateTime reusedAtUtc = DateTime.UtcNow;
                        records = previousRecords
                            .Where(r => !changedPaths.Contains(NormalizeRepoPath(r.Path)))
                            .Select(r => CloneRecordForCommit(r, commitSha, reusedAtUtc))
                            .ToList();
                        records.AddRange(BuildRecordsFromDirectory(vessel, commitSha, tempDirectory, changedPaths));
                        records = records
                            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(r => r.StartLine)
                            .ToList();
                    }
                    else
                    {
                        records = BuildRecordsFromDirectory(vessel, commitSha, tempDirectory);
                    }

                    if (_Settings.CodeIndex.UseSemanticSearch && _EmbeddingClient != null)
                    {
                        SetActiveUpdateProgress(vessel.Id, "embedding chunks", 0, records.Count);
                        await PopulateEmbeddingsAsync(vessel.Id, records, canReuseEmbeddings ? previousRecords : new List<CodeIndexRecord>(), token).ConfigureAwait(false);
                    }

                    List<FileSignatureRecord> signatures = new List<FileSignatureRecord>();
                    if (_Settings.CodeIndex.UseFileSignatures && _InferenceClient != null && _EmbeddingClient != null)
                    {
                        SetActiveUpdateProgress(vessel.Id, "embedding file signatures", 0, records.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Count());
                        const string signatureSystemPrompt =
                            "You are a codebase analyst. Describe the purpose of the following source file in 1-2 sentences, naming the main types and their responsibilities. Output only the description.";
                        List<IGrouping<string, CodeIndexRecord>> signatureGroups = records.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
                        int signatureProcessed = 0;
                        foreach (IGrouping<string, CodeIndexRecord> group in signatureGroups)
                        {
                            CodeIndexRecord representative = group
                                .OrderBy(r => r.StartLine)
                                .First();

                            string excerpt = representative.Content ?? "";
                            if (excerpt.Length > 2000)
                            {
                                excerpt = excerpt.Substring(0, 2000);
                            }

                            string userMessage = "File: " + representative.Path + "\n\n" + excerpt;
                            try
                            {
                                string signature = (await _InferenceClient.CompleteAsync(signatureSystemPrompt, userMessage, token).ConfigureAwait(false) ?? "").Trim();
                                if (String.IsNullOrWhiteSpace(signature))
                                {
                                    continue;
                                }

                                float[] vector = await _EmbeddingClient.EmbedAsync(signature, token).ConfigureAwait(false);
                                if (vector == null || vector.Length == 0)
                                {
                                    continue;
                                }

                                signatures.Add(new FileSignatureRecord
                                {
                                    VesselId = representative.VesselId,
                                    Path = representative.Path,
                                    CommitSha = representative.CommitSha,
                                    ContentHash = representative.ContentHash,
                                    Language = representative.Language,
                                    Signature = signature,
                                    SignatureVector = vector,
                                    GeneratedAtUtc = DateTime.UtcNow
                                });
                            }
                            catch (Exception ex)
                            {
                                _Logging.Warn(_Header + "signature generation failed for file " + representative.Path + ": " + ex.Message);
                            }
                            finally
                            {
                                signatureProcessed++;
                                LogEmbeddingProgress(signatureProcessed, signatureGroups.Count, vessel.Id, "Embedded file signatures");
                            }
                        }

                        await WriteSignaturesAsync(vessel.Id, signatures, token).ConfigureAwait(false);
                    }

                    CodeIndexStatus status = new CodeIndexStatus
                    {
                        VesselId = vessel.Id,
                        VesselName = vessel.Name,
                        DefaultBranch = vessel.DefaultBranch,
                        IndexedCommitSha = commitSha,
                        CurrentCommitSha = commitSha,
                        IndexedAtUtc = DateTime.UtcNow,
                        Freshness = "Fresh",
                        DocumentCount = CountDocuments(records),
                        ChunkCount = records.Count,
                        IndexDirectory = vesselIndexDirectory,
                        LastError = null,
                        IndexSettingsFingerprint = indexSettingsFingerprint,
                        EmbeddingSettingsFingerprint = embeddingSettingsFingerprint,
                        UseSemanticSearch = _Settings.CodeIndex.UseSemanticSearch,
                        EmbeddingModel = _Settings.CodeIndex.UseSemanticSearch ? _Settings.CodeIndex.EmbeddingModel : null
                    };

                    SetActiveUpdateProgress(vessel.Id, "writing index", null, null);
                    await WriteIndexAsync(vesselIndexDirectory, status, records, token).ConfigureAwait(false);
                    SetActiveUpdateProgress(vessel.Id, "writing graph sidecars", null, null);
                    await WriteGraphSidecarsAsync(vesselIndexDirectory, vessel.Id, commitSha, tempDirectory, records, token).ConfigureAwait(false);
                    return status;
                }
                catch (Exception ex)
                {
                    CodeIndexStatus errorStatus = BuildMissingStatus(vessel);
                    errorStatus.CurrentCommitSha = commitSha;
                    errorStatus.IndexedAtUtc = DateTime.UtcNow;
                    errorStatus.Freshness = "Error";
                    errorStatus.LastError = ex.Message;
                    await WriteStatusAsync(vesselIndexDirectory, errorStatus, token).ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    TryDeleteDirectory(tempDirectory);
                }
            }
            finally
            {
                _ActiveUpdates.TryRemove(vessel.Id, out _);
            }
        }

        /// <inheritdoc />
        public async Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Query)) throw new ArgumentNullException(nameof(request.Query));

            CodeIndexStatus status = await GetStatusAsync(request.VesselId, token).ConfigureAwait(false);
            if (status.IndexedAtUtc == null || String.Equals(status.Freshness, "Missing", StringComparison.Ordinal))
            {
                status = await UpdateAsync(request.VesselId, token).ConfigureAwait(false);
            }

            List<CodeIndexRecord> records = await ReadRecordsAsync(request.VesselId, status.Freshness, token).ConfigureAwait(false);
            string[] terms = SplitQueryTerms(request.Query);
            int limit = ClampLimit(request.Limit, _Settings.CodeIndex.MaxSearchResults);

            float[]? queryVector = null;
            if (_Settings.CodeIndex.UseSemanticSearch && _EmbeddingClient != null)
            {
                try
                {
                    float[] rawQueryVector = await _EmbeddingClient.EmbedAsync(request.Query, token).ConfigureAwait(false);
                    if (rawQueryVector != null && rawQueryVector.Length > 0)
                        queryVector = rawQueryVector;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "query embedding failed for vessel " + request.VesselId + ": " + ex.Message);
                }
            }

            Dictionary<string, FileSignatureRecord>? signaturesByPath = null;
            if (_Settings.CodeIndex.UseFileSignatures && queryVector != null)
            {
                List<FileSignatureRecord> signatures = await ReadSignaturesAsync(request.VesselId, token).ConfigureAwait(false);
                signaturesByPath = signatures
                    .Where(s => !String.IsNullOrWhiteSpace(s.Path))
                    .GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, double> graphBoostsByPath = _Settings.CodeIndex.UseGraphSearchBoosts
                ? await BuildGraphSearchBoostsAsync(request.VesselId, request.Query, token).ConfigureAwait(false)
                : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            List<CodeSearchResult> results = new List<CodeSearchResult>();
            foreach (CodeIndexRecord record in records)
            {
                if (!request.IncludeReferenceOnly && record.IsReferenceOnly) continue;
                if (!String.IsNullOrWhiteSpace(request.PathPrefix) &&
                    !record.Path.StartsWith(NormalizeRepoPath(request.PathPrefix!), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!String.IsNullOrWhiteSpace(request.Language) &&
                    !String.Equals(record.Language, request.Language, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double score = ScoreRecord(record, request.Query, terms, queryVector);
                if (graphBoostsByPath.TryGetValue(record.Path, out double graphBoost))
                {
                    score += graphBoost;
                }

                if (signaturesByPath != null
                    && queryVector != null
                    && signaturesByPath.TryGetValue(record.Path, out FileSignatureRecord? signatureRecord)
                    && signatureRecord.SignatureVector != null
                    && signatureRecord.SignatureVector.Length > 0)
                {
                    double signatureSimilarity = CosineSimilarity(queryVector, signatureRecord.SignatureVector);
                    score += _Settings.CodeIndex.FileSignatureBoostWeight * signatureSimilarity;
                }

                if (score <= 0) continue;

                CodeIndexRecord outputRecord = CopyRecord(record);
                if (!request.IncludeContent)
                {
                    outputRecord.Content = "";
                }

                results.Add(new CodeSearchResult
                {
                    Score = score,
                    Record = outputRecord,
                    Excerpt = BuildExcerpt(record.Content, terms)
                });
            }

            results = results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Record.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Record.StartLine)
                .Take(limit)
                .ToList();

            return new CodeSearchResponse
            {
                Status = status,
                Query = request.Query,
                Results = results
            };
        }

        /// <inheritdoc />
        public async Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.FleetId)) throw new ArgumentNullException(nameof(request.FleetId));
            if (String.IsNullOrWhiteSpace(request.Query)) throw new ArgumentNullException(nameof(request.Query));

            List<Vessel> vessels = await _Database.Vessels.EnumerateByFleetAsync(request.FleetId, token).ConfigureAwait(false);
            int vesselCount = vessels.Count;
            int perVesselDefault = ClampLimit(0, _Settings.CodeIndex.MaxSearchResults);
            int requestedLimit = request.Limit > 0 ? request.Limit : perVesselDefault * Math.Max(1, vesselCount);
            int limit = Math.Min(50, requestedLimit);
            if (limit < 1) limit = 1;

            List<FleetCodeSearchResult> merged = new List<FleetCodeSearchResult>();
            List<string> warnings = new List<string>();

            foreach (Vessel vessel in vessels)
            {
                try
                {
                    CodeSearchRequest vesselRequest = new CodeSearchRequest
                    {
                        VesselId = vessel.Id,
                        Query = request.Query,
                        Limit = limit,
                        PathPrefix = request.PathPrefix,
                        Language = request.Language,
                        IncludeContent = request.IncludeContent,
                        IncludeReferenceOnly = request.IncludeReferenceOnly
                    };

                    CodeSearchResponse search = await SearchAsync(vesselRequest, token).ConfigureAwait(false);

                    if (!String.Equals(search.Status.Freshness, "Fresh", StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add("vessel " + vessel.Id + " (" + vessel.Name + ") index freshness is " + search.Status.Freshness);
                    }

                    if (!String.IsNullOrWhiteSpace(search.Status.LastError))
                    {
                        warnings.Add("vessel " + vessel.Id + " (" + vessel.Name + ") index error: " + search.Status.LastError);
                    }

                    foreach (CodeSearchResult result in search.Results)
                    {
                        merged.Add(new FleetCodeSearchResult
                        {
                            VesselId = vessel.Id,
                            VesselName = vessel.Name,
                            Score = result.Score,
                            Record = result.Record,
                            Excerpt = result.Excerpt
                        });
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("vessel " + vessel.Id + " (" + vessel.Name + ") search failed: " + ex.Message);
                }
            }

            List<FleetCodeSearchResult> results = merged
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Record.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Record.StartLine)
                .Take(limit)
                .ToList();

            return new FleetCodeSearchResponse
            {
                FleetId = request.FleetId,
                Query = request.Query,
                Results = results,
                Warnings = warnings
            };
        }

        /// <inheritdoc />
        public async Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Goal)) throw new ArgumentNullException(nameof(request.Goal));

            int tokenBudget = request.TokenBudget;
            if (tokenBudget < 500) tokenBudget = 500;
            if (tokenBudget > 20000) tokenBudget = 20000;

            int maxResults = request.MaxResults ?? _Settings.CodeIndex.MaxContextPackResults;
            maxResults = ClampLimit(maxResults, _Settings.CodeIndex.MaxContextPackResults);

            // v2-F1 pre-selection pass: load vessel_pack_hints, filter by goal regex, compute hard-include / hard-exclude.
            List<VesselPackHint> matchedHints = new List<VesselPackHint>();
            List<string> warnings = new List<string>();
            HashSet<string> hardExcludePatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                List<VesselPackHint> activeHints = await _Database.VesselPackHints
                    .EnumerateActiveByVesselAsync(request.VesselId, token).ConfigureAwait(false);
                foreach (VesselPackHint h in activeHints)
                {
                    if (TryMatchGoalPattern(h.GoalPattern, request.Goal))
                    {
                        matchedHints.Add(h);
                        foreach (string excl in h.GetMustExclude())
                            if (!String.IsNullOrWhiteSpace(excl))
                                hardExcludePatterns.Add(excl);
                    }
                }
                matchedHints = matchedHints.OrderByDescending(h => h.Priority).ToList();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "vessel_pack_hints lookup failed for " + request.VesselId + ": " + ex.Message);
            }

            CodeSearchRequest searchRequest = new CodeSearchRequest
            {
                VesselId = request.VesselId,
                Query = request.Goal,
                Limit = maxResults,
                IncludeContent = true,
                IncludeReferenceOnly = false
            };

            CodeSearchResponse search = await SearchAsync(searchRequest, token).ConfigureAwait(false);
            if (hardExcludePatterns.Count > 0)
            {
                Matcher excludeMatcher = new Matcher();
                foreach (string p in hardExcludePatterns) excludeMatcher.AddInclude(p);
                List<CodeSearchResult> filtered = new List<CodeSearchResult>();
                int dropped = 0;
                foreach (CodeSearchResult r in search.Results)
                {
                    string path = r.Record?.Path ?? "";
                    PatternMatchingResult match = excludeMatcher.Match(path);
                    if (match.HasMatches) { dropped++; continue; }
                    filtered.Add(r);
                }
                search.Results = filtered;
                if (dropped > 0)
                {
                    warnings.Add("hard_exclude_filtered: dropped " + dropped + " result(s) matching pack-hint mustExclude globs");
                }
            }

            GraphContextPackExpansion graphExpansion = await BuildGraphContextPackExpansionAsync(request.VesselId, request.Goal, search, token).ConfigureAwait(false);
            foreach (string warning in graphExpansion.Warnings)
            {
                warnings.Add(warning);
            }

            string markdown = BuildContextPackMarkdown(request.Goal, tokenBudget, search);
            if (graphExpansion.Used)
            {
                markdown = markdown.TrimEnd() + "\n\n" + graphExpansion.Markdown.TrimEnd() + "\n";
            }

            string? summarizedMarkdown = null;
            bool isSummarized = false;

            if (_Settings.CodeIndex.UseSummarizer && _InferenceClient != null && search.Results.Count > 0)
            {
                string systemPrompt = "You are a codebase analyst. Given code chunks from a repository, produce a compact markdown summary for a software engineer who needs to understand the relevant patterns before making a change. Output: first a 3-5 sentence synthesis naming the key types, their responsibilities, and any important call chains. Then a bulleted file-by-file list of key types and their roles. Be concise. No introductory text. Output only the summary markdown.";
                string userMessage = "Goal: " + request.Goal + "\n\n" + markdown;
                try
                {
                    string summary = (await _InferenceClient.CompleteAsync(systemPrompt, userMessage, token).ConfigureAwait(false) ?? "").Trim();
                    if (!String.IsNullOrEmpty(summary))
                    {
                        summarizedMarkdown = summary;
                        isSummarized = true;
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "summarizer failed: " + ex.Message);
                }
            }

            string materializedPath = await WriteContextPackAsync(request.VesselId, isSummarized ? summarizedMarkdown! : markdown, token).ConfigureAwait(false);

            ContextPackResponse response = new ContextPackResponse
            {
                Status = search.Status,
                Goal = request.Goal,
                Markdown = markdown,
                EstimatedTokens = EstimateTokens(markdown),
                MaterializedPath = materializedPath,
                Results = search.Results,
                GraphIncludedFiles = graphExpansion.IncludedFiles,
                SummarizedMarkdown = summarizedMarkdown,
                IsSummarized = isSummarized
            };
            response.PrestagedFiles.Add(new PrestagedFile(materializedPath, "_briefing/context-pack.md"));
            foreach (VesselPackHint h in matchedHints)
                response.MatchedHintIds.Add(h.Id);
            foreach (string w in warnings)
                response.Warnings.Add(w);
            response.Metrics = BuildContextPackMetrics(response, graphExpansionUsed: graphExpansion.Used, vesselCount: 1);
            return response;
        }

        /// <inheritdoc />
        public async Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.FleetId)) throw new ArgumentNullException(nameof(request.FleetId));
            if (String.IsNullOrWhiteSpace(request.Goal)) throw new ArgumentNullException(nameof(request.Goal));

            int tokenBudget = request.TokenBudget;
            if (tokenBudget < 500) tokenBudget = 500;
            if (tokenBudget > 20000) tokenBudget = 20000;

            List<Vessel> vessels = await _Database.Vessels.EnumerateByFleetAsync(request.FleetId, token).ConfigureAwait(false);
            int vesselCount = Math.Max(1, vessels.Count);
            int perVesselBudget = Math.Max(2000, tokenBudget / vesselCount);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("# Armada Fleet Code Context Pack");
            builder.AppendLine();
            builder.AppendLine("Goal: " + request.Goal);
            builder.AppendLine();
            builder.AppendLine("FleetId: " + request.FleetId);
            builder.AppendLine();
            builder.AppendLine("This is repo discovery evidence from Armada's code index across fleet vessels. Playbooks, vessel CLAUDE.md, and project CLAUDE.md rules win on conflict.");

            List<string> warnings = new List<string>();
            List<string> includedFiles = new List<string>();
            List<string> matchedHintIds = new List<string>();
            int resultCount = 0;
            bool graphExpansionUsed = false;
            foreach (Vessel vessel in vessels)
            {
                try
                {
                    ContextPackRequest vesselRequest = new ContextPackRequest
                    {
                        VesselId = vessel.Id,
                        Goal = request.Goal,
                        TokenBudget = perVesselBudget,
                        MaxResults = request.MaxResultsPerVessel
                    };
                    ContextPackResponse vesselPack = await BuildContextPackAsync(vesselRequest, token).ConfigureAwait(false);

                    builder.AppendLine();
                    builder.AppendLine("## Vessel: " + vessel.Name);
                    builder.AppendLine();
                    builder.AppendLine(vesselPack.Markdown.Trim());

                    resultCount += vesselPack.Metrics.ResultCount;
                    graphExpansionUsed = graphExpansionUsed || vesselPack.Metrics.GraphExpansionUsed;
                    foreach (string file in vesselPack.Metrics.IncludedFiles)
                    {
                        includedFiles.Add(vessel.Id + ":" + file);
                    }
                    foreach (string hintId in vesselPack.Metrics.MatchedHintIds)
                    {
                        matchedHintIds.Add(hintId);
                    }
                    foreach (string warning in vesselPack.Warnings)
                    {
                        warnings.Add("vessel " + vessel.Id + " (" + vessel.Name + "): " + warning);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("vessel " + vessel.Id + " (" + vessel.Name + ") context pack failed: " + ex.Message);
                }
            }

            if (vessels.Count == 0)
            {
                builder.AppendLine();
                builder.AppendLine("No vessels were found for this fleet.");
            }

            string markdown = builder.ToString().TrimEnd() + "\n";
            string? summarizedMarkdown = null;
            bool isSummarized = false;

            if (_Settings.CodeIndex.UseSummarizer && _InferenceClient != null && vessels.Count > 0)
            {
                string systemPrompt = "You are a codebase analyst. Given code chunks from multiple repositories, produce a compact markdown summary for a software engineer who needs to understand the relevant patterns before making a change. Output: first a 3-5 sentence synthesis naming the key types, their responsibilities, and any important call chains across vessels. Then a bulleted file-by-file list of key types and their roles grouped by vessel. Be concise. No introductory text. Output only the summary markdown.";
                string userMessage = "Goal: " + request.Goal + "\n\n" + markdown;
                try
                {
                    string summary = (await _InferenceClient.CompleteAsync(systemPrompt, userMessage, token).ConfigureAwait(false) ?? "").Trim();
                    if (!String.IsNullOrEmpty(summary))
                    {
                        summarizedMarkdown = summary;
                        isSummarized = true;
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "fleet summarizer failed: " + ex.Message);
                }
            }

            string materializedPath = await WriteFleetContextPackAsync(request.FleetId, isSummarized ? summarizedMarkdown! : markdown, token).ConfigureAwait(false);

            FleetContextPackResponse response = new FleetContextPackResponse
            {
                FleetId = request.FleetId,
                Goal = request.Goal,
                Markdown = markdown,
                SummarizedMarkdown = summarizedMarkdown,
                IsSummarized = isSummarized,
                EstimatedTokens = EstimateTokens(markdown),
                MaterializedPath = materializedPath,
                Warnings = warnings
            };
            response.PrestagedFiles.Add(new PrestagedFile(materializedPath, "_briefing/context-pack.md"));
            response.Metrics = new ContextPackMetrics
            {
                ResultCount = resultCount,
                IncludedFileCount = includedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                IncludedFiles = includedFiles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
                MatchedHintCount = matchedHintIds.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                MatchedHintIds = matchedHintIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                GraphExpansionUsed = graphExpansionUsed,
                WarningCount = warnings.Count,
                IsSummarized = isSummarized,
                PrestagedFileCount = response.PrestagedFiles.Count,
                EstimatedTokens = response.EstimatedTokens,
                VesselCount = vessels.Count
            };
            return response;
        }

        /// <inheritdoc />
        public async Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Query)) throw new ArgumentNullException(nameof(request.Query));

            GraphQueryContext context = await LoadGraphQueryContextAsync(request.VesselId, token).ConfigureAwait(false);
            int limit = ClampGraphLimit(request.Limit, _DefaultGraphSearchLimit, _MaxGraphResults);
            string query = request.Query.Trim();
            string pathPrefix = String.IsNullOrWhiteSpace(request.PathPrefix) ? "" : NormalizeRepoPath(request.PathPrefix.Trim());

            List<CodeGraphSymbolSearchResult> results = new List<CodeGraphSymbolSearchResult>();
            foreach (CodeGraphSymbolRecord symbol in context.Symbols)
            {
                if (!String.IsNullOrWhiteSpace(pathPrefix)
                    && !symbol.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (request.Kind.HasValue && symbol.Kind != request.Kind.Value) continue;

                ScoredSymbolMatch match = ScoreSymbolMatch(symbol, query);
                if (match.Score <= 0) continue;
                results.Add(new CodeGraphSymbolSearchResult
                {
                    Score = match.Score,
                    MatchReason = match.Reason,
                    Symbol = symbol
                });
            }

            results = results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => SelectSymbolName(r.Symbol), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Symbol.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Symbol.StartLine)
                .Take(limit)
                .ToList();

            return new CodeGraphSymbolSearchResponse
            {
                Status = context.Status,
                Query = request.Query,
                Results = results,
                Warnings = context.Warnings
            };
        }

        /// <inheritdoc />
        public async Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
        {
            return await GetNeighborsAsync(request, includeCallers: true, includeCallees: false, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
        {
            return await GetNeighborsAsync(request, includeCallers: false, includeCallees: true, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Symbol)) throw new ArgumentNullException(nameof(request.Symbol));

            GraphQueryContext context = await LoadGraphQueryContextAsync(request.VesselId, token).ConfigureAwait(false);
            int maxDepth = ClampGraphDepth(request.MaxDepth, _DefaultImpactDepth);
            int maxResults = ClampGraphLimit(request.MaxResults, _DefaultImpactResultLimit, _MaxGraphResults);
            return ComputeImpact(context, request.Symbol, request.Direction, maxDepth, maxResults);
        }

        /// <inheritdoc />
        public async Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Symbol)) throw new ArgumentNullException(nameof(request.Symbol));

            int maxDepth = ClampGraphDepth(request.MaxDepth, _DefaultImpactDepth);
            int maxResults = ClampGraphLimit(request.MaxResults, _DefaultAffectedTestsLimit, _MaxGraphResults);

            GraphQueryContext context = await LoadGraphQueryContextAsync(request.VesselId, token).ConfigureAwait(false);
            CodeGraphImpactResponse impact = ComputeImpact(
                context,
                request.Symbol,
                CodeGraphTraversalDirectionEnum.Both,
                maxDepth,
                _MaxGraphResults);

            Dictionary<string, AffectedTestAccumulator> explicitCandidates = new Dictionary<string, AffectedTestAccumulator>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, AffectedTestAccumulator> conventionCandidates = new Dictionary<string, AffectedTestAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (CodeGraphImpactResult hit in impact.Results)
            {
                ClassifyTestSignal(hit.Symbol, out bool explicitSignal, out bool conventionSignal, out string signalReason);
                if (!explicitSignal && !conventionSignal) continue;

                AffectedTestAccumulator bucket = explicitSignal
                    ? GetOrCreateAffectedTestAccumulator(explicitCandidates, hit.Symbol.Path)
                    : GetOrCreateAffectedTestAccumulator(conventionCandidates, hit.Symbol.Path);

                bucket.Path = hit.Symbol.Path;
                bucket.Symbol = SelectSymbolName(hit.Symbol);
                bucket.IsExplicitSignal = explicitSignal || bucket.IsExplicitSignal;
                bucket.MinDepth = Math.Min(bucket.MinDepth, hit.MinDepth);
                bucket.Score = Math.Max(bucket.Score, hit.Score + (explicitSignal ? 150 : 40));
                if (bucket.Reasons.Count < 4)
                {
                    AddReason(bucket.Reasons, signalReason);
                    foreach (string reason in hit.Reasons)
                    {
                        if (bucket.Reasons.Count >= 4) break;
                        AddReason(bucket.Reasons, reason);
                    }
                }
            }

            if (explicitCandidates.Count == 0)
            {
                HashSet<string> reachablePaths = new HashSet<string>(
                    impact.Results.Select(r => r.Symbol.Path),
                    StringComparer.OrdinalIgnoreCase);

                foreach (CodeGraphSymbolRecord seed in impact.ResolvedSeedSymbols)
                {
                    string seedStem = Path.GetFileNameWithoutExtension(seed.Path) ?? "";
                    if (String.IsNullOrWhiteSpace(seedStem)) continue;

                    foreach (CodeGraphImpactResult hit in impact.Results)
                    {
                        if (!LooksLikeTestPath(hit.Symbol.Path)) continue;
                        string testStem = Path.GetFileNameWithoutExtension(hit.Symbol.Path) ?? "";
                        if (!testStem.Contains(seedStem, StringComparison.OrdinalIgnoreCase)) continue;

                        AffectedTestAccumulator bucket = GetOrCreateAffectedTestAccumulator(conventionCandidates, hit.Symbol.Path);
                        bucket.Path = hit.Symbol.Path;
                        bucket.Symbol = SelectSymbolName(hit.Symbol);
                        bucket.IsExplicitSignal = false;
                        bucket.MinDepth = Math.Min(bucket.MinDepth, hit.MinDepth);
                        bucket.Score = Math.Max(bucket.Score, hit.Score + 30);
                        AddReason(bucket.Reasons, "filename convention matched seed file stem");
                    }

                    // Also scan all symbols in the index that are not graph-reachable, so a
                    // test file that exists in symbols.jsonl but has no edge to the target symbol
                    // is still suggested when its filename stem matches the seed.
                    foreach (CodeGraphSymbolRecord sym in context.Symbols)
                    {
                        if (!LooksLikeTestPath(sym.Path)) continue;
                        if (reachablePaths.Contains(sym.Path)) continue;
                        string testStem = Path.GetFileNameWithoutExtension(sym.Path) ?? "";
                        if (!testStem.Contains(seedStem, StringComparison.OrdinalIgnoreCase)) continue;

                        AffectedTestAccumulator bucket = GetOrCreateAffectedTestAccumulator(conventionCandidates, sym.Path);
                        bucket.Path = sym.Path;
                        bucket.Symbol = SelectSymbolName(sym);
                        bucket.IsExplicitSignal = false;
                        bucket.MinDepth = Math.Min(bucket.MinDepth, Int32.MaxValue);
                        bucket.Score = Math.Max(bucket.Score, 20);
                        AddReason(bucket.Reasons, "filename convention matched seed file stem (not graph-reachable)");
                    }
                }
            }

            List<CodeGraphAffectedTestCandidate> candidates = explicitCandidates.Values
                .Concat(conventionCandidates.Values)
                .OrderByDescending(c => c.IsExplicitSignal)
                .ThenByDescending(c => c.Score)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .Select(c => new CodeGraphAffectedTestCandidate
                {
                    TestPath = c.Path,
                    Symbol = c.Symbol,
                    Score = c.Score,
                    IsExplicitSignal = c.IsExplicitSignal,
                    EvidenceDepth = c.MinDepth == Int32.MaxValue ? 0 : c.MinDepth,
                    Reasons = c.Reasons.ToList()
                })
                .ToList();

            return new CodeGraphAffectedTestsResponse
            {
                Status = impact.Status,
                RequestedSymbol = request.Symbol,
                MaxDepth = maxDepth,
                ResolvedSeedSymbols = impact.ResolvedSeedSymbols,
                Candidates = candidates,
                Warnings = impact.Warnings
            };
        }

        /// <inheritdoc />
        public async Task<CodeGraphNodeResponse> GetNodeAsync(CodeGraphNodeRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Symbol)) throw new ArgumentNullException(nameof(request.Symbol));

            GraphQueryContext context = await LoadGraphQueryContextAsync(request.VesselId, token).ConfigureAwait(false);
            List<CodeGraphSymbolRecord> resolved = ResolveSeedSymbols(context, request.Symbol, _DefaultGraphNeighborLimit);
            CodeGraphSymbolRecord? primary = resolved.FirstOrDefault();

            List<CodeGraphNeighborResult> callers = new List<CodeGraphNeighborResult>();
            List<CodeGraphNeighborResult> callees = new List<CodeGraphNeighborResult>();
            CodeGraphSourceSection? source = null;

            if (primary != null)
            {
                callers = ResolveDirectNeighbors(context, primary, includeCallers: true)
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => SelectSymbolName(r.Symbol), StringComparer.OrdinalIgnoreCase)
                    .Take(_DefaultGraphNeighborLimit)
                    .ToList();

                callees = ResolveDirectNeighbors(context, primary, includeCallers: false)
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => SelectSymbolName(r.Symbol), StringComparer.OrdinalIgnoreCase)
                    .Take(_DefaultGraphNeighborLimit)
                    .ToList();

                if (request.IncludeSource)
                {
                    source = await TryBuildSourceSectionAsync(
                        request.VesselId,
                        primary,
                        request.SourcePadding,
                        token).ConfigureAwait(false);
                }
            }

            return new CodeGraphNodeResponse
            {
                Status = context.Status,
                RequestedSymbol = request.Symbol,
                ResolvedSymbols = resolved,
                Callers = callers,
                Callees = callees,
                Source = source,
                Warnings = context.Warnings
            };
        }

        /// <inheritdoc />
        public async Task<CodeGraphFileStructureResponse> GetFileStructureAsync(CodeGraphFileStructureRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));

            GraphQueryContext context = await LoadGraphQueryContextAsync(request.VesselId, token).ConfigureAwait(false);
            string pathPrefix = String.IsNullOrWhiteSpace(request.PathPrefix) ? "" : NormalizeRepoPath(request.PathPrefix.Trim());
            int limit = ClampGraphLimit(request.Limit, _DefaultFileStructureLimit, _MaxGraphResults);

            List<CodeIndexRecord> records = await ReadRecordsAsync(request.VesselId, context.Status.Freshness, token).ConfigureAwait(false);
            Dictionary<string, string> languageByPath = records
                .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Language ?? "", StringComparer.OrdinalIgnoreCase);

            List<CodeGraphFileStructureEntry> files = context.Symbols
                .Where(s => String.IsNullOrWhiteSpace(pathPrefix) || s.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(g => new CodeGraphFileStructureEntry
                {
                    Path = g.Key,
                    Language = languageByPath.TryGetValue(g.Key, out string? language) ? language : DetectLanguage(g.Key),
                    SymbolCount = g.Count(),
                    Symbols = request.IncludeSymbols
                        ? g.OrderBy(s => s.StartLine).ThenBy(s => SelectSymbolName(s), StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<CodeGraphSymbolRecord>()
                })
                .ToList();

            return new CodeGraphFileStructureResponse
            {
                Status = context.Status,
                Files = files,
                Warnings = context.Warnings
            };
        }

        /// <inheritdoc />
        public async Task<CodeGraphExploreResponse> ExploreAsync(CodeGraphExploreRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Query)) throw new ArgumentNullException(nameof(request.Query));

            GraphQueryContext context = await LoadGraphQueryContextAsync(request.VesselId, token).ConfigureAwait(false);
            int maxDepth = ClampGraphDepth(request.MaxDepth, _DefaultExploreDepth);
            int maxResults = ClampGraphLimit(request.MaxResults, _DefaultExploreResultLimit, _MaxGraphResults);

            List<CodeGraphSymbolRecord> seeds = ResolveSeedSymbols(context, request.Query, _DefaultGraphNeighborLimit);
            Dictionary<string, (CodeGraphSymbolRecord Symbol, double Score)> included = new Dictionary<string, (CodeGraphSymbolRecord Symbol, double Score)>(StringComparer.OrdinalIgnoreCase);

            foreach (CodeGraphSymbolRecord seed in seeds)
            {
                string key = BuildSymbolKey(seed);
                if (!String.IsNullOrWhiteSpace(key)) included[key] = (seed, 500);
            }

            CodeGraphImpactResponse impact = ComputeImpact(context, request.Query, CodeGraphTraversalDirectionEnum.Both, maxDepth, maxResults);
            foreach (CodeGraphImpactResult result in impact.Results)
            {
                string key = BuildSymbolKey(result.Symbol);
                if (String.IsNullOrWhiteSpace(key)) continue;
                if (!included.TryGetValue(key, out (CodeGraphSymbolRecord Symbol, double Score) existing) || result.Score > existing.Score)
                {
                    included[key] = (result.Symbol, result.Score);
                }
            }

            List<(CodeGraphSymbolRecord Symbol, double Score)> orderedSymbols = included.Values
                .OrderByDescending(v => v.Score)
                .ThenBy(v => SelectSymbolName(v.Symbol), StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToList();

            HashSet<string> includedKeys = new HashSet<string>(
                orderedSymbols.Select(v => BuildSymbolKey(v.Symbol)).Where(k => !String.IsNullOrWhiteSpace(k)),
                StringComparer.OrdinalIgnoreCase);

            List<CodeGraphEdgeRecord> relationships = context.Edges
                .Where(e => EdgeEndpointsIncluded(context, e, includedKeys))
                .OrderBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.SourceLine)
                .Take(_MaxGraphResults)
                .ToList();

            List<CodeGraphExploreFile> files = new List<CodeGraphExploreFile>();
            foreach (IGrouping<string, (CodeGraphSymbolRecord Symbol, double Score)> group in orderedSymbols.GroupBy(v => v.Symbol.Path, StringComparer.OrdinalIgnoreCase))
            {
                CodeGraphExploreFile file = new CodeGraphExploreFile
                {
                    Path = group.Key,
                    Score = group.Max(v => v.Score),
                    Symbols = group.Select(v => v.Symbol)
                        .OrderBy(s => s.StartLine)
                        .ThenBy(s => SelectSymbolName(s), StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                if (request.IncludeSource)
                {
                    foreach (CodeGraphSymbolRecord symbol in file.Symbols.Take(4))
                    {
                        CodeGraphSourceSection? section = await TryBuildSourceSectionAsync(request.VesselId, symbol, 1, token).ConfigureAwait(false);
                        if (section != null && file.SourceSections.All(s => s.StartLine != section.StartLine || s.EndLine != section.EndLine))
                        {
                            file.SourceSections.Add(section);
                        }
                    }
                }

                files.Add(file);
            }

            return new CodeGraphExploreResponse
            {
                Status = context.Status,
                Query = request.Query,
                ResolvedSeedSymbols = seeds,
                Files = files.OrderByDescending(f => f.Score).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                Relationships = relationships,
                Warnings = context.Warnings
            };
        }

        private static bool TryMatchGoalPattern(string? pattern, string goal)
        {
            if (String.IsNullOrEmpty(pattern)) return false;
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    goal,
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region Private-Methods

        private async Task<Vessel> ReadVesselOrThrowAsync(string vesselId, CancellationToken token)
        {
            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + vesselId);
            return vessel;
        }

        private async Task<CodeIndexStatus> LoadReadOnlyStatusAsync(string vesselId, CancellationToken token)
        {
            Vessel vessel = await ReadVesselOrThrowAsync(vesselId, token).ConfigureAwait(false);
            CodeIndexStatus status = await ReadPersistedStatusAsync(vessel).ConfigureAwait(false)
                ?? BuildMissingStatus(vessel);
            status.IndexDirectory = GetVesselIndexDirectory(vessel.Id);
            // Deliberately omit TryResolveCurrentCommitAsync: graph queries must not clone or mutate vessel state.
            // Freshness is derived from persisted metadata only; CurrentCommitSha stays null.
            status.Freshness = ResolveFreshness(status);
            ApplyActiveUpdateStatus(status);
            return status;
        }

        private CodeGraphImpactResponse ComputeImpact(
            GraphQueryContext context,
            string symbol,
            CodeGraphTraversalDirectionEnum direction,
            int maxDepth,
            int maxResults)
        {
            List<CodeGraphSymbolRecord> seeds = ResolveSeedSymbols(context, symbol, _DefaultGraphNeighborLimit);

            Dictionary<string, ImpactAccumulator> accumulators = new Dictionary<string, ImpactAccumulator>(StringComparer.OrdinalIgnoreCase);
            Queue<GraphTraversalStep> queue = new Queue<GraphTraversalStep>();
            Dictionary<string, int> minDepthByNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (CodeGraphSymbolRecord seed in seeds)
            {
                string seedKey = BuildSymbolKey(seed);
                if (String.IsNullOrWhiteSpace(seedKey)) continue;
                minDepthByNode[seedKey] = 0;
                queue.Enqueue(new GraphTraversalStep(seed, 0));
            }

            while (queue.Count > 0)
            {
                GraphTraversalStep current = queue.Dequeue();
                if (current.Depth >= maxDepth) continue;

                List<GraphEdgeHop> hops = ResolveTraversalHops(context, current.Symbol, direction);
                foreach (GraphEdgeHop hop in hops)
                {
                    string hopKey = BuildSymbolKey(hop.Symbol);
                    if (String.IsNullOrWhiteSpace(hopKey)) continue;

                    int nextDepth = current.Depth + 1;
                    if (minDepthByNode.TryGetValue(hopKey, out int existingDepth) && existingDepth < nextDepth)
                    {
                        continue;
                    }
                    minDepthByNode[hopKey] = nextDepth;

                    ImpactAccumulator accumulator = GetOrCreateImpactAccumulator(accumulators, hop.Symbol);
                    accumulator.MinDepth = Math.Min(accumulator.MinDepth, nextDepth);
                    accumulator.HitCount++;
                    accumulator.Score = Math.Max(accumulator.Score, ScoreTraversalDepth(nextDepth) + hop.WeightBoost);
                    if (accumulator.Reasons.Count < 3 && !accumulator.Reasons.Contains(hop.Reason, StringComparer.OrdinalIgnoreCase))
                    {
                        accumulator.Reasons.Add(hop.Reason);
                    }

                    queue.Enqueue(new GraphTraversalStep(hop.Symbol, nextDepth));
                }
            }

            List<CodeGraphImpactResult> results = accumulators.Values
                .OrderByDescending(a => a.Score)
                .ThenBy(a => a.MinDepth)
                .ThenBy(a => SelectSymbolName(a.Symbol), StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Symbol.Path, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .Select(a => new CodeGraphImpactResult
                {
                    Score = a.Score,
                    MinDepth = a.MinDepth == Int32.MaxValue ? 0 : a.MinDepth,
                    HitCount = a.HitCount,
                    Symbol = a.Symbol,
                    Reasons = a.Reasons.ToList()
                })
                .ToList();

            return new CodeGraphImpactResponse
            {
                Status = context.Status,
                RequestedSymbol = symbol,
                Direction = direction,
                MaxDepth = maxDepth,
                ResolvedSeedSymbols = seeds,
                Results = results,
                Warnings = context.Warnings
            };
        }

        private CodeIndexStatus BuildMissingStatus(Vessel vessel)
        {
            return new CodeIndexStatus
            {
                VesselId = vessel.Id,
                VesselName = vessel.Name,
                DefaultBranch = vessel.DefaultBranch,
                Freshness = "Missing",
                IndexDirectory = GetVesselIndexDirectory(vessel.Id)
            };
        }

        private string ResolveFreshness(CodeIndexStatus status)
        {
            if (!String.IsNullOrEmpty(status.LastError)) return "Error";
            if (String.IsNullOrEmpty(status.IndexedCommitSha)) return "Missing";
            if (String.IsNullOrEmpty(status.CurrentCommitSha)) return status.Freshness;
            return String.Equals(status.IndexedCommitSha, status.CurrentCommitSha, StringComparison.OrdinalIgnoreCase)
                ? "Fresh"
                : "Stale";
        }

        private static void ApplyActiveUpdateStatus(CodeIndexStatus status)
        {
            if (status == null) return;

            if (_ActiveUpdates.TryGetValue(status.VesselId, out CodeIndexActiveUpdate? active))
            {
                status.UpdateInProgress = true;
                status.UpdateStartedUtc = active.StartedUtc;
                status.UpdateHeartbeatUtc = active.HeartbeatUtc;
                status.UpdateStage = active.Stage;
                status.UpdateProgressDone = active.ProgressDone;
                status.UpdateProgressTotal = active.ProgressTotal;
                status.UpdateProgressPercent = active.ProgressTotal.HasValue && active.ProgressTotal.Value > 0 && active.ProgressDone.HasValue
                    ? Math.Round((double)active.ProgressDone.Value / active.ProgressTotal.Value * 100d, 2)
                    : null;
                status.Freshness = "Updating";
                return;
            }

            status.UpdateInProgress = false;
            status.UpdateStartedUtc = null;
            status.UpdateHeartbeatUtc = null;
            status.UpdateStage = null;
            status.UpdateProgressDone = null;
            status.UpdateProgressTotal = null;
            status.UpdateProgressPercent = null;
        }

        private static string BuildUpdateInProgressMessage(CodeIndexStatus status)
        {
            string vesselName = String.IsNullOrWhiteSpace(status.VesselName) ? status.VesselId : status.VesselName;
            string started = status.UpdateStartedUtc.HasValue ? status.UpdateStartedUtc.Value.ToString("o") : "unknown time";
            string heartbeat = status.UpdateHeartbeatUtc.HasValue ? status.UpdateHeartbeatUtc.Value.ToString("o") : "unknown heartbeat";
            string stage = String.IsNullOrWhiteSpace(status.UpdateStage) ? "unknown stage" : status.UpdateStage!;
            return "Code index update already in progress for vessel " + status.VesselId + " (" + vesselName + ") since " + started + "; stage " + stage + "; heartbeat " + heartbeat + ".";
        }

        private async Task<CodeIndexStatus?> ReadPersistedStatusAsync(Vessel vessel)
        {
            string path = GetStatusPath(vessel.Id);
            if (!File.Exists(path)) return null;

            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            CodeIndexStatus? status = JsonSerializer.Deserialize<CodeIndexStatus>(json, _JsonOptions);
            if (status == null) return null;
            status.VesselId = vessel.Id;
            status.VesselName = vessel.Name;
            status.DefaultBranch = vessel.DefaultBranch;
            return status;
        }

        private async Task WriteStatusAsync(string vesselIndexDirectory, CodeIndexStatus status, CancellationToken token)
        {
            Directory.CreateDirectory(vesselIndexDirectory);
            string statusJson = JsonSerializer.Serialize(status, _JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(vesselIndexDirectory, "metadata.json"), statusJson, token).ConfigureAwait(false);
        }

        private async Task WriteIndexAsync(string vesselIndexDirectory, CodeIndexStatus status, List<CodeIndexRecord> records, CancellationToken token)
        {
            await WriteStatusAsync(vesselIndexDirectory, status, token).ConfigureAwait(false);

            string chunksPath = Path.Combine(vesselIndexDirectory, "chunks.jsonl");
            using (FileStream stream = new FileStream(chunksPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (CodeIndexRecord record in records)
                {
                    string json = JsonSerializer.Serialize(record, _JsonOptions);
                    await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
                }
            }
        }

        private async Task WriteGraphSidecarsAsync(
            string vesselIndexDirectory,
            string vesselId,
            string commitSha,
            string tempDirectory,
            List<CodeIndexRecord> records,
            CancellationToken token)
        {
            List<CodeGraphSymbolRecord> allSymbols = new List<CodeGraphSymbolRecord>();
            List<CodeGraphEdgeRecord> allEdges = new List<CodeGraphEdgeRecord>();

            // Process each unique graph-supported source file represented in the chunk records.
            HashSet<string> processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (CodeIndexRecord record in records)
            {
                if (!PolyglotSymbolExtractor.SupportsLanguage(record.Language)) continue;
                if (!processedPaths.Add(record.Path)) continue;

                string absolutePath = Path.Combine(tempDirectory, record.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolutePath)) continue;

                string source;
                try
                {
                    source = File.ReadAllText(absolutePath, System.Text.Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "graph extraction: failed to read " + record.Path + ": " + ex.Message);
                    continue;
                }

                try
                {
                    List<CodeGraphSymbolRecord> fileSymbols;
                    List<CodeGraphEdgeRecord> fileEdges;
                    _SymbolExtractor.Extract(vesselId, commitSha, record.Path, record.ContentHash, record.Language, source, out fileSymbols, out fileEdges);
                    allSymbols.AddRange(fileSymbols);
                    allEdges.AddRange(fileEdges);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "graph extraction: failed to extract symbols from " + record.Path + ": " + ex.Message);
                }
            }

            await WriteJsonlFileAsync(Path.Combine(vesselIndexDirectory, "symbols.jsonl"), allSymbols, token).ConfigureAwait(false);
            await WriteJsonlFileAsync(Path.Combine(vesselIndexDirectory, "edges.jsonl"), allEdges, token).ConfigureAwait(false);

            _Logging.Info(_Header + "graph sidecars written: " + allSymbols.Count + " symbols, " + allEdges.Count + " edges");
        }

        private async Task WriteJsonlFileAsync<T>(string path, List<T> items, CancellationToken token)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                foreach (T item in items)
                {
                    string json = JsonSerializer.Serialize(item, _JsonOptions);
                    await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
                }
            }
        }

        private async Task<List<CodeIndexRecord>> ReadRecordsAsync(string vesselId, string freshness, CancellationToken token)
        {
            string chunksPath = GetChunksPath(vesselId);
            if (!File.Exists(chunksPath)) return new List<CodeIndexRecord>();

            List<CodeIndexRecord> records = new List<CodeIndexRecord>();
            using (FileStream stream = new FileStream(chunksPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    CodeIndexRecord? record = JsonSerializer.Deserialize<CodeIndexRecord>(line, _JsonOptions);
                    if (record == null) continue;
                    record.Freshness = freshness;
                    records.Add(record);
                }
            }

            return records;
        }

        private async Task WriteSignaturesAsync(string vesselId, List<FileSignatureRecord> signatures, CancellationToken token)
        {
            string signaturesPath = GetSignaturesPath(vesselId);
            string? directory = Path.GetDirectoryName(signaturesPath);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new FileStream(signaturesPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (FileSignatureRecord signature in signatures)
                {
                    string json = JsonSerializer.Serialize(signature, _JsonOptions);
                    await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
                }
            }
        }

        private async Task<List<FileSignatureRecord>> ReadSignaturesAsync(string vesselId, CancellationToken token)
        {
            string signaturesPath = GetSignaturesPath(vesselId);
            if (!File.Exists(signaturesPath)) return new List<FileSignatureRecord>();

            List<FileSignatureRecord> signatures = new List<FileSignatureRecord>();
            using (FileStream stream = new FileStream(signaturesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    FileSignatureRecord? record = JsonSerializer.Deserialize<FileSignatureRecord>(line, _JsonOptions);
                    if (record == null) continue;
                    signatures.Add(record);
                }
            }

            return signatures;
        }

        private async Task<CodeGraphNeighborsResponse> GetNeighborsAsync(
            CodeGraphNeighborsRequest request,
            bool includeCallers,
            bool includeCallees,
            CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));
            if (String.IsNullOrWhiteSpace(request.Symbol)) throw new ArgumentNullException(nameof(request.Symbol));

            GraphQueryContext context = await LoadGraphQueryContextAsync(request.VesselId, token).ConfigureAwait(false);
            int limit = ClampGraphLimit(request.Limit, _DefaultGraphNeighborLimit, _MaxGraphResults);
            List<CodeGraphSymbolRecord> seeds = ResolveSeedSymbols(context, request.Symbol, _DefaultGraphNeighborLimit);

            Dictionary<string, CodeGraphNeighborResult> resultsByKey = new Dictionary<string, CodeGraphNeighborResult>(StringComparer.OrdinalIgnoreCase);

            foreach (CodeGraphSymbolRecord seed in seeds)
            {
                if (includeCallers)
                {
                    foreach (CodeGraphNeighborResult caller in ResolveDirectNeighbors(context, seed, includeCallers: true))
                    {
                        string key = BuildSymbolKey(caller.Symbol);
                        if (String.IsNullOrWhiteSpace(key)) continue;
                        if (!resultsByKey.TryGetValue(key, out CodeGraphNeighborResult? existing))
                        {
                            resultsByKey[key] = caller;
                            continue;
                        }

                        if (caller.Score > existing.Score)
                        {
                            resultsByKey[key] = caller;
                        }
                    }
                }

                if (includeCallees)
                {
                    foreach (CodeGraphNeighborResult callee in ResolveDirectNeighbors(context, seed, includeCallers: false))
                    {
                        string key = BuildSymbolKey(callee.Symbol);
                        if (String.IsNullOrWhiteSpace(key)) continue;
                        if (!resultsByKey.TryGetValue(key, out CodeGraphNeighborResult? existing))
                        {
                            resultsByKey[key] = callee;
                            continue;
                        }

                        if (callee.Score > existing.Score)
                        {
                            resultsByKey[key] = callee;
                        }
                    }
                }
            }

            List<CodeGraphNeighborResult> ordered = resultsByKey.Values
                .OrderByDescending(r => r.Score)
                .ThenBy(r => SelectSymbolName(r.Symbol), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Symbol.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Symbol.StartLine)
                .Take(limit)
                .ToList();

            return new CodeGraphNeighborsResponse
            {
                Status = context.Status,
                RequestedSymbol = request.Symbol,
                ResolvedSeedSymbols = seeds,
                Results = ordered,
                Warnings = context.Warnings
            };
        }

        private async Task<GraphQueryContext> LoadGraphQueryContextAsync(string vesselId, CancellationToken token)
        {
            CodeIndexStatus status = await LoadReadOnlyStatusAsync(vesselId, token).ConfigureAwait(false);
            List<string> warnings = new List<string>();
            if (!String.Equals(status.Freshness, "Fresh", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("index freshness is " + status.Freshness);
            }

            string vesselDirectory = GetVesselIndexDirectory(vesselId);
            string symbolsPath = Path.Combine(vesselDirectory, "symbols.jsonl");
            string edgesPath = Path.Combine(vesselDirectory, "edges.jsonl");

            if (!File.Exists(symbolsPath)) warnings.Add("graph symbols sidecar missing: symbols.jsonl");
            if (!File.Exists(edgesPath)) warnings.Add("graph edges sidecar missing: edges.jsonl");

            List<CodeGraphSymbolRecord> symbols = await ReadJsonlRecordsAsync<CodeGraphSymbolRecord>(symbolsPath, token).ConfigureAwait(false);
            List<CodeGraphEdgeRecord> edges = await ReadJsonlRecordsAsync<CodeGraphEdgeRecord>(edgesPath, token).ConfigureAwait(false);

            symbols = symbols
                .Where(s => String.IsNullOrWhiteSpace(s.VesselId) || String.Equals(s.VesselId, vesselId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            edges = edges
                .Where(e => String.IsNullOrWhiteSpace(e.VesselId) || String.Equals(e.VesselId, vesselId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (symbols.Count == 0) warnings.Add("graph symbols sidecar is empty");
            if (edges.Count == 0) warnings.Add("graph edges sidecar is empty");

            if (!String.IsNullOrWhiteSpace(status.IndexedCommitSha))
            {
                bool mismatchedSymbolCommit = symbols.Any(s =>
                    !String.IsNullOrWhiteSpace(s.CommitSha)
                    && !String.Equals(s.CommitSha, status.IndexedCommitSha, StringComparison.OrdinalIgnoreCase));
                if (mismatchedSymbolCommit)
                {
                    warnings.Add("graph symbols sidecar commit does not match metadata indexed commit");
                }

                bool mismatchedEdgeCommit = edges.Any(e =>
                    !String.IsNullOrWhiteSpace(e.CommitSha)
                    && !String.Equals(e.CommitSha, status.IndexedCommitSha, StringComparison.OrdinalIgnoreCase));
                if (mismatchedEdgeCommit)
                {
                    warnings.Add("graph edges sidecar commit does not match metadata indexed commit");
                }
            }

            Dictionary<string, List<CodeGraphSymbolRecord>> symbolLookup = new Dictionary<string, List<CodeGraphSymbolRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (CodeGraphSymbolRecord symbol in symbols)
            {
                AddSymbolLookup(symbolLookup, symbol.QualifiedName, symbol);
                AddSymbolLookup(symbolLookup, symbol.SimpleName, symbol);
            }

            return new GraphQueryContext(status, symbols, edges, symbolLookup, warnings);
        }

        private async Task<List<T>> ReadJsonlRecordsAsync<T>(string path, CancellationToken token)
        {
            if (!File.Exists(path)) return new List<T>();

            List<T> records = new List<T>();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    T? record = JsonSerializer.Deserialize<T>(line, _JsonOptions);
                    if (record == null) continue;
                    records.Add(record);
                }
            }

            return records;
        }

        private static void AddSymbolLookup(Dictionary<string, List<CodeGraphSymbolRecord>> lookup, string key, CodeGraphSymbolRecord symbol)
        {
            if (String.IsNullOrWhiteSpace(key)) return;
            if (!lookup.TryGetValue(key, out List<CodeGraphSymbolRecord>? bucket))
            {
                bucket = new List<CodeGraphSymbolRecord>();
                lookup[key] = bucket;
            }
            bucket.Add(symbol);
        }

        private static List<CodeGraphSymbolRecord> ResolveSeedSymbols(GraphQueryContext context, string symbolQuery, int maxSeeds)
        {
            if (String.IsNullOrWhiteSpace(symbolQuery)) return new List<CodeGraphSymbolRecord>();
            int seedLimit = ClampGraphLimit(maxSeeds, _DefaultGraphNeighborLimit, _MaxGraphResults);
            string query = symbolQuery.Trim();

            List<(CodeGraphSymbolRecord Symbol, double Score)> scored = new List<(CodeGraphSymbolRecord Symbol, double Score)>();
            foreach (CodeGraphSymbolRecord symbol in context.Symbols)
            {
                ScoredSymbolMatch match = ScoreSymbolMatch(symbol, query);
                if (match.Score <= 0) continue;
                scored.Add((symbol, match.Score));
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => SelectSymbolName(s.Symbol), StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Symbol.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Symbol.StartLine)
                .Take(seedLimit)
                .Select(s => s.Symbol)
                .ToList();
        }

        private static ScoredSymbolMatch ScoreSymbolMatch(CodeGraphSymbolRecord symbol, string query)
        {
            string qualified = symbol.QualifiedName ?? "";
            string simple = symbol.SimpleName ?? "";
            if (String.IsNullOrWhiteSpace(query)) return new ScoredSymbolMatch(0, "");

            if (!String.IsNullOrWhiteSpace(qualified) && String.Equals(qualified, query, StringComparison.OrdinalIgnoreCase))
                return new ScoredSymbolMatch(120, "exact qualified symbol match");
            if (!String.IsNullOrWhiteSpace(simple) && String.Equals(simple, query, StringComparison.OrdinalIgnoreCase))
                return new ScoredSymbolMatch(110, "exact simple symbol match");
            if (!String.IsNullOrWhiteSpace(qualified) && qualified.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return new ScoredSymbolMatch(95, "qualified symbol prefix match");
            if (!String.IsNullOrWhiteSpace(simple) && simple.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return new ScoredSymbolMatch(85, "simple symbol prefix match");
            if (!String.IsNullOrWhiteSpace(qualified) && qualified.Contains(query, StringComparison.OrdinalIgnoreCase))
                return new ScoredSymbolMatch(70, "qualified symbol contains query");
            if (!String.IsNullOrWhiteSpace(simple) && simple.Contains(query, StringComparison.OrdinalIgnoreCase))
                return new ScoredSymbolMatch(60, "simple symbol contains query");
            if (!String.IsNullOrWhiteSpace(symbol.Path) && symbol.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                return new ScoredSymbolMatch(40, "path contains query");

            return new ScoredSymbolMatch(0, "");
        }

        private static List<CodeGraphNeighborResult> ResolveDirectNeighbors(GraphQueryContext context, CodeGraphSymbolRecord seed, bool includeCallers)
        {
            List<CodeGraphNeighborResult> results = new List<CodeGraphNeighborResult>();
            foreach (CodeGraphEdgeRecord edge in context.Edges)
            {
                if (edge.Kind != CodeGraphEdgeKindEnum.Calls) continue;
                if (includeCallers)
                {
                    if (!SymbolMatchesEndpoint(seed, edge.TargetSymbol)) continue;
                    foreach (CodeGraphSymbolRecord source in ResolveSymbolsForEndpoint(context, edge.SourceSymbol, edge.SourcePath))
                    {
                        results.Add(new CodeGraphNeighborResult
                        {
                            Score = 95,
                            EdgeKind = edge.Kind,
                            TraversalDepth = 1,
                            Symbol = source,
                            Reason = "direct caller"
                        });
                    }
                }
                else
                {
                    if (!SymbolMatchesEndpoint(seed, edge.SourceSymbol)) continue;
                    foreach (CodeGraphSymbolRecord target in ResolveSymbolsForEndpoint(context, edge.TargetSymbol, ""))
                    {
                        results.Add(new CodeGraphNeighborResult
                        {
                            Score = 95,
                            EdgeKind = edge.Kind,
                            TraversalDepth = 1,
                            Symbol = target,
                            Reason = "direct callee"
                        });
                    }
                }
            }

            return results;
        }

        private static List<GraphEdgeHop> ResolveTraversalHops(
            GraphQueryContext context,
            CodeGraphSymbolRecord symbol,
            CodeGraphTraversalDirectionEnum direction)
        {
            List<GraphEdgeHop> hops = new List<GraphEdgeHop>();
            foreach (CodeGraphEdgeRecord edge in context.Edges)
            {
                if (edge.Kind != CodeGraphEdgeKindEnum.Calls) continue;

                if ((direction == CodeGraphTraversalDirectionEnum.Callees || direction == CodeGraphTraversalDirectionEnum.Both)
                    && SymbolMatchesEndpoint(symbol, edge.SourceSymbol))
                {
                    foreach (CodeGraphSymbolRecord target in ResolveSymbolsForEndpoint(context, edge.TargetSymbol, ""))
                    {
                        hops.Add(new GraphEdgeHop(
                            target,
                            "callee traversal",
                            0));
                    }
                }

                if ((direction == CodeGraphTraversalDirectionEnum.Callers || direction == CodeGraphTraversalDirectionEnum.Both)
                    && SymbolMatchesEndpoint(symbol, edge.TargetSymbol))
                {
                    foreach (CodeGraphSymbolRecord source in ResolveSymbolsForEndpoint(context, edge.SourceSymbol, edge.SourcePath))
                    {
                        hops.Add(new GraphEdgeHop(
                            source,
                            "caller traversal",
                            0));
                    }
                }
            }

            return hops;
        }

        private static bool SymbolMatchesEndpoint(CodeGraphSymbolRecord symbol, string endpoint)
        {
            if (String.IsNullOrWhiteSpace(endpoint)) return false;
            string normalizedEndpoint = NormalizeGraphEndpoint(endpoint);
            if (!String.IsNullOrWhiteSpace(symbol.QualifiedName)
                && (String.Equals(symbol.QualifiedName, normalizedEndpoint, StringComparison.OrdinalIgnoreCase)
                    || symbol.QualifiedName.EndsWith("." + normalizedEndpoint, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!String.IsNullOrWhiteSpace(symbol.SimpleName)
                && String.Equals(symbol.SimpleName, normalizedEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static List<CodeGraphSymbolRecord> ResolveSymbolsForEndpoint(GraphQueryContext context, string endpoint, string fallbackPath)
        {
            string normalizedEndpoint = NormalizeGraphEndpoint(endpoint);
            if (!String.IsNullOrWhiteSpace(normalizedEndpoint)
                && context.SymbolLookup.TryGetValue(normalizedEndpoint, out List<CodeGraphSymbolRecord>? matches)
                && matches.Count > 0)
            {
                return matches;
            }

            if (!String.IsNullOrWhiteSpace(normalizedEndpoint))
            {
                List<CodeGraphSymbolRecord> suffixMatches = context.Symbols
                    .Where(s =>
                        (!String.IsNullOrWhiteSpace(s.QualifiedName)
                            && s.QualifiedName.EndsWith("." + normalizedEndpoint, StringComparison.OrdinalIgnoreCase))
                        || (!String.IsNullOrWhiteSpace(s.SimpleName)
                            && String.Equals(s.SimpleName, normalizedEndpoint, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(s => !String.IsNullOrWhiteSpace(fallbackPath) && String.Equals(s.Path, fallbackPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.StartLine)
                    .Take(_DefaultGraphNeighborLimit)
                    .ToList();
                if (suffixMatches.Count > 0) return suffixMatches;
            }

            if (!String.IsNullOrWhiteSpace(fallbackPath))
            {
                List<CodeGraphSymbolRecord> pathMatches = context.Symbols
                    .Where(s => String.Equals(s.Path, fallbackPath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.StartLine)
                    .Take(1)
                    .ToList();
                if (pathMatches.Count > 0) return pathMatches;
            }

            return new List<CodeGraphSymbolRecord>
            {
                new CodeGraphSymbolRecord
                {
                    Path = fallbackPath ?? "",
                    SimpleName = normalizedEndpoint,
                    QualifiedName = normalizedEndpoint,
                    Kind = CodeGraphSymbolKindEnum.Unknown,
                    StartLine = 1,
                    EndLine = 1
                }
            };
        }

        private static string NormalizeGraphEndpoint(string? endpoint)
        {
            string value = (endpoint ?? "").Trim();
            if (String.IsNullOrWhiteSpace(value)) return "";
            int parenIndex = value.IndexOf('(');
            if (parenIndex > 0) value = value.Substring(0, parenIndex).Trim();
            int genericIndex = value.IndexOf('<');
            if (genericIndex > 0) value = value.Substring(0, genericIndex).Trim();
            if (value.Contains('.'))
            {
                string last = value.Substring(value.LastIndexOf('.') + 1).Trim();
                if (!String.IsNullOrWhiteSpace(last)) return last;
            }
            return value;
        }

        private static bool EdgeEndpointsIncluded(GraphQueryContext context, CodeGraphEdgeRecord edge, HashSet<string> includedKeys)
        {
            if (edge == null || includedKeys == null || includedKeys.Count == 0) return false;

            bool sourceIncluded = ResolveSymbolsForEndpoint(context, edge.SourceSymbol, edge.SourcePath)
                .Any(s => includedKeys.Contains(BuildSymbolKey(s)));
            if (!sourceIncluded) return false;

            bool targetIncluded = ResolveSymbolsForEndpoint(context, edge.TargetSymbol, "")
                .Any(s => includedKeys.Contains(BuildSymbolKey(s)));
            return targetIncluded;
        }

        private static string BuildSymbolKey(CodeGraphSymbolRecord symbol)
        {
            if (!String.IsNullOrWhiteSpace(symbol.QualifiedName)) return symbol.QualifiedName.Trim();
            if (!String.IsNullOrWhiteSpace(symbol.SimpleName)) return symbol.SimpleName.Trim();
            if (!String.IsNullOrWhiteSpace(symbol.Path)) return symbol.Path.Trim() + ":" + symbol.StartLine;
            return "";
        }

        private static string SelectSymbolName(CodeGraphSymbolRecord symbol)
        {
            if (!String.IsNullOrWhiteSpace(symbol.QualifiedName)) return symbol.QualifiedName;
            if (!String.IsNullOrWhiteSpace(symbol.SimpleName)) return symbol.SimpleName;
            return "";
        }

        private static ImpactAccumulator GetOrCreateImpactAccumulator(Dictionary<string, ImpactAccumulator> accumulators, CodeGraphSymbolRecord symbol)
        {
            string key = BuildSymbolKey(symbol);
            if (!accumulators.TryGetValue(key, out ImpactAccumulator? accumulator))
            {
                accumulator = new ImpactAccumulator(symbol);
                accumulators[key] = accumulator;
            }
            return accumulator;
        }

        private static int ClampGraphDepth(int requested, int defaultValue)
        {
            int depth = requested > 0 ? requested : defaultValue;
            if (depth < 1) depth = 1;
            if (depth > _MaxGraphDepth) depth = _MaxGraphDepth;
            return depth;
        }

        private static int ClampGraphLimit(int requested, int defaultValue, int maxValue)
        {
            int limit = requested > 0 ? requested : defaultValue;
            if (limit < 1) limit = 1;
            if (limit > maxValue) limit = maxValue;
            return limit;
        }

        private static double ScoreTraversalDepth(int depth)
        {
            return Math.Max(1, 100 - (depth * 12));
        }

        private static void ClassifyTestSignal(CodeGraphSymbolRecord symbol, out bool explicitSignal, out bool conventionSignal, out string reason)
        {
            explicitSignal = false;
            conventionSignal = false;
            reason = "";

            string path = symbol.Path ?? "";
            string simple = symbol.SimpleName ?? "";
            string qualified = symbol.QualifiedName ?? "";

            if (LooksLikeExplicitTestPath(path))
            {
                explicitSignal = true;
                reason = "explicit test file path signal";
                return;
            }

            if (simple.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
                || simple.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                || qualified.Contains(".Tests.", StringComparison.OrdinalIgnoreCase))
            {
                explicitSignal = true;
                reason = "explicit test symbol naming signal";
                return;
            }

            if (LooksLikeTestPath(path))
            {
                conventionSignal = true;
                reason = "test path naming convention";
            }
        }

        private static bool LooksLikeExplicitTestPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string normalized = path.Replace('\\', '/');
            if (normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool LooksLikeTestPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string normalized = path.Replace('\\', '/');
            if (normalized.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.EndsWith("Spec.cs", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.Contains("/integration/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static AffectedTestAccumulator GetOrCreateAffectedTestAccumulator(Dictionary<string, AffectedTestAccumulator> accumulators, string path)
        {
            string key = String.IsNullOrWhiteSpace(path) ? "<unknown>" : path;
            if (!accumulators.TryGetValue(key, out AffectedTestAccumulator? accumulator))
            {
                accumulator = new AffectedTestAccumulator(path);
                accumulators[key] = accumulator;
            }
            return accumulator;
        }

        private static void AddReason(List<string> reasons, string reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) return;
            if (reasons.Contains(reason, StringComparer.OrdinalIgnoreCase)) return;
            reasons.Add(reason);
        }

        private async Task<string> ResolveRepositoryPathAsync(Vessel vessel, CancellationToken token)
        {
            string repoPath = vessel.LocalPath ?? Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
            if (Directory.Exists(repoPath) && await _Git.IsRepositoryAsync(repoPath, token).ConfigureAwait(false))
            {
                return repoPath;
            }

            if (!String.IsNullOrWhiteSpace(vessel.WorkingDirectory) &&
                Directory.Exists(vessel.WorkingDirectory) &&
                await _Git.IsRepositoryAsync(vessel.WorkingDirectory, token).ConfigureAwait(false))
            {
                return vessel.WorkingDirectory;
            }

            if (String.IsNullOrWhiteSpace(vessel.RepoUrl))
            {
                throw new InvalidOperationException("Vessel " + vessel.Id + " has no usable repository path or repo URL.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(repoPath)!);
            await _Git.CloneBareAsync(vessel.RepoUrl!, repoPath, token).ConfigureAwait(false);
            vessel.LocalPath = repoPath;
            await _Database.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);
            return repoPath;
        }

        private async Task<string?> TryResolveCurrentCommitAsync(Vessel vessel, CancellationToken token)
        {
            try
            {
                string repoPath = await ResolveRepositoryPathAsync(vessel, token).ConfigureAwait(false);
                return await ResolveDefaultBranchCommitAsync(repoPath, vessel.DefaultBranch, token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> ResolveDefaultBranchCommitAsync(string repoPath, string defaultBranch, CancellationToken token)
        {
            List<string> refs = new List<string>
            {
                "refs/remotes/origin/" + defaultBranch,
                "origin/" + defaultBranch,
                "refs/heads/" + defaultBranch,
                defaultBranch,
                "HEAD"
            };

            foreach (string gitRef in refs)
            {
                try
                {
                    string output = await RunGitAsync(repoPath, token, "rev-parse", "--verify", gitRef).ConfigureAwait(false);
                    string sha = output.Trim();
                    if (!String.IsNullOrEmpty(sha)) return sha;
                }
                catch
                {
                }
            }

            throw new InvalidOperationException("Unable to resolve default branch " + defaultBranch + " in " + repoPath);
        }

        private async Task ExtractCommitArchiveAsync(string repoPath, string commitSha, string destinationDirectory, CancellationToken token)
        {
            string archivePath = Path.Combine(Path.GetTempPath(), "armada-code-index-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await RunGitAsync(repoPath, token, "archive", "--format=zip", "-o", archivePath, commitSha).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(archivePath, destinationDirectory);
            }
            finally
            {
                try
                {
                    if (File.Exists(archivePath)) File.Delete(archivePath);
                }
                catch
                {
                }
            }
        }

        private List<CodeIndexRecord> BuildRecordsFromDirectory(
            Vessel vessel,
            string commitSha,
            string rootDirectory,
            HashSet<string>? includeOnlyPaths = null)
        {
            List<CodeIndexRecord> records = new List<CodeIndexRecord>();
            string[] files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                string relativePath = NormalizeRepoPath(Path.GetRelativePath(rootDirectory, file));
                if (includeOnlyPaths != null && !includeOnlyPaths.Contains(relativePath)) continue;
                if (!ShouldIndexPath(relativePath, file)) continue;

                string content;
                try
                {
                    content = File.ReadAllText(file, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                if (content.IndexOf('\0') >= 0) continue;

                string contentHash = ComputeSha256(content);
                string language = DetectLanguage(relativePath);
                bool isReferenceOnly = IsReferenceOnlyPath(relativePath);
                string[] lines = NormalizeLineEndings(content).Split('\n');

                for (int start = 0; start < lines.Length; start += _Settings.CodeIndex.MaxChunkLines)
                {
                    int endExclusive = Math.Min(lines.Length, start + _Settings.CodeIndex.MaxChunkLines);
                    string chunk = String.Join("\n", lines.Skip(start).Take(endExclusive - start)).Trim();
                    if (String.IsNullOrWhiteSpace(chunk)) continue;

                    records.Add(new CodeIndexRecord
                    {
                        VesselId = vessel.Id,
                        Path = relativePath,
                        CommitSha = commitSha,
                        ContentHash = contentHash,
                        Language = language,
                        StartLine = start + 1,
                        EndLine = endExclusive,
                        Freshness = "Fresh",
                        IndexedAtUtc = DateTime.UtcNow,
                        IsReferenceOnly = isReferenceOnly,
                        Content = chunk
                    });
                }
            }

            return records;
        }

        private async Task PopulateEmbeddingsAsync(
            string vesselId,
            List<CodeIndexRecord> records,
            List<CodeIndexRecord> previousRecords,
            CancellationToken token)
        {
            if (_EmbeddingClient == null) return;

            Dictionary<string, float[]> reusableVectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (CodeIndexRecord previous in previousRecords)
            {
                if (previous.EmbeddingVector == null || previous.EmbeddingVector.Length == 0) continue;
                string chunkHash = ComputeSha256(previous.Content ?? "");
                if (!reusableVectors.ContainsKey(chunkHash))
                {
                    reusableVectors[chunkHash] = previous.EmbeddingVector.ToArray();
                }
            }

            List<CodeIndexRecord> missing = new List<CodeIndexRecord>();
            foreach (CodeIndexRecord record in records)
            {
                if (record.EmbeddingVector != null && record.EmbeddingVector.Length > 0) continue;

                string chunkHash = ComputeSha256(record.Content ?? "");
                if (reusableVectors.TryGetValue(chunkHash, out float[]? vector))
                {
                    record.EmbeddingVector = vector.ToArray();
                    continue;
                }

                missing.Add(record);
            }

            int batchSize = Math.Max(1, _Settings.CodeIndex.EmbeddingBatchSize);
            int embedded = 0;
            for (int start = 0; start < missing.Count; start += batchSize)
            {
                List<CodeIndexRecord> batch = missing.Skip(start).Take(batchSize).ToList();
                List<string> inputs = batch.Select(r => r.Content ?? "").ToList();
                IReadOnlyList<float[]> vectors;
                try
                {
                    vectors = await _EmbeddingClient.EmbedBatchAsync(inputs, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "embedding batch failed: " + ex.Message);
                    vectors = Array.Empty<float[]>();
                }

                if (vectors.Count == batch.Count)
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        float[] vector = vectors[i];
                        if (vector != null && vector.Length > 0)
                        {
                            batch[i].EmbeddingVector = vector;
                        }
                    }

                    embedded += batch.Count;
                    LogEmbeddingProgress(embedded, missing.Count, vesselId, "Embedded");
                    continue;
                }

                for (int i = 0; i < batch.Count; i++)
                {
                    CodeIndexRecord record = batch[i];
                    try
                    {
                        float[] vector = await _EmbeddingClient.EmbedAsync(record.Content ?? "", token).ConfigureAwait(false);
                        if (vector != null && vector.Length > 0)
                            record.EmbeddingVector = vector;
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "embedding failed for chunk " + record.Path + ": " + ex.Message);
                    }
                    finally
                    {
                        embedded++;
                        LogEmbeddingProgress(embedded, missing.Count, vesselId, "Embedded");
                    }
                }
            }
        }

        private void LogEmbeddingProgress(int done, int total, string vesselId, string label)
        {
            if (total <= 0) return;
            SetActiveUpdateProgress(vesselId, label, done, total);
            int interval = Math.Max(1, _Settings.CodeIndex.EmbeddingProgressLogInterval);
            if (done < total && done % interval != 0) return;
            _Logging.Info(_Header + label + " " + done + "/" + total + " chunks for vessel " + vesselId);
        }

        private static void SetActiveUpdateProgress(string vesselId, string stage, int? done, int? total)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) return;
            if (!_ActiveUpdates.TryGetValue(vesselId, out CodeIndexActiveUpdate? active)) return;

            active.HeartbeatUtc = DateTime.UtcNow;
            active.Stage = stage;
            active.ProgressDone = done;
            active.ProgressTotal = total;
        }

        private bool CanReusePersistedIndex(
            CodeIndexStatus? previousStatus,
            string commitSha,
            string indexSettingsFingerprint,
            string embeddingSettingsFingerprint)
        {
            if (previousStatus == null) return false;
            if (String.IsNullOrWhiteSpace(previousStatus.IndexedCommitSha)) return false;
            if (!String.Equals(previousStatus.IndexedCommitSha, commitSha, StringComparison.OrdinalIgnoreCase)) return false;
            if (!String.Equals(previousStatus.IndexSettingsFingerprint, indexSettingsFingerprint, StringComparison.Ordinal)) return false;
            if (!String.Equals(previousStatus.EmbeddingSettingsFingerprint, embeddingSettingsFingerprint, StringComparison.Ordinal)) return false;
            return File.Exists(GetChunksPath(previousStatus.VesselId));
        }

        private bool CanReuseFileRecords(
            CodeIndexStatus? previousStatus,
            string indexSettingsFingerprint,
            List<CodeIndexRecord> previousRecords)
        {
            return previousStatus != null
                && !String.IsNullOrWhiteSpace(previousStatus.IndexedCommitSha)
                && previousRecords.Count > 0
                && String.Equals(previousStatus.IndexSettingsFingerprint, indexSettingsFingerprint, StringComparison.Ordinal);
        }

        private bool CanReuseEmbeddings(
            CodeIndexStatus? previousStatus,
            string embeddingSettingsFingerprint,
            List<CodeIndexRecord> previousRecords)
        {
            return previousStatus != null
                && _Settings.CodeIndex.UseSemanticSearch
                && previousRecords.Any(r => r.EmbeddingVector != null && r.EmbeddingVector.Length > 0)
                && String.Equals(previousStatus.EmbeddingSettingsFingerprint, embeddingSettingsFingerprint, StringComparison.Ordinal);
        }

        private static CodeIndexRecord CloneRecordForCommit(CodeIndexRecord source, string commitSha, DateTime indexedAtUtc)
        {
            return new CodeIndexRecord
            {
                VesselId = source.VesselId,
                Path = source.Path,
                CommitSha = commitSha,
                ContentHash = source.ContentHash,
                Language = source.Language,
                StartLine = source.StartLine,
                EndLine = source.EndLine,
                Freshness = "Fresh",
                IndexedAtUtc = indexedAtUtc,
                IsReferenceOnly = source.IsReferenceOnly,
                Content = source.Content,
                EmbeddingVector = source.EmbeddingVector == null ? null : source.EmbeddingVector.ToArray()
            };
        }

        private async Task<HashSet<string>?> TryGetChangedPathsAsync(
            string repoPath,
            string fromCommit,
            string toCommit,
            CancellationToken token)
        {
            try
            {
                string output = await RunGitAsync(repoPath, token, "diff", "--name-status", "-M", fromCommit, toCommit).ConfigureAwait(false);
                HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in output.Split('\n'))
                {
                    string line = raw.Trim();
                    if (String.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    if (parts[0].StartsWith("R", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                    {
                        paths.Add(NormalizeRepoPath(parts[1]));
                        paths.Add(NormalizeRepoPath(parts[2]));
                    }
                    else if (parts.Length >= 2)
                    {
                        paths.Add(NormalizeRepoPath(parts[1]));
                    }
                }

                return paths;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not compute incremental changed paths: " + ex.Message);
                return null;
            }
        }

        private bool ShouldIndexPath(string relativePath, string absolutePath)
        {
            string fileName = Path.GetFileName(relativePath);
            string extension = Path.GetExtension(relativePath);
            string normalized = "/" + relativePath.Replace('\\', '/').Trim('/') + "/";

            if (IsSecretOrCredentialPath(fileName, extension, normalized)) return false;

            bool referenceOnly = IsReferenceOnlyPath(relativePath);
            if (!referenceOnly)
            {
                string[] segments = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string segment in segments)
                {
                    if (ContainsOrdinalIgnoreCase(_Settings.CodeIndex.ExcludedDirectoryNames, segment)) return false;
                }

                foreach (string fragment in _Settings.CodeIndex.ExcludedPathFragments)
                {
                    if (!String.IsNullOrWhiteSpace(fragment) && normalized.Contains(fragment.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            if (ContainsOrdinalIgnoreCase(_Settings.CodeIndex.ExcludedFileNames, fileName)) return false;
            if (!String.IsNullOrEmpty(extension) && ContainsOrdinalIgnoreCase(_Settings.CodeIndex.ExcludedExtensions, extension)) return false;

            FileInfo info = new FileInfo(absolutePath);
            return info.Length <= _Settings.CodeIndex.MaxFileBytes;
        }

        private bool IsSecretOrCredentialPath(string fileName, string extension, string normalizedPath)
        {
            if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return true;
            if (fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) && !IsSourceExtension(extension)) return true;
            if (fileName.Contains("password", StringComparison.OrdinalIgnoreCase) && !IsSourceExtension(extension)) return true;
            if (fileName.Contains("token", StringComparison.OrdinalIgnoreCase) && !IsSourceExtension(extension)) return true;
            if (fileName.Contains("credential", StringComparison.OrdinalIgnoreCase) && !IsSourceExtension(extension)) return true;

            string[] blockedFragments =
            {
                "/.secrets/",
                "/secrets/",
                "/secret/",
                "/credentials/",
                "/private-keys/"
            };

            foreach (string fragment in blockedFragments)
            {
                if (normalizedPath.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private bool IsSourceExtension(string extension)
        {
            string ext = extension ?? "";
            return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".fs", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".py", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".java", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".kt", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".rs", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".go", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsReferenceOnlyPath(string relativePath)
        {
            string normalized = "/" + relativePath.Replace('\\', '/').Trim('/') + "/";
            foreach (string fragment in _Settings.CodeIndex.ReferenceOnlyPathFragments)
            {
                if (!String.IsNullOrWhiteSpace(fragment) &&
                    normalized.Contains(fragment.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsOrdinalIgnoreCase(List<string> values, string candidate)
        {
            foreach (string value in values)
            {
                if (String.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string NormalizeRepoPath(string path)
        {
            return (path ?? "").Replace('\\', '/').TrimStart('/');
        }

        private static string NormalizeLineEndings(string content)
        {
            return (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string ComputeSha256(string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content ?? "");
            byte[] hash = SHA256.HashData(bytes);
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }

        private string BuildIndexSettingsFingerprint()
        {
            object payload = new
            {
                _Settings.CodeIndex.MaxFileBytes,
                _Settings.CodeIndex.MaxChunkLines,
                ExcludedDirectoryNames = NormalizeList(_Settings.CodeIndex.ExcludedDirectoryNames),
                ExcludedFileNames = NormalizeList(_Settings.CodeIndex.ExcludedFileNames),
                ExcludedExtensions = NormalizeList(_Settings.CodeIndex.ExcludedExtensions),
                ExcludedPathFragments = NormalizeList(_Settings.CodeIndex.ExcludedPathFragments),
                ReferenceOnlyPathFragments = NormalizeList(_Settings.CodeIndex.ReferenceOnlyPathFragments)
            };
            return ComputeSha256(JsonSerializer.Serialize(payload, _JsonOptions));
        }

        private string BuildEmbeddingSettingsFingerprint()
        {
            object payload = new
            {
                _Settings.CodeIndex.UseSemanticSearch,
                Model = _Settings.CodeIndex.UseSemanticSearch ? _Settings.CodeIndex.EmbeddingModel : "",
                BaseUrl = _Settings.CodeIndex.UseSemanticSearch ? _Settings.CodeIndex.EmbeddingApiBaseUrl : ""
            };
            return ComputeSha256(JsonSerializer.Serialize(payload, _JsonOptions));
        }

        private static List<string> NormalizeList(List<string> values)
        {
            return values
                .Where(v => !String.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim().Replace('\\', '/').ToLowerInvariant())
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();
        }

        private static int CountDocuments(List<CodeIndexRecord> records)
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CodeIndexRecord record in records)
            {
                paths.Add(record.Path);
            }

            return paths.Count;
        }

        private string DetectLanguage(string relativePath)
        {
            string extension = Path.GetExtension(relativePath).ToLowerInvariant();
            return extension switch
            {
                ".cs" => "csharp",
                ".fs" => "fsharp",
                ".vb" => "vbnet",
                ".ts" => "typescript",
                ".tsx" => "typescript",
                ".js" => "javascript",
                ".jsx" => "javascript",
                ".json" => "json",
                ".md" => "markdown",
                ".yml" => "yaml",
                ".yaml" => "yaml",
                ".xml" => "xml",
                ".html" => "html",
                ".css" => "css",
                ".ps1" => "powershell",
                ".sh" => "shell",
                ".py" => "python",
                ".java" => "java",
                ".kt" => "kotlin",
                ".rs" => "rust",
                ".go" => "go",
                ".sql" => "sql",
                _ => extension.TrimStart('.')
            };
        }

        private string[] SplitQueryTerms(string query)
        {
            return (query ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private int ClampLimit(int requested, int defaultLimit)
        {
            int limit = requested > 0 ? requested : defaultLimit;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;
            return limit;
        }

        private double ScoreRecord(CodeIndexRecord record, string query, string[] terms, float[]? queryVector)
        {
            double lexicalScore = ComputeLexicalScore(record, query, terms);
            bool useSemantic =
                queryVector != null
                && queryVector.Length > 0
                && record.EmbeddingVector != null
                && record.EmbeddingVector.Length > 0
                && record.EmbeddingVector.Length == queryVector.Length;

            if (!useSemantic)
                return lexicalScore;

            double rawCosine = CosineSimilarity(queryVector!, record.EmbeddingVector!);
            double semanticScore = rawCosine;
            if (semanticScore < 0) semanticScore = 0;
            if (semanticScore > 1) semanticScore = 1;

            double lexicalNormalized = lexicalScore / 40.0;
            if (lexicalNormalized < 0) lexicalNormalized = 0;
            if (lexicalNormalized > 1) lexicalNormalized = 1;

            CodeIndexSettings codeIndex = _Settings.CodeIndex;
            return semanticScore * codeIndex.SemanticWeight + lexicalNormalized * codeIndex.LexicalWeight;
        }

        private static double ComputeLexicalScore(CodeIndexRecord record, string query, string[] terms)
        {
            double score = 0;
            string content = record.Content ?? "";
            string path = record.Path ?? "";

            if (!String.IsNullOrWhiteSpace(query))
            {
                if (content.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 8;
                if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 12;
            }

            foreach (string term in terms)
            {
                score += CountOccurrences(content, term) * 1.0;
                score += CountOccurrences(path, term) * 4.0;
            }

            return score;
        }

        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0 || a.Length != b.Length)
                return 0.0;

            double dot = 0;
            double magA = 0;
            double magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double fa = a[i];
                double fb = b[i];
                dot += fa * fb;
                magA += fa * fa;
                magB += fb * fb;
            }

            if (magA <= 0 || magB <= 0)
                return 0.0;

            return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
        }

        private static int CountOccurrences(string text, string term)
        {
            if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(term)) return 0;

            int count = 0;
            int index = 0;
            while (index < text.Length)
            {
                int found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                count++;
                index = found + term.Length;
            }

            return count;
        }

        private string BuildExcerpt(string content, string[] terms)
        {
            string compact = NormalizeLineEndings(content ?? "").Trim();
            if (compact.Length <= 240) return compact;

            int firstMatch = -1;
            foreach (string term in terms)
            {
                int index = compact.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (firstMatch < 0 || index < firstMatch)) firstMatch = index;
            }

            if (firstMatch < 0) firstMatch = 0;
            int start = Math.Max(0, firstMatch - 80);
            int length = Math.Min(240, compact.Length - start);
            string excerpt = compact.Substring(start, length).Trim();
            if (start > 0) excerpt = "..." + excerpt;
            if (start + length < compact.Length) excerpt += "...";
            return excerpt;
        }

        private CodeIndexRecord CopyRecord(CodeIndexRecord record)
        {
            return new CodeIndexRecord
            {
                VesselId = record.VesselId,
                Path = record.Path,
                CommitSha = record.CommitSha,
                ContentHash = record.ContentHash,
                Language = record.Language,
                StartLine = record.StartLine,
                EndLine = record.EndLine,
                Freshness = record.Freshness,
                IndexedAtUtc = record.IndexedAtUtc,
                IsReferenceOnly = record.IsReferenceOnly,
                Content = record.Content,
                EmbeddingVector = record.EmbeddingVector
            };
        }

        private async Task<GraphContextPackExpansion> BuildGraphContextPackExpansionAsync(
            string vesselId,
            string goal,
            CodeSearchResponse search,
            CancellationToken token)
        {
            GraphContextPackExpansion expansion = new GraphContextPackExpansion();

            GraphQueryContext context;
            try
            {
                context = await LoadGraphQueryContextAsync(vesselId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                expansion.Warnings.Add("graph_expansion_failed: " + ex.Message);
                return expansion;
            }

            if (context.Symbols.Count == 0 || context.Edges.Count == 0)
            {
                foreach (string warning in context.Warnings)
                {
                    expansion.Warnings.Add("graph_expansion: " + warning);
                }
                return expansion;
            }

            Dictionary<string, CodeGraphSymbolRecord> seedsByKey = new Dictionary<string, CodeGraphSymbolRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (CodeGraphSymbolRecord seed in ResolveSeedSymbols(context, goal, 5))
            {
                string key = BuildSymbolKey(seed);
                if (!String.IsNullOrWhiteSpace(key)) seedsByKey[key] = seed;
            }

            HashSet<string> resultPaths = new HashSet<string>(
                search.Results.Select(r => r.Record?.Path ?? "").Where(p => !String.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);

            foreach (CodeGraphSymbolRecord symbol in context.Symbols
                .Where(s => resultPaths.Contains(s.Path))
                .OrderBy(s => s.StartLine)
                .Take(8))
            {
                string key = BuildSymbolKey(symbol);
                if (!String.IsNullOrWhiteSpace(key)) seedsByKey[key] = symbol;
            }

            if (seedsByKey.Count == 0)
            {
                return expansion;
            }

            Dictionary<string, CodeGraphImpactResult> impactByKey = new Dictionary<string, CodeGraphImpactResult>(StringComparer.OrdinalIgnoreCase);
            foreach (CodeGraphSymbolRecord seed in seedsByKey.Values.Take(8))
            {
                CodeGraphImpactResponse impact = ComputeImpact(
                    context,
                    SelectSymbolName(seed),
                    CodeGraphTraversalDirectionEnum.Both,
                    2,
                    20);

                foreach (CodeGraphImpactResult result in impact.Results)
                {
                    string key = BuildSymbolKey(result.Symbol);
                    if (String.IsNullOrWhiteSpace(key)) continue;
                    if (!impactByKey.TryGetValue(key, out CodeGraphImpactResult? existing) || result.Score > existing.Score)
                    {
                        impactByKey[key] = result;
                    }
                }
            }

            List<CodeGraphImpactResult> impacts = impactByKey.Values
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.MinDepth)
                .ThenBy(r => SelectSymbolName(r.Symbol), StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            if (impacts.Count == 0)
            {
                return expansion;
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("## Symbol Graph Context");
            builder.AppendLine();
            builder.AppendLine("Graph expansion from indexed supported-language symbol sidecars. Use it to verify callers, callees, and likely tests before editing.");
            builder.AppendLine();
            builder.AppendLine("### Seed symbols");
            foreach (CodeGraphSymbolRecord seed in seedsByKey.Values.Take(8))
            {
                builder.AppendLine("- " + SelectSymbolName(seed) + " (" + seed.Kind + ", `" + seed.Path + ":" + seed.StartLine + "`)");
                expansion.IncludedFiles.Add(seed.Path);
            }

            builder.AppendLine();
            builder.AppendLine("### Nearby graph impact");
            foreach (CodeGraphImpactResult impact in impacts)
            {
                string reasons = impact.Reasons.Count > 0 ? " -- " + String.Join("; ", impact.Reasons) : "";
                builder.AppendLine("- " + SelectSymbolName(impact.Symbol) + " (" + impact.Symbol.Kind + ", depth " + impact.MinDepth + ", `" + impact.Symbol.Path + ":" + impact.Symbol.StartLine + "`)" + reasons);
                expansion.IncludedFiles.Add(impact.Symbol.Path);
            }

            List<CodeGraphAffectedTestCandidate> affectedTests = BuildAffectedTestCandidates(context, goal, 2, 8);
            if (affectedTests.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("### Likely affected tests");
                foreach (CodeGraphAffectedTestCandidate test in affectedTests)
                {
                    builder.AppendLine("- `" + test.TestPath + "` (" + (test.IsExplicitSignal ? "explicit" : "convention") + ", depth " + test.EvidenceDepth + ") " + String.Join("; ", test.Reasons));
                    expansion.IncludedFiles.Add(test.TestPath);
                }
            }

            builder.AppendLine();
            builder.AppendLine("### Graph source excerpts");
            foreach (CodeGraphImpactResult impact in impacts.Take(4))
            {
                CodeGraphSourceSection? section = await TryBuildSourceSectionAsync(vesselId, impact.Symbol, 1, token).ConfigureAwait(false);
                if (section == null || String.IsNullOrWhiteSpace(section.Content)) continue;

                builder.AppendLine();
                builder.AppendLine("#### `" + section.Path + ":" + section.StartLine + "-" + section.EndLine + "`");
                builder.AppendLine();
                builder.AppendLine("```" + DetectLanguage(section.Path));
                builder.AppendLine(section.Content.TrimEnd());
                builder.AppendLine("```");
            }

            expansion.Markdown = builder.ToString().TrimEnd() + "\n";
            expansion.IncludedFiles = expansion.IncludedFiles
                .Where(p => !String.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            expansion.Used = true;
            foreach (string warning in context.Warnings)
            {
                expansion.Warnings.Add("graph_expansion: " + warning);
            }
            return expansion;
        }

        private List<CodeGraphAffectedTestCandidate> BuildAffectedTestCandidates(
            GraphQueryContext context,
            string symbol,
            int maxDepth,
            int maxResults)
        {
            CodeGraphImpactResponse impact = ComputeImpact(
                context,
                symbol,
                CodeGraphTraversalDirectionEnum.Both,
                maxDepth,
                _MaxGraphResults);

            Dictionary<string, AffectedTestAccumulator> candidates = new Dictionary<string, AffectedTestAccumulator>(StringComparer.OrdinalIgnoreCase);
            foreach (CodeGraphImpactResult hit in impact.Results)
            {
                ClassifyTestSignal(hit.Symbol, out bool explicitSignal, out bool conventionSignal, out string signalReason);
                if (!explicitSignal && !conventionSignal) continue;

                AffectedTestAccumulator bucket = GetOrCreateAffectedTestAccumulator(candidates, hit.Symbol.Path);
                bucket.Path = hit.Symbol.Path;
                bucket.Symbol = SelectSymbolName(hit.Symbol);
                bucket.IsExplicitSignal = explicitSignal || bucket.IsExplicitSignal;
                bucket.MinDepth = Math.Min(bucket.MinDepth, hit.MinDepth);
                bucket.Score = Math.Max(bucket.Score, hit.Score + (explicitSignal ? 150 : 40));
                AddReason(bucket.Reasons, signalReason);
                foreach (string reason in hit.Reasons)
                {
                    if (bucket.Reasons.Count >= 4) break;
                    AddReason(bucket.Reasons, reason);
                }
            }

            return candidates.Values
                .OrderByDescending(c => c.IsExplicitSignal)
                .ThenByDescending(c => c.Score)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .Select(c => new CodeGraphAffectedTestCandidate
                {
                    TestPath = c.Path,
                    Symbol = c.Symbol,
                    Score = c.Score,
                    IsExplicitSignal = c.IsExplicitSignal,
                    EvidenceDepth = c.MinDepth == Int32.MaxValue ? 0 : c.MinDepth,
                    Reasons = c.Reasons.ToList()
                })
                .ToList();
        }

        private async Task<Dictionary<string, double>> BuildGraphSearchBoostsAsync(string vesselId, string query, CancellationToken token)
        {
            Dictionary<string, double> boosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            GraphQueryContext context;
            try
            {
                context = await LoadGraphQueryContextAsync(vesselId, token).ConfigureAwait(false);
            }
            catch
            {
                return boosts;
            }

            if (context.Symbols.Count == 0) return boosts;

            List<CodeGraphSymbolRecord> seeds = ResolveSeedSymbols(context, query, 8);
            CodeIndexSettings settings = _Settings.CodeIndex;
            foreach (CodeGraphSymbolRecord seed in seeds)
            {
                AddGraphPathBoost(boosts, seed.Path, settings.GraphSeedBoost);

                foreach (CodeGraphNeighborResult caller in ResolveDirectNeighbors(context, seed, includeCallers: true).Take(8))
                {
                    AddGraphPathBoost(boosts, caller.Symbol.Path, settings.GraphNeighborBoost);
                }

                foreach (CodeGraphNeighborResult callee in ResolveDirectNeighbors(context, seed, includeCallers: false).Take(8))
                {
                    AddGraphPathBoost(boosts, callee.Symbol.Path, settings.GraphNeighborBoost);
                }

                if (seed.Kind == CodeGraphSymbolKindEnum.Endpoint || HasTag(seed, "endpoint") || HasTag(seed, "route"))
                {
                    AddGraphPathBoost(boosts, seed.Path, settings.GraphEndpointBoost);
                }
            }

            foreach (CodeGraphSymbolRecord symbol in context.Symbols)
            {
                if (!String.IsNullOrWhiteSpace(symbol.Framework)
                    && query.Contains(symbol.Framework, StringComparison.OrdinalIgnoreCase))
                {
                    AddGraphPathBoost(boosts, symbol.Path, settings.GraphFrameworkBoost);
                }

                foreach (string tag in symbol.Tags)
                {
                    if (!String.IsNullOrWhiteSpace(tag)
                        && query.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    {
                        AddGraphPathBoost(boosts, symbol.Path, settings.GraphTagBoost);
                    }
                }
            }

            return boosts;
        }

        private static void AddGraphPathBoost(Dictionary<string, double> boosts, string path, double score)
        {
            if (String.IsNullOrWhiteSpace(path) || score <= 0) return;
            if (!boosts.TryGetValue(path, out double existing) || score > existing)
            {
                boosts[path] = score;
            }
        }

        private static bool HasTag(CodeGraphSymbolRecord symbol, string tag)
        {
            if (symbol == null || symbol.Tags == null || String.IsNullOrWhiteSpace(tag)) return false;
            return symbol.Tags.Any(t => String.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<CodeGraphSourceSection?> TryBuildSourceSectionAsync(
            string vesselId,
            CodeGraphSymbolRecord symbol,
            int padding,
            CancellationToken token)
        {
            if (symbol == null || String.IsNullOrWhiteSpace(symbol.Path)) return null;

            CodeIndexStatus status = await LoadReadOnlyStatusAsync(vesselId, token).ConfigureAwait(false);
            List<CodeIndexRecord> records = await ReadRecordsAsync(vesselId, status.Freshness, token).ConfigureAwait(false);
            CodeIndexRecord? record = records
                .Where(r => String.Equals(r.Path, symbol.Path, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.StartLine)
                .FirstOrDefault(r => r.StartLine <= symbol.StartLine && r.EndLine >= symbol.StartLine)
                ?? records
                    .Where(r => String.Equals(r.Path, symbol.Path, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => Math.Abs(r.StartLine - symbol.StartLine))
                    .FirstOrDefault();

            if (record == null || String.IsNullOrEmpty(record.Content)) return null;

            int start = Math.Max(record.StartLine, symbol.StartLine - Math.Max(0, padding));
            int end = Math.Max(start, Math.Min(record.EndLine, symbol.EndLine + Math.Max(0, padding)));
            string[] lines = record.Content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int localStart = Math.Max(0, start - record.StartLine);
            int localEnd = Math.Min(lines.Length - 1, end - record.StartLine);
            if (localStart >= lines.Length || localEnd < localStart) return null;

            string content = String.Join("\n", lines.Skip(localStart).Take(localEnd - localStart + 1));
            return new CodeGraphSourceSection
            {
                Path = symbol.Path,
                StartLine = start,
                EndLine = start + (localEnd - localStart),
                Content = content
            };
        }

        private string BuildContextPackMarkdown(string goal, int tokenBudget, CodeSearchResponse search)
        {
            int charBudget = Math.Max(2000, tokenBudget * 4);
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("# Armada Code Context Pack");
            builder.AppendLine();
            builder.AppendLine("Goal: " + goal);
            builder.AppendLine();
            builder.AppendLine("This is repo discovery evidence from Armada's code index. Playbooks, vessel CLAUDE.md, and project CLAUDE.md rules win on conflict.");
            builder.AppendLine();
            builder.AppendLine("VesselId: " + search.Status.VesselId);
            builder.AppendLine("Commit: " + (search.Status.IndexedCommitSha ?? ""));
            builder.AppendLine("Freshness: " + search.Status.Freshness);
            builder.AppendLine("IndexedAtUtc: " + (search.Status.IndexedAtUtc.HasValue ? search.Status.IndexedAtUtc.Value.ToString("o") : ""));
            builder.AppendLine();
            builder.AppendLine("## Evidence");

            foreach (CodeSearchResult result in search.Results)
            {
                CodeIndexRecord record = result.Record;
                string content = record.Content.Trim();
                if (content.Length > 1200)
                {
                    content = content.Substring(0, 1200).TrimEnd() + "\n...";
                }

                StringBuilder section = new StringBuilder();
                section.AppendLine();
                section.AppendLine("### " + record.Path + ":" + record.StartLine + "-" + record.EndLine);
                section.AppendLine();
                section.AppendLine("- Language: " + record.Language);
                section.AppendLine("- Score: " + result.Score.ToString("0.##"));
                section.AppendLine("- Commit: " + record.CommitSha);
                section.AppendLine("- ContentHash: " + record.ContentHash);
                section.AppendLine("- Freshness: " + record.Freshness);
                section.AppendLine();
                section.AppendLine("```" + record.Language);
                section.AppendLine(content);
                section.AppendLine("```");

                if (builder.Length + section.Length > charBudget)
                {
                    break;
                }

                builder.Append(section);
            }

            if (search.Results.Count == 0)
            {
                builder.AppendLine();
                builder.AppendLine("No indexed evidence matched the goal. Dispatch should include explicit file reads.");
            }

            return builder.ToString().TrimEnd() + "\n";
        }

        private static ContextPackMetrics BuildContextPackMetrics(ContextPackResponse response, bool graphExpansionUsed, int vesselCount)
        {
            List<string> includedFiles = response.Results
                .Select(r => r.Record?.Path ?? "")
                .Concat(response.GraphIncludedFiles ?? new List<string>())
                .Where(p => !String.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ContextPackMetrics
            {
                ResultCount = response.Results.Count,
                IncludedFileCount = includedFiles.Count,
                IncludedFiles = includedFiles,
                MatchedHintCount = response.MatchedHintIds.Count,
                MatchedHintIds = response.MatchedHintIds
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                GraphExpansionUsed = graphExpansionUsed,
                WarningCount = response.Warnings.Count,
                IsSummarized = response.IsSummarized,
                PrestagedFileCount = response.PrestagedFiles.Count,
                EstimatedTokens = response.EstimatedTokens,
                VesselCount = vesselCount
            };
        }

        private async Task<string> WriteContextPackAsync(string vesselId, string markdown, CancellationToken token)
        {
            string contextPackDirectory = Path.Combine(GetVesselIndexDirectory(vesselId), "context-packs");
            Directory.CreateDirectory(contextPackDirectory);
            string fileName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".md";
            string path = Path.Combine(contextPackDirectory, fileName);
            await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false), token).ConfigureAwait(false);
            return path;
        }

        private async Task<string> WriteFleetContextPackAsync(string fleetId, string markdown, CancellationToken token)
        {
            string contextPackDirectory = Path.Combine(_Settings.CodeIndex.IndexDirectory, "fleets", fleetId, "context-packs");
            Directory.CreateDirectory(contextPackDirectory);
            string fileName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".md";
            string path = Path.Combine(contextPackDirectory, fileName);
            await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false), token).ConfigureAwait(false);
            return path;
        }

        private int EstimateTokens(string markdown)
        {
            return (int)Math.Ceiling((markdown ?? "").Length / 4.0);
        }

        private sealed class GraphQueryContext
        {
            public CodeIndexStatus Status { get; }

            public List<CodeGraphSymbolRecord> Symbols { get; }

            public List<CodeGraphEdgeRecord> Edges { get; }

            public Dictionary<string, List<CodeGraphSymbolRecord>> SymbolLookup { get; }

            public List<string> Warnings { get; }

            public GraphQueryContext(
                CodeIndexStatus status,
                List<CodeGraphSymbolRecord> symbols,
                List<CodeGraphEdgeRecord> edges,
                Dictionary<string, List<CodeGraphSymbolRecord>> symbolLookup,
                List<string> warnings)
            {
                Status = status;
                Symbols = symbols;
                Edges = edges;
                SymbolLookup = symbolLookup;
                Warnings = warnings;
            }
        }

        private sealed class GraphContextPackExpansion
        {
            public bool Used { get; set; }

            public string Markdown { get; set; } = "";

            public List<string> IncludedFiles { get; set; } = new List<string>();

            public List<string> Warnings { get; } = new List<string>();
        }

        private readonly struct ScoredSymbolMatch
        {
            public double Score { get; }

            public string Reason { get; }

            public ScoredSymbolMatch(double score, string reason)
            {
                Score = score;
                Reason = reason ?? "";
            }
        }

        private readonly struct GraphTraversalStep
        {
            public CodeGraphSymbolRecord Symbol { get; }

            public int Depth { get; }

            public GraphTraversalStep(CodeGraphSymbolRecord symbol, int depth)
            {
                Symbol = symbol;
                Depth = depth;
            }
        }

        private readonly struct GraphEdgeHop
        {
            public CodeGraphSymbolRecord Symbol { get; }

            public string Reason { get; }

            public double WeightBoost { get; }

            public GraphEdgeHop(CodeGraphSymbolRecord symbol, string reason, double weightBoost)
            {
                Symbol = symbol;
                Reason = reason ?? "";
                WeightBoost = weightBoost;
            }
        }

        private sealed class ImpactAccumulator
        {
            public CodeGraphSymbolRecord Symbol { get; }

            public double Score { get; set; } = 0;

            public int MinDepth { get; set; } = Int32.MaxValue;

            public int HitCount { get; set; } = 0;

            public List<string> Reasons { get; } = new List<string>();

            public ImpactAccumulator(CodeGraphSymbolRecord symbol)
            {
                Symbol = symbol;
            }
        }

        private sealed class AffectedTestAccumulator
        {
            public string Path { get; set; } = "";

            public string Symbol { get; set; } = "";

            public double Score { get; set; } = 0;

            public bool IsExplicitSignal { get; set; } = false;

            public int MinDepth { get; set; } = Int32.MaxValue;

            public List<string> Reasons { get; } = new List<string>();

            public AffectedTestAccumulator(string path)
            {
                Path = path ?? "";
            }
        }

        private sealed class CodeIndexActiveUpdate
        {
            public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

            public DateTime HeartbeatUtc { get; set; } = DateTime.UtcNow;

            public string Stage { get; set; } = "starting";

            public int? ProgressDone { get; set; } = null;

            public int? ProgressTotal { get; set; } = null;
        }

        private string GetVesselIndexDirectory(string vesselId)
        {
            return Path.Combine(_Settings.CodeIndex.IndexDirectory, vesselId);
        }

        private string GetStatusPath(string vesselId)
        {
            return Path.Combine(GetVesselIndexDirectory(vesselId), "metadata.json");
        }

        private string GetChunksPath(string vesselId)
        {
            return Path.Combine(GetVesselIndexDirectory(vesselId), "chunks.jsonl");
        }

        private string GetSignaturesPath(string vesselId)
        {
            return Path.Combine(GetVesselIndexDirectory(vesselId), "signatures.jsonl");
        }

        private async Task<string> RunGitAsync(string workingDirectory, CancellationToken token, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            using Process process = new Process { StartInfo = startInfo };
            process.Start();

            string stdout;
            string stderr;
            try
            {
                stdout = await process.StandardOutput.ReadToEndAsync(linkedCts.Token).ConfigureAwait(false);
                stderr = await process.StandardError.ReadToEndAsync(linkedCts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("git timed out after 120 seconds");
            }

            if (process.ExitCode != 0)
            {
                string detail = !String.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
                throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + detail);
            }

            return stdout;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }

        #endregion
    }
}
