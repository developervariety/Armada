namespace Armada.LoadGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>CLI for provider-neutral database or authenticated API screenshot generation. Preview is the default.</summary>
    public static class Program
    {
        /// <summary>Run the generator; exit 0 on success, 1 on invalid input or failed application.</summary>
        /// <param name="args">Named CLI arguments.</param>
        /// <returns>Exit code.</returns>
        public static async Task<int> Main(string[] args)
        {
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (sender, eventArgs) => { eventArgs.Cancel = true; cancel.Cancel(); };
                try { return await RunAsync(args, cancel.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException || ex is IOException || ex is JsonException || ex is HttpRequestException || ex is OperationCanceledException)
                {
                    Console.Error.WriteLine("Load generation failed: " + ex.Message);
                    return 1;
                }
            }
        }

        private static async Task<int> RunAsync(string[] args, CancellationToken token)
        {
            if (args.Length == 0 || Array.IndexOf(args, "--help") >= 0)
            {
                Console.WriteLine("Armada.LoadGenerator (preview by default; --apply writes historical records)\n" +
                    "Profile: --profile-file PATH (create or resume), --batch-id SLUG, --days 42, --density 1 (0.1-5), --entropy 1 (0-2), --seed INT, --to-utc ISO8601\n" +
                    "API: --retention-days DAYS (optional minimum, never shortens existing retention), --api-url URL --session-file EXTERNAL_PATH (X-Token) or ARMADA_TOKEN environment variable\n" +
                    "Database: --db-type Sqlite|Mysql|Postgresql|SqlServer --db-file PATH --db-host HOST --db-port PORT --db-name NAME --db-user USER --db-password-env NAME\n" +
                    "Database ownership: --tenant-id ID --user-id ID. Existing initialized database required; no migrations are applied.\n" +
                    "--apply requires --profile-file to preserve the exact window/seed for safe retries. API and database targets are mutually exclusive.");
                return 0;
            }
            Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (name == "--apply") { options.Add(name, "true"); continue; }
                if (!name.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new ArgumentException("Expected --name value: " + name);
                options.Add(name, args[++i]);
            }
            string[] supported = new string[] { "--retention-days", "--apply", "--profile-file", "--batch-id", "--days", "--density", "--entropy", "--seed", "--to-utc", "--api-url", "--session-file", "--db-type", "--db-file", "--db-host", "--db-port", "--db-name", "--db-user", "--db-password-env", "--tenant-id", "--user-id" };
            foreach (string option in options.Keys) if (Array.IndexOf(supported, option) < 0) throw new ArgumentException("Unknown option: " + option);
            bool api = options.ContainsKey("--api-url");
            if (api && (options.ContainsKey("--db-type") || options.ContainsKey("--db-file") || options.ContainsKey("--db-host"))) throw new ArgumentException("Choose API or database mode, not both.");
            if (!api && options.ContainsKey("--retention-days")) throw new ArgumentException("--retention-days is available in API mode; configure the server settings for database mode.");
            string? profilePath = options.GetValueOrDefault("--profile-file");
            SyntheticActivityRequest profile;
            if (profilePath != null && File.Exists(profilePath))
            {
                profile = JsonSerializer.Deserialize<SyntheticActivityRequest>(await File.ReadAllTextAsync(profilePath, token).ConfigureAwait(false)) ?? throw new ArgumentException("Profile is empty.");
                foreach (string name in new string[] { "--batch-id", "--days", "--density", "--entropy", "--seed", "--to-utc" }) if (options.ContainsKey(name)) throw new ArgumentException("Existing profile supplies settings; omit " + name + " or use a new profile file.");
            }
            else
            {
                profile = new SyntheticActivityRequest
                {
                    BatchId = options.GetValueOrDefault("--batch-id", "screenshot-" + Guid.NewGuid().ToString("N").Substring(0, 16)),
                    Days = Int32.Parse(options.GetValueOrDefault("--days", "42"), CultureInfo.InvariantCulture),
                    Density = Double.Parse(options.GetValueOrDefault("--density", "1"), CultureInfo.InvariantCulture),
                    Entropy = Double.Parse(options.GetValueOrDefault("--entropy", "1"), CultureInfo.InvariantCulture),
                    Seed = options.TryGetValue("--seed", out string? seed) ? Int32.Parse(seed, CultureInfo.InvariantCulture) : RandomNumberGenerator.GetInt32(Int32.MaxValue),
                    ToUtc = options.TryGetValue("--to-utc", out string? to) ? DateTime.Parse(to, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : DateTime.UtcNow.AddSeconds(-30)
                };
                SyntheticActivityGenerator.Validate(profile, DateTime.UtcNow);
                if (profilePath != null) await File.WriteAllTextAsync(profilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), token).ConfigureAwait(false);
            }
            SyntheticActivityGenerator.Validate(profile, DateTime.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(profile));
            if (!options.ContainsKey("--apply")) { Console.WriteLine("Preview only. Add --apply to write synthetic activity."); return 0; }
            if (profilePath == null) throw new ArgumentException("--apply requires --profile-file for reproducible retries.");
            SyntheticActivityResult result;
            if (api)
            {
                string auth = Environment.GetEnvironmentVariable("ARMADA_TOKEN") ?? "";
                if (options.TryGetValue("--session-file", out string? sessionFile)) auth = (JsonSerializer.Deserialize<LoadGeneratorSession>(await File.ReadAllTextAsync(sessionFile, token).ConfigureAwait(false), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Invalid session file.")).Token;
                if (String.IsNullOrWhiteSpace(auth)) throw new ArgumentException("API mode requires --session-file or ARMADA_TOKEN.");
                using (HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                {
                    http.DefaultRequestHeaders.Add("X-Token", auth);
                    if (options.TryGetValue("--retention-days", out string? retentionValue))
                    {
                        int days = Int32.Parse(retentionValue, CultureInfo.InvariantCulture);
                        if (days < profile.Days + 1 || days > 3650) throw new ArgumentException("Retention must exceed the profile window and be at most 3650 days.");
                        string settingsUrl = options["--api-url"].TrimEnd('/') + "/api/v1/settings";
                        LoadGeneratorRetention retained = await http.GetFromJsonAsync<LoadGeneratorRetention>(settingsUrl, token).ConfigureAwait(false) ?? throw new HttpRequestException("Retention settings unavailable.");
                        if (!retained.DataRetentionDays.HasValue || !retained.RequestHistoryRetentionDays.HasValue) throw new HttpRequestException("Update the Admiral image to expose historical retention settings before applying this profile.");
                        retained.DataRetentionDays = retained.DataRetentionDays == 0 ? 0 : Math.Max(days, retained.DataRetentionDays.Value);
                        retained.RequestHistoryRetentionDays = retained.RequestHistoryRetentionDays == 0 ? 0 : Math.Max(days, retained.RequestHistoryRetentionDays.Value);
                        using (HttpResponseMessage update = await http.PutAsJsonAsync(settingsUrl, retained, token).ConfigureAwait(false)) update.EnsureSuccessStatusCode();
                        Console.WriteLine("Retained screenshot history using minimum " + days + " days (existing unlimited/longer retention preserved).");
                    }
                    using (HttpResponseMessage response = await http.PostAsJsonAsync(options["--api-url"].TrimEnd('/') + "/api/v1/activity/synthetic", profile, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Synthetic activity API returned HTTP " + (int)response.StatusCode + ". Verify admin credentials and update the Admiral image if the route is missing.");
                        result = await response.Content.ReadFromJsonAsync<SyntheticActivityResult>(cancellationToken: token).ConfigureAwait(false) ?? throw new HttpRequestException("Empty generation receipt.");
                    }
                }
            }
            else
            {
                string tenantId = options.GetValueOrDefault("--tenant-id") ?? throw new ArgumentException("Database mode requires --tenant-id.");
                string userId = options.GetValueOrDefault("--user-id") ?? throw new ArgumentException("Database mode requires --user-id.");
                DatabaseSettings settings = new DatabaseSettings { Type = Enum.Parse<DatabaseTypeEnum>(options.GetValueOrDefault("--db-type", "Sqlite"), true) };
                if (options.TryGetValue("--db-file", out string? filename)) settings.Filename = filename;
                else if (settings.Type == DatabaseTypeEnum.Sqlite) throw new ArgumentException("SQLite mode requires an existing --db-file.");
                if (settings.Type == DatabaseTypeEnum.Sqlite && !File.Exists(settings.Filename)) throw new ArgumentException("SQLite database does not exist. Initialize it with Armada first.");
                settings.Hostname = options.GetValueOrDefault("--db-host", "127.0.0.1");
                settings.Port = Int32.Parse(options.GetValueOrDefault("--db-port", "0"), CultureInfo.InvariantCulture);
                if (options.TryGetValue("--db-name", out string? name)) settings.DatabaseName = name;
                if (options.TryGetValue("--db-user", out string? user)) settings.Username = user;
                if (options.TryGetValue("--db-password-env", out string? passwordEnv)) settings.Password = Environment.GetEnvironmentVariable(passwordEnv) ?? throw new ArgumentException("Database password environment variable is missing.");
                LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                using (DatabaseDriver database = DatabaseDriverFactory.Create(settings, logging))
                {
                    if (await database.Users.ReadAsync(tenantId, userId, token).ConfigureAwait(false) == null) throw new ArgumentException("User does not exist in the supplied tenant.");
                    result = await SyntheticActivityGenerator.GenerateAsync(database, profile, tenantId, userId, token).ConfigureAwait(false);
                }
            }
            string receipt = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(receipt);
            await File.WriteAllTextAsync(profilePath + ".receipt.json", receipt, token).ConfigureAwait(false);
            return 0;
        }
    }
}
