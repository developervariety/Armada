namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using System.Text.Json.Nodes;
    using Armada.Core.Services;
    using Armada.Helm.Commands;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Behavior checks for Helm MCP configuration mutation.</summary>
    public sealed class HelmMcpConfigSuite : IArmadaTestSuite
    {
        /// <summary>Build the registered Helm MCP behavior cases.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("jsonc_install_remove_is_idempotent_and_preserves_settings", "Helm JSONC MCP install/remove", TestTags.Positive, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-mcp-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "opencode.jsonc");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, """
{ // keep comments parseable
  "provider": { "name": "test" },
  "mcp": { "other": { "type": "remote", }, },
}
""");
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("OpenCode", path, new JsonObject { ["type"] = "remote", ["url"] = "http://localhost:7891/mcp" }, IsOpenCodeConfig: true);
                    try
                    {
                        McpConfigHelper.ApplyResult first = await McpConfigHelper.InstallTargetAsync(target);
                        McpConfigHelper.ApplyResult second = await McpConfigHelper.InstallTargetAsync(target);
                        AssertTrue(first.Changed, "First install must change the file.");
                        AssertFalse(second.Changed, "Second install must be idempotent.");
                        string installed = await File.ReadAllTextAsync(path);
                        AssertTrue(installed.Contains("// keep comments parseable", StringComparison.Ordinal), "JSONC comments must remain.");
                        JsonObject installedRoot = JsonNode.Parse(installed, null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
                        AssertEqual("test", installedRoot["provider"]!["name"]!.GetValue<string>());
                        AssertEqual("remote", installedRoot["mcp"]!["other"]!["type"]!.GetValue<string>());
                        AssertEqual("http://localhost:7891/mcp", installedRoot["mcp"]!["armada"]!["url"]!.GetValue<string>());
                        McpConfigHelper.ApplyResult removed = await McpConfigHelper.RemoveTargetAsync(target);
                        McpConfigHelper.ApplyResult removedAgain = await McpConfigHelper.RemoveTargetAsync(target);
                        AssertTrue(removed.Changed, "Remove must delete Armada entry.");
                        AssertFalse(removedAgain.Changed, "Repeated remove must be idempotent.");
                        string final = await File.ReadAllTextAsync(path);
                        AssertTrue(final.Contains("// keep comments parseable", StringComparison.Ordinal), "Remove must preserve JSONC comments.");
                        JsonObject finalRoot = JsonNode.Parse(final, null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
                        AssertEqual("test", finalRoot["provider"]!["name"]!.GetValue<string>());
                        AssertEqual("remote", finalRoot["mcp"]!["other"]!["type"]!.GetValue<string>());
                        AssertFalse(final.Contains("armada", StringComparison.OrdinalIgnoreCase), "Remove must delete only Armada entry.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("malformed_jsonc_does_not_overwrite_existing_file", "Helm malformed JSONC safety", TestTags.Negative, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-mcp-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "opencode.jsonc");
                    Directory.CreateDirectory(root);
                    string original = "{ invalid";
                    await File.WriteAllTextAsync(path, original);
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("OpenCode", path, new JsonObject { ["type"] = "remote" }, IsOpenCodeConfig: true);
                    try
                    {
                        await McpConfigHelper.InstallTargetAsync(target);
                        AssertEqual(original, await File.ReadAllTextAsync(path));
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        AssertEqual(original, await File.ReadAllTextAsync(path));
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("malformed_managed_value_install_and_remove_do_not_write", "Helm malformed managed value safety", TestTags.Negative, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-managed-invalid-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "mcp.jsonc");
                    Directory.CreateDirectory(root);
                    string original = "{\n  \"mcpServers\": {\n    \"armada\": INVALID\n  }\n}\n";
                    await File.WriteAllTextAsync(path, original);
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("Generic", path, new JsonObject { ["type"] = "remote" });
                    try
                    {
                        await AssertThrowsAsync<System.Text.Json.JsonException>(() => McpConfigHelper.InstallTargetAsync(target));
                        AssertEqual(original, await File.ReadAllTextAsync(path), "Malformed managed value must remain unchanged after install.");
                        await AssertThrowsAsync<System.Text.Json.JsonException>(() => McpConfigHelper.RemoveTargetAsync(target));
                        AssertEqual(original, await File.ReadAllTextAsync(path), "Malformed managed value must remain unchanged after remove.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("jsonc_utf8_bom_is_preserved", "Helm JSONC UTF-8 BOM preservation", TestTags.Positive, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-bom-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "mcp.jsonc");
                    Directory.CreateDirectory(root);
                    byte[] original = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'\n', (byte)'}', (byte)'\n' };
                    await File.WriteAllBytesAsync(path, original);
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("Generic", path, new JsonObject { ["type"] = "remote" });
                    try
                    {
                        await McpConfigHelper.InstallTargetAsync(target);
                        byte[] installed = await File.ReadAllBytesAsync(path);
                        AssertTrue(installed.Length >= 3 && installed[0] == 0xEF && installed[1] == 0xBB && installed[2] == 0xBF, "Install must preserve a UTF-8 BOM.");
                        await McpConfigHelper.RemoveTargetAsync(target);
                        byte[] removed = await File.ReadAllBytesAsync(path);
                        AssertTrue(removed.Length >= 3 && removed[0] == 0xEF && removed[1] == 0xBB && removed[2] == 0xBF, "Remove must preserve a UTF-8 BOM.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("mux_install_remove_is_idempotent", "Helm Mux MCP install/remove", TestTags.Positive, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-mux-" + Guid.NewGuid().ToString("N"));
                    string? prior = Environment.GetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable);
                    Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, root);
                    try
                    {
                        McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("Mux", Path.Combine(root, "mcp-servers.json"), new JsonObject
                        {
                            ["name"] = "armada",
                            ["transport"] = "http",
                            ["url"] = "http://localhost:7891",
                            ["mcpPath"] = "/mcp",
                        }, IsMuxServers: true);
                        McpConfigHelper.ApplyResult first = await McpConfigHelper.InstallTargetAsync(target);
                        McpConfigHelper.ApplyResult second = await McpConfigHelper.InstallTargetAsync(target);
                        AssertTrue(first.Changed, "First Mux install must change the file.");
                        AssertFalse(second.Changed, "Second Mux install must be idempotent.");
                        McpConfigHelper.ApplyResult removed = await McpConfigHelper.RemoveTargetAsync(target);
                        McpConfigHelper.ApplyResult removedAgain = await McpConfigHelper.RemoveTargetAsync(target);
                        AssertTrue(removed.Changed, "Mux remove must delete Armada entry.");
                        AssertFalse(removedAgain.Changed, "Repeated Mux remove must be idempotent.");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, prior);
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("mux_preserves_existing_and_rejects_scalar_shape", "Helm Mux shape safety", TestTags.Negative, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-mux-shape-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "mcp-servers.json");
                    string? prior = Environment.GetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable);
                    Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, root);
                    try
                    {
                        Directory.CreateDirectory(root);
                        await File.WriteAllTextAsync(path, "{\"servers\":[{\"name\":\"other\",\"url\":\"http://other\"},{\"name\":\"armada\",\"url\":\"http://old\"}]}\n");
                        McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("Mux", path, new JsonObject
                        {
                            ["name"] = "armada",
                            ["transport"] = "http",
                            ["url"] = "http://localhost:7891",
                            ["mcpPath"] = "/mcp",
                        }, IsMuxServers: true);
                        McpConfigHelper.ApplyResult installed = await McpConfigHelper.InstallTargetAsync(target);
                        AssertTrue(installed.Changed, "Mux install must add Armada beside an existing server.");
                        JsonObject parsed = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                        AssertEqual(2, parsed["servers"]!.AsArray().Count);
                        AssertEqual("http://other", parsed["servers"]!.AsArray()[0]!["url"]!.GetValue<string>());
                        AssertEqual("http://localhost:7891", parsed["servers"]!.AsArray()[1]!["url"]!.GetValue<string>());

                        string original = "{\"servers\":\"invalid\"}";
                        await File.WriteAllTextAsync(path, original);
                        try
                        {
                            await McpConfigHelper.InstallTargetAsync(target);
                            AssertTrue(false, "A scalar Mux servers value must be rejected.");
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            AssertEqual(original, await File.ReadAllTextAsync(path));
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, prior);
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("mux_install_entry_references_the_api_key_variable", "Helm Mux MCP entry authenticates by variable reference", TestTags.Positive, () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-mux-auth-" + Guid.NewGuid().ToString("N"));
                    string? prior = Environment.GetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable);
                    Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, root);
                    try
                    {
                        Directory.CreateDirectory(root);
                        McpConfigHelper.ConfigTarget? mux = null;
                        foreach (McpConfigHelper.ConfigTarget target in McpConfigHelper.BuildTargets(7891))
                            if (target.ClientName == "Mux") mux = target;
                        AssertNotNull(mux, "A present Mux config directory offers the Mux target.");
                        JsonNode? auth = mux!.ArmadaConfig!["auth"];
                        AssertEqual("api_key", auth?["scheme"]?.GetValue<string>(), "Mux sends the API key in a header.");
                        AssertEqual(McpConfigHelper.ApiKeyHeaderName, auth?["headerName"]?.GetValue<string>(), "Mux names the Armada API key header.");
                        AssertEqual("${" + McpConfigHelper.ApiKeyEnvironmentVariable + "}", auth?["key"]?.GetValue<string>(), "Mux references the API key variable, never a value.");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, prior);
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                    return System.Threading.Tasks.Task.CompletedTask;
                }),
                CaseAsync("generic_jsonc_scoped_edit_has_exact_fixture_output", "Helm generic JSONC scoped edit", TestTags.Positive, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-generic-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "mcp.jsonc");
                    Directory.CreateDirectory(root);
                    string original = """
{
  "text": "fake { \"mcpServers\": { \"armada\": 1 } }",
  "nested": {
    "mcpServers": { "armada": { "url": "nested" } }
  },
  "mcpServers" /* root container */ : /* before value */ {
    "other" /* before colon */ : /* before value */ {
      "text": "brace } and quote \\\""
    },
    "armada" : /* managed comment */ {
      "type": "old"
    } // managed trailing
  },
  "after": true
}
""";
                    JsonObject config = new JsonObject { ["type"] = "remote", ["url"] = "http://localhost:7891/mcp" };
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("Generic", path, config);
                    string expectedInstall = original.Replace("{\n      \"type\": \"old\"\n    }", "{\n  \"type\": \"remote\",\n  \"url\": \"http://localhost:7891/mcp\"\n}", StringComparison.Ordinal);
                    string expectedRemove = """
{
  "text": "fake { \"mcpServers\": { \"armada\": 1 } }",
  "nested": {
    "mcpServers": { "armada": { "url": "nested" } }
  },
  "mcpServers" /* root container */ : /* before value */ {
    "other" /* before colon */ : /* before value */ {
      "text": "brace } and quote \\\""
    },
 // managed trailing
  },
  "after": true
}
""";
                    try
                    {
                        await File.WriteAllTextAsync(path, original);
                        McpConfigHelper.ApplyResult installed = await McpConfigHelper.InstallTargetAsync(target);
                        AssertTrue(installed.Changed, "Generic install must change the managed value.");
                        AssertEqual(expectedInstall, await File.ReadAllTextAsync(path), "Generic install must preserve the exact fixture bytes.");
                        McpConfigHelper.ApplyResult updated = await McpConfigHelper.InstallTargetAsync(target);
                        AssertFalse(updated.Changed, "Generic update must be idempotent.");
                        McpConfigHelper.ApplyResult removed = await McpConfigHelper.RemoveTargetAsync(target);
                        AssertTrue(removed.Changed, "Generic remove must remove the managed value.");
                        AssertEqual(expectedRemove, await File.ReadAllTextAsync(path), "Generic remove must preserve unrelated fixture bytes.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("opencode_jsonc_nested_names_and_escaped_strings_are_scoped", "Helm OpenCode JSONC scope", TestTags.Positive, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-opencode-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "opencode.jsonc");
                    Directory.CreateDirectory(root);
                    string original = """
{
  "description": "fake { \\\"mcp\\\": { \\\"armada\\\": 1 } }",
  "nested": { "mcp": { "armada": { "url": "nested" } } },
  "mcp" /* key comment */ : /* value comment */ {
    "other": { "text": "braces { } and \\\"quotes\\\"" }, // trailing other
  },
  "after": "keep"
}
""";
                    JsonObject config = new JsonObject { ["type"] = "remote", ["url"] = "http://localhost:7891/mcp" };
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("OpenCode", path, config, IsOpenCodeConfig: true);
                    string expectedInstall = """
{
  "description": "fake { \\\"mcp\\\": { \\\"armada\\\": 1 } }",
  "nested": { "mcp": { "armada": { "url": "nested" } } },
  "mcp" /* key comment */ : /* value comment */ {
    "other": { "text": "braces { } and \\\"quotes\\\"" }, // trailing other
    "armada": {
  "type": "remote",
  "url": "http://localhost:7891/mcp"
}
  },
  "after": "keep"
}
""";
                    string expectedRemove = original;
                    try
                    {
                        await File.WriteAllTextAsync(path, original);
                        McpConfigHelper.ApplyResult installed = await McpConfigHelper.InstallTargetAsync(target);
                        string installedText = await File.ReadAllTextAsync(path);
                        AssertTrue(installed.Changed, "OpenCode install must add Armada.");
                        AssertEqual(expectedInstall, installedText, "OpenCode install must preserve exact fixture bytes.");
                        AssertContains("nested", installedText, "Nested fake keys must remain.");
                        AssertContains("braces { }", installedText, "Strings with braces must remain.");
                        AssertContains("// trailing other", installedText, "Comments must remain.");
                        AssertContains("\"armada\":", installedText, "OpenCode must add the direct Armada member.");
                        McpConfigHelper.ApplyResult removed = await McpConfigHelper.RemoveTargetAsync(target);
                        AssertTrue(removed.Changed, "OpenCode remove must remove Armada.");
                        string removedText = await File.ReadAllTextAsync(path);
                        AssertEqual(expectedRemove, removedText, "OpenCode remove must restore exact fixture bytes.");
                        AssertContains("fake {", removedText, "Escaped fake keys must remain after remove.");
                        AssertContains("nested", removedText, "Nested fake keys must remain after remove.");
                        AssertContains("// trailing other", removedText, "Unrelated trailing comments must remain after remove.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("mux_jsonc_array_scope_has_exact_install_remove", "Helm Mux JSONC scoped edit", TestTags.Positive, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-mux-jsonc-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "mcp-servers.json");
                    string? prior = Environment.GetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable);
                    Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, root);
                    Directory.CreateDirectory(root);
                    string original = """
{
  "text": "fake { \\\"servers\\\": [{\\\"name\\\":\\\"armada\\\"}] }",
  "nested": { "servers": [{ "name": "armada", "url": "nested" }] },
  "servers" /* array comment */ : [
    { "name" /* name comment */ : /* value comment */ "other", "url": "http://other" }, // other
    { "name": "armada", "url": "http://old" } // managed
  ]
}
""";
                    JsonObject config = new JsonObject { ["name"] = "armada", ["transport"] = "http", ["url"] = "http://localhost:7891", ["mcpPath"] = "/mcp" };
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("Mux", path, config, IsMuxServers: true);
                    string expectedInstall = """
{
  "text": "fake { \\\"servers\\\": [{\\\"name\\\":\\\"armada\\\"}] }",
  "nested": { "servers": [{ "name": "armada", "url": "nested" }] },
  "servers" /* array comment */ : [
    { "name" /* name comment */ : /* value comment */ "other", "url": "http://other" }, // other
    {
  "name": "armada",
  "transport": "http",
  "url": "http://localhost:7891",
  "mcpPath": "/mcp"
} // managed
  ]
}
""";
                    string expectedRemove = """
{
  "text": "fake { \\\"servers\\\": [{\\\"name\\\":\\\"armada\\\"}] }",
  "nested": { "servers": [{ "name": "armada", "url": "nested" }] },
  "servers" /* array comment */ : [
    { "name" /* name comment */ : /* value comment */ "other", "url": "http://other" }, // other
 // managed
  ]
}
""";
                    try
                    {
                        await File.WriteAllTextAsync(path, original);
                        McpConfigHelper.ApplyResult installed = await McpConfigHelper.InstallTargetAsync(target);
                        string installedText = await File.ReadAllTextAsync(path);
                        AssertTrue(installed.Changed, "Mux install must update Armada.");
                        AssertEqual(expectedInstall, installedText, "Mux install must preserve exact fixture bytes.");
                        AssertContains("nested", installedText, "Nested fake arrays must remain.");
                        AssertContains("/* name comment */", installedText, "Comments between key and value must remain.");
                        AssertContains("\"url\": \"http://localhost:7891\"", installedText, "Mux must update the direct Armada object.");
                        McpConfigHelper.ApplyResult removed = await McpConfigHelper.RemoveTargetAsync(target);
                        AssertTrue(removed.Changed, "Mux remove must remove Armada.");
                        string removedText = await File.ReadAllTextAsync(path);
                        AssertEqual(expectedRemove, removedText, "Mux remove must preserve exact fixture bytes.");
                        AssertContains("\"nested\"", removedText, "Nested fake arrays must remain after remove.");
                        AssertContains("http://other", removedText, "Other Mux servers must remain after remove.");
                        AssertFalse(removedText.Contains("http://localhost:7891", StringComparison.Ordinal), "Managed Mux value must be gone.");
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, prior);
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("jsonc_duplicate_managed_roots_reject_without_write", "Helm JSONC ambiguity safety", TestTags.Negative, async () =>
                {
                    string root = Path.Combine(Path.GetTempPath(), "armada-helm-ambiguous-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(root, "mcp.jsonc");
                    Directory.CreateDirectory(root);
                    string original = "{\n  \"mcpServers\": {},\n  \"mcpServers\": {}\n}\n";
                    await File.WriteAllTextAsync(path, original);
                    McpConfigHelper.ConfigTarget target = new McpConfigHelper.ConfigTarget("Generic", path, new JsonObject { ["type"] = "remote" });
                    try
                    {
                        try
                        {
                            await McpConfigHelper.InstallTargetAsync(target);
                            AssertTrue(false, "Duplicate container roots must be rejected.");
                        }
                        catch (InvalidOperationException)
                        {
                            AssertEqual(original, await File.ReadAllTextAsync(path), "Ambiguous JSONC must remain unchanged.");
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("client_cli_command_survives_output_past_a_pipe_buffer", "Helm MCP client commands do not block on a client that writes more than a pipe buffer", TestTags.Negative, async () =>
                {
                    if (OperatingSystem.IsWindows()) return;

                    // 1 MiB on each stream: a caller that waits for exit without reading leaves the client blocked on a
                    // full pipe, and the exit it waits for never comes.
                    Task<bool> run = Task.Run(() => McpConfigHelper.RunCliCommandAsync("/bin/sh", new[]
                    {
                        "-c",
                        "head -c 1048576 /dev/zero | tr '\\0' 'o'; head -c 1048576 /dev/zero | tr '\\0' 'e' >&2; exit 0"
                    }));
                    Task winner = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                    AssertTrue(winner == run, "The client command must return while it writes more than a pipe buffer.");
                    AssertTrue(await run.ConfigureAwait(false), "A client command that exits 0 reports success.");
                })
            };
            return new TestSuiteDescriptor("Services.HelmMcpConfig", "Helm MCP configuration", cases);
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor("Services.HelmMcpConfig", id, name, _ => body(), new List<string> { tag });
        }
    }
}
