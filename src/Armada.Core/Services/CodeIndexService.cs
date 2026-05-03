namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
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

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Armada settings.</param>
        /// <param name="git">Git service.</param>
        public CodeIndexService(LoggingModule logging, DatabaseDriver database, ArmadaSettings settings, IGitService git)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
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
            return status;
        }

        /// <inheritdoc />
        public async Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
        {
            if (!_Settings.CodeIndex.Enabled) throw new InvalidOperationException("Code indexing is disabled.");
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));

            Vessel vessel = await ReadVesselOrThrowAsync(vesselId, token).ConfigureAwait(false);
            string repoPath = await ResolveRepositoryPathAsync(vessel, token).ConfigureAwait(false);

            if (!String.IsNullOrWhiteSpace(vessel.RepoUrl))
            {
                try
                {
                    await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "fetch failed for vessel " + vessel.Id + ": " + ex.Message);
                }
            }

            string commitSha = await ResolveDefaultBranchCommitAsync(repoPath, vessel.DefaultBranch, token).ConfigureAwait(false);
            string vesselIndexDirectory = GetVesselIndexDirectory(vessel.Id);
            Directory.CreateDirectory(vesselIndexDirectory);

            string tempDirectory = Path.Combine(Path.GetTempPath(), "armada-code-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                await ExtractCommitArchiveAsync(repoPath, commitSha, tempDirectory, token).ConfigureAwait(false);

                List<CodeIndexRecord> records = BuildRecordsFromDirectory(vessel, commitSha, tempDirectory);
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
                    LastError = null
                };

                await WriteIndexAsync(vesselIndexDirectory, status, records, token).ConfigureAwait(false);
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

                double score = ScoreRecord(record, request.Query, terms);
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

            CodeSearchRequest searchRequest = new CodeSearchRequest
            {
                VesselId = request.VesselId,
                Query = request.Goal,
                Limit = maxResults,
                IncludeContent = true,
                IncludeReferenceOnly = false
            };

            CodeSearchResponse search = await SearchAsync(searchRequest, token).ConfigureAwait(false);
            string markdown = BuildContextPackMarkdown(request.Goal, tokenBudget, search);
            string materializedPath = await WriteContextPackAsync(request.VesselId, markdown, token).ConfigureAwait(false);

            ContextPackResponse response = new ContextPackResponse
            {
                Status = search.Status,
                Goal = request.Goal,
                Markdown = markdown,
                EstimatedTokens = EstimateTokens(markdown),
                MaterializedPath = materializedPath,
                Results = search.Results
            };
            response.PrestagedFiles.Add(new PrestagedFile(materializedPath, "_briefing/context-pack.md"));
            return response;
        }

        #endregion

        #region Private-Methods

        private async Task<Vessel> ReadVesselOrThrowAsync(string vesselId, CancellationToken token)
        {
            Vessel? vessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + vesselId);
            return vessel;
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

        private List<CodeIndexRecord> BuildRecordsFromDirectory(Vessel vessel, string commitSha, string rootDirectory)
        {
            List<CodeIndexRecord> records = new List<CodeIndexRecord>();
            string[] files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                string relativePath = NormalizeRepoPath(Path.GetRelativePath(rootDirectory, file));
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

        private double ScoreRecord(CodeIndexRecord record, string query, string[] terms)
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

        private int CountOccurrences(string text, string term)
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
                Content = record.Content
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

        private async Task<string> WriteContextPackAsync(string vesselId, string markdown, CancellationToken token)
        {
            string contextPackDirectory = Path.Combine(GetVesselIndexDirectory(vesselId), "context-packs");
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
