namespace Armada.Core.Services.Archive
{
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    // Only native owner metadata is read. An absent, malformed or over-bound header is unresolved,
    // never evidence that an arbitrary old transcript has ended.
    internal sealed class HistorySessionHeader
    {
        public string? Type { get; set; }
        public string? SessionId { get; set; }
        public string? Cwd { get; set; }
        public SessionPayload? Payload { get; set; }

        public HistorySessionHeader() { }

        internal static async Task<string?> ReadWorkingDirectoryAsync(string file, AgentRuntimeEnum runtime, CancellationToken token)
        {
            const int maximum = 65536;
            using (FileStream input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, maximum, FileOptions.Asynchronous))
            {
                byte[] bytes = new byte[maximum];
                int total = 0;
                while (total < bytes.Length)
                {
                    int read = await input.ReadAsync(bytes.AsMemory(total), token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                }
                string text = new UTF8Encoding(false, true).GetString(bytes, 0, total);
                string[] lines = text.Split('\n');
                int complete = total < input.Length ? lines.Length - 1 : lines.Length;
                JsonSerializerOptions options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                for (int i = 0; i < Math.Min(64, complete); i++)
                {
                    if (String.IsNullOrWhiteSpace(lines[i])) continue;
                    HistorySessionHeader? header = JsonSerializer.Deserialize<HistorySessionHeader>(lines[i], options);
                    if (header == null) continue;
                    string? id = runtime == AgentRuntimeEnum.Codex && header.Type == "session_meta" ? header.Payload?.Id
                        : runtime == AgentRuntimeEnum.ClaudeCode ? header.SessionId : null;
                    string? cwd = runtime == AgentRuntimeEnum.Codex && header.Type == "session_meta" ? header.Payload?.Cwd
                        : runtime == AgentRuntimeEnum.ClaudeCode ? header.Cwd : null;
                    if (!Guid.TryParse(id, out Guid ignored) || String.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd)) continue;
                    if (!Path.GetFileName(file).Contains(id!, StringComparison.OrdinalIgnoreCase)) return null;
                    return Path.GetFullPath(cwd);
                }
                return null;
            }
        }

        internal sealed class SessionPayload
        {
            public string? Id { get; set; }
            public string? Cwd { get; set; }
            public SessionPayload() { }
        }
    }
}
