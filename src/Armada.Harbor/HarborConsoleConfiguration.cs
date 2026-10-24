namespace Armada.Harbor
{
    using System;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Runtimes.Harbor;

    internal static class HarborConsoleConfiguration
    {
        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        internal static async Task<HarborRunnerSettings> LoadAsync(string filename)
        {
            string path = Path.GetFullPath(filename);
            string? component = path;
            while (!String.IsNullOrEmpty(component))
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("harbor_configuration_link_refused");
                component = Path.GetDirectoryName(component);
            }
            HarborPrivateStorage.ValidatePrivateFile(path);
            using (StreamReader reader = File.OpenText(path))
            {
                string json = await ReadBoundedAsync(reader, 1024 * 1024).ConfigureAwait(false);
                return JsonSerializer.Deserialize<HarborRunnerSettings>(json, JsonOptions)
                    ?? throw new ArgumentException("harbor_configuration_invalid");
            }
        }

        internal static async Task<string> ReadBoundedAsync(TextReader reader, int limit)
        {
            StringBuilder text = new StringBuilder();
            char[] buffer = new char[4096];
            while (true)
            {
                int count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0) return text.ToString();
                if (text.Length > limit - count) throw new ArgumentException("harbor_input_limit_exceeded");
                text.Append(buffer, 0, count);
            }
        }
    }
}
