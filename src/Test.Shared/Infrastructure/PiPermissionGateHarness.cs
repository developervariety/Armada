namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services;
    using Armada.Runtimes;

    /// <summary>
    /// Runs Armada's pi permission gate extension (the copy shipped in the test bin, <c>Pi/armada-permission-gate.ts</c>)
    /// under plain <c>node</c>: the harness calls the extension's default export with a fake <c>pi</c> object, feeds the
    /// registered <c>tool_call</c> handler the given events concurrently, and returns what the handler resolved to for
    /// each. pi itself is not needed; the extension is plain JavaScript, so it is copied to a <c>.mjs</c> file.
    /// </summary>
    public static class PiPermissionGateHarness
    {
        #region Private-Members

        private const string HarnessScript = @"import { pathToFileURL } from 'node:url';
import { readFileSync } from 'node:fs';
const [extensionPath, eventsPath] = process.argv.slice(2);
const mod = await import(pathToFileURL(extensionPath).href);
let handler = null;
mod.default({ on: (name, h) => { if (name === 'tool_call') handler = h; } });
if (!handler) { console.error('no tool_call handler registered'); process.exit(3); }
const events = JSON.parse(readFileSync(eventsPath, 'utf8'));
const started = Date.now();
const results = await Promise.all(events.map(async (event, index) => {
  const result = await handler(event, { hasUI: false, cwd: process.cwd() });
  return { index, result: result ?? null, finishedMs: Date.now() - started };
}));
process.stdout.write(JSON.stringify(results));
";

        #endregion

        #region Public-Methods

        /// <summary>
        /// The extension in the test bin (copied there by Armada.Runtimes.csproj).
        /// </summary>
        public static string ExtensionPath => Path.Combine(AppContext.BaseDirectory, "Pi", PiRuntime.PermissionGateFileName);

        /// <summary>
        /// The node executable on PATH, or null.
        /// </summary>
        /// <returns>The path, or null.</returns>
        public static string? FindNode()
        {
            return HarborRuntimeDetector.ResolveOnPath("node", Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());
        }

        /// <summary>
        /// A skip reason when node is unavailable, else null.
        /// </summary>
        /// <returns>The reason, or null.</returns>
        public static string? SkipReason()
        {
            return FindNode() == null ? "node is not on PATH (the pi permission gate extension runs under node in tests)." : null;
        }

        /// <summary>
        /// Run the extension's tool_call handler over <paramref name="events"/> (all started at once).
        /// </summary>
        /// <param name="events">Events.</param>
        /// <param name="url">ARMADA_MCP_URL, or null to leave it unset.</param>
        /// <param name="token">ARMADA_MCP_TOKEN, or null to leave it unset.</param>
        /// <param name="timeoutSeconds">ARMADA_PERMISSION_TIMEOUT_SECONDS, or null.</param>
        /// <returns>One outcome per event, in input order.</returns>
        public static async Task<List<PiGateOutcome>> RunAsync(List<PiGateEvent> events, string? url, string? token, int? timeoutSeconds)
        {
            string node = FindNode() ?? throw new InvalidOperationException("node is not on PATH");
            string source = ExtensionPath;
            Asserts.AssertTrue(File.Exists(source), "the extension ships in the build output: " + source);

            string dir = TestTemp.NewDirectory("pi_gate");
            try
            {
                string extension = Path.Combine(dir, "armada-permission-gate.mjs");
                File.Copy(source, extension);
                string harness = Path.Combine(dir, "harness.mjs");
                File.WriteAllText(harness, HarnessScript);
                string eventsPath = Path.Combine(dir, "events.json");
                File.WriteAllText(eventsPath, JsonSerializer.Serialize(events));

                ProcessStartInfo info = new ProcessStartInfo(node)
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add(harness);
                info.ArgumentList.Add(extension);
                info.ArgumentList.Add(eventsPath);
                info.Environment.Remove(CaptainThreadMcpPlanner.UrlEnvironmentVariable);
                info.Environment.Remove(CaptainThreadMcpPlanner.TokenEnvironmentVariable);
                info.Environment.Remove(PiRuntime.PermissionTimeoutEnvironmentVariable);
                if (url != null) info.Environment[CaptainThreadMcpPlanner.UrlEnvironmentVariable] = url;
                if (token != null) info.Environment[CaptainThreadMcpPlanner.TokenEnvironmentVariable] = token;
                if (timeoutSeconds.HasValue) info.Environment[PiRuntime.PermissionTimeoutEnvironmentVariable] = timeoutSeconds.Value.ToString();

                using Process process = Process.Start(info)!;
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                Task exited = process.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false) != exited)
                {
                    try { process.Kill(true); } catch { }
                    throw new TimeoutException("the pi permission gate harness did not finish within 90 seconds");
                }

                string output = await stdout.ConfigureAwait(false);
                string errors = await stderr.ConfigureAwait(false);
                Asserts.AssertEqual(0, process.ExitCode, "harness exit; stderr: " + errors);
                return JsonSerializer.Deserialize<List<PiGateOutcome>>(output) ?? new List<PiGateOutcome>();
            }
            finally
            {
                TestTemp.TryDelete(dir);
            }
        }

        #endregion
    }
}
