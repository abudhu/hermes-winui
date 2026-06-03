using System.Text;
using System.Text.Json;
using Hermes.ApiClient;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Exercises the subtree-splice behaviour of <see cref="HermesYamlConfig"/>
/// against the fixtures in /Fixtures. The high-value tests are
/// "comments survive byte-for-byte" and "folded personality strings
/// survive byte-for-byte" — those motivate the whole splice approach.
/// </summary>
public class HermesYamlConfigTests : IDisposable
{
    private readonly string _scratch;

    public HermesYamlConfigTests()
    {
        _scratch = Path.Combine(
            Path.GetTempPath(),
            $"hermes-yaml-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private string Stage(string fixtureName)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(src), $"Fixture not found: {src}");
        var dst = Path.Combine(_scratch, fixtureName);
        File.Copy(src, dst, overwrite: true);
        return dst;
    }

    private static JsonElement JsonObj(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- 1. Add server → save → reload → present --------------------------

    [Fact]
    public void Add_Server_Round_Trips()
    {
        var path = Stage("existing-mcp-block.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);
        Assert.Equal(2, servers.Count);

        var next = new List<McpServerEntry>(servers)
        {
            new("newone", JsonObj("""{ "command": "uvx", "args": ["mcp-server-time"] }""")),
        };

        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var (reloaded, _) = HermesYamlConfig.Load(path);
        Assert.Equal(3, reloaded.Count);
        var newone = reloaded.Single(s => s.Name == "newone");
        Assert.Equal("uvx", newone.Body.GetProperty("command").GetString());
        Assert.Equal("mcp-server-time", newone.Body.GetProperty("args")[0].GetString());
    }

    // ---- 2. Edit server → save → reload → present with new values ---------

    [Fact]
    public void Edit_Server_Round_Trips()
    {
        var path = Stage("existing-mcp-block.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        var workiq = servers.Single(s => s.Name == "workiq");
        var edited = new McpServerEntry(
            "workiq",
            JsonObj("""{ "command": "npx", "args": ["-y", "@microsoft/workiq@latest", "mcp"], "timeout": 300 }"""));

        var next = servers.Select(s => s.Name == "workiq" ? edited : s).ToList();
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var (reloaded, _) = HermesYamlConfig.Load(path);
        var w = reloaded.Single(s => s.Name == "workiq");
        Assert.Equal(300, w.Body.GetProperty("timeout").GetInt32());
    }

    // ---- 3. Remove server → save → reload → absent ------------------------

    [Fact]
    public void Remove_Server_Round_Trips()
    {
        var path = Stage("existing-mcp-block.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);
        var next = servers.Where(s => s.Name != "msx").ToList();
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var (reloaded, _) = HermesYamlConfig.Load(path);
        Assert.Single(reloaded);
        Assert.Equal("workiq", reloaded[0].Name);
    }

    // ---- 4. No-change save → no write ------------------------------------

    [Fact]
    public void No_Change_Is_NoOp()
    {
        var path = Stage("existing-mcp-block.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);
        var originalBytes = File.ReadAllBytes(path);
        var originalStamp = File.GetLastWriteTimeUtc(path);

        // Short delay so a real write would change the stamp.
        Thread.Sleep(50);

        var result = HermesYamlConfig.Save(path, token, servers);
        Assert.Equal(YamlSaveStatus.Unchanged, result.Status);

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Equal(originalStamp, File.GetLastWriteTimeUtc(path));
    }

    // ---- 5. Load minimal.yaml → add → top-level key appended -------------

    [Fact]
    public void Append_McpServers_When_Missing()
    {
        var path = Stage("minimal.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);
        Assert.Empty(servers);

        var next = new List<McpServerEntry>
        {
            new("time", JsonObj("""{ "command": "uvx", "args": ["mcp-server-time"] }""")),
        };
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);
        Assert.Contains("mcp_servers:", written, StringComparison.Ordinal);
        Assert.Contains("time:", written, StringComparison.Ordinal);

        var (reloaded, _) = HermesYamlConfig.Load(path);
        Assert.Single(reloaded);
        Assert.Equal("time", reloaded[0].Name);
    }

    // ---- 6. with-comments.yaml: comments survive byte-for-byte ------------

    [Fact]
    public void Comments_Survive_Round_Trip()
    {
        var path = Stage("with-comments.yaml");
        var original = File.ReadAllText(path);

        var (servers, token) = HermesYamlConfig.Load(path);
        Assert.Empty(servers);

        var next = new List<McpServerEntry>
        {
            new("foo", JsonObj("""{ "command": "uvx", "args": ["foo"] }""")),
        };
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);

        // All comment lines from the original must appear verbatim in
        // the output. We don't check exact line offsets because the
        // appended block adds lines at the end.
        var commentLines = original
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("#", StringComparison.Ordinal))
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
        Assert.NotEmpty(commentLines);
        foreach (var line in commentLines)
        {
            Assert.Contains(line, written, StringComparison.Ordinal);
        }
    }

    // ---- 7. folded-strings.yaml: agent.personalities byte-for-byte --------

    [Fact]
    public void FoldedStrings_Survive_Round_Trip()
    {
        var path = Stage("folded-strings.yaml");
        var original = File.ReadAllText(path);

        var (servers, token) = HermesYamlConfig.Load(path);

        var next = new List<McpServerEntry>
        {
            new("filesystem", JsonObj("""{ "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"] }""")),
        };
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);

        // The agent.personalities section must be present byte-for-byte
        // in the output. Extract a contiguous slice from the original
        // covering the personalities block and assert it's a substring.
        var folded = ExtractRange(original, "agent:", "filesystem:");
        // If "filesystem:" isn't in the source (it shouldn't be),
        // ExtractRange falls back to end-of-file. We want the slice
        // through the end of the original.
        var originalSlice = ExtractFromTo(original, "agent:", endIsEof: true);
        Assert.Contains(originalSlice.Trim(), written, StringComparison.Ordinal);
    }

    private static string ExtractRange(string text, string from, string to)
    {
        var s = text.IndexOf(from, StringComparison.Ordinal);
        var e = text.IndexOf(to, StringComparison.Ordinal);
        if (s < 0) return string.Empty;
        if (e < 0 || e <= s) e = text.Length;
        return text[s..e];
    }

    private static string ExtractFromTo(string text, string from, bool endIsEof)
    {
        var s = text.IndexOf(from, StringComparison.Ordinal);
        if (s < 0) return string.Empty;
        return text[s..];
    }

    // ---- 8. Concurrency: external content edit blocks save ---------------

    [Fact]
    public void External_Content_Edit_Is_Detected()
    {
        var path = Stage("existing-mcp-block.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        // Simulate external content edit AFTER our snapshot.
        Thread.Sleep(1100); // beyond 1s timestamp slack
        File.AppendAllText(path, "\n# external edit\n");

        var next = new List<McpServerEntry>(servers);
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.ConflictExternalEdit, result.Status);
    }

    // ---- 9. Concurrency: stamp-only change (no content) passes -----------

    [Fact]
    public void Stamp_Only_Change_Still_Allows_Save()
    {
        var path = Stage("existing-mcp-block.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        // Touch the file (update stamp, NOT content).
        Thread.Sleep(1100);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        var next = new List<McpServerEntry>(servers)
        {
            new("extra", JsonObj("""{ "command": "uvx", "args": ["extra"] }""")),
        };
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);
    }

    // ---- 10. Permanent backup created exactly once -----------------------

    [Fact]
    public void Permanent_Backup_Is_Written_Once_Only()
    {
        var path = Stage("existing-mcp-block.yaml");
        var permanentBak = path + HermesYamlConfig.PermanentBackupSuffix;
        var preTouchBytes = File.ReadAllBytes(path);

        // 1st save
        var (s1, t1) = HermesYamlConfig.Load(path);
        var next1 = new List<McpServerEntry>(s1)
        {
            new("a", JsonObj("""{ "command": "x" }""")),
        };
        var r1 = HermesYamlConfig.Save(path, t1, next1);
        Assert.Equal(YamlSaveStatus.Saved, r1.Status);

        Assert.True(File.Exists(permanentBak));
        var bakBytes = File.ReadAllBytes(permanentBak);
        Assert.Equal(preTouchBytes, bakBytes);

        // 2nd save — must NOT overwrite permanentBak.
        Thread.Sleep(20);
        var (s2, t2) = HermesYamlConfig.Load(path);
        var next2 = new List<McpServerEntry>(s2)
        {
            new("b", JsonObj("""{ "command": "y" }""")),
        };
        var r2 = HermesYamlConfig.Save(path, t2, next2);
        Assert.Equal(YamlSaveStatus.Saved, r2.Status);

        var bakBytesAfter = File.ReadAllBytes(permanentBak);
        Assert.Equal(preTouchBytes, bakBytesAfter);
    }

    // ---- 12. Server name collision via normalisation ---------------------
    // (Collision check lives in the UI validator, not the YAML service,
    //  so this test asserts the name validator only — collision check
    //  is exercised by the ViewModel tests in a separate file.)

    [Fact]
    public void Server_Name_Validator_Rejects_Whitespace_And_Symbols()
    {
        var path = Stage("minimal.yaml");
        var (_, token) = HermesYamlConfig.Load(path);

        var bad = new List<McpServerEntry>
        {
            new("has space", JsonObj("""{ "command": "x" }""")),
        };
        var result = HermesYamlConfig.Save(path, token, bad);
        Assert.Equal(YamlSaveStatus.ValidationFailed, result.Status);
        Assert.Contains("invalid characters", result.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ---- 13. Corrupt YAML throws cleanly, no write -----------------------

    [Fact]
    public void Corrupt_Yaml_Throws_On_Load_No_Write()
    {
        var path = Stage("corrupt.yaml");
        var preBytes = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => HermesYamlConfig.Load(path));

        Assert.Equal(preBytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + HermesYamlConfig.PermanentBackupSuffix));
    }

    // ---- mcp-at-eof: replacing block that ends with no trailing newline --

    [Fact]
    public void Mcp_At_Eof_Is_Replaceable()
    {
        var path = Stage("mcp-at-eof.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);
        Assert.Single(servers);
        Assert.Equal("time", servers[0].Name);

        var next = new List<McpServerEntry>
        {
            new("time", JsonObj("""{ "command": "uvx", "args": ["mcp-server-time"], "timeout": 60 }""")),
        };
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var (reloaded, _) = HermesYamlConfig.Load(path);
        Assert.Single(reloaded);
        Assert.Equal(60, reloaded[0].Body.GetProperty("timeout").GetInt32());
    }

    // ---- mcp-mid-file: ensure keys after mcp_servers are preserved -------

    [Fact]
    public void Mid_File_Mcp_Preserves_Following_Keys()
    {
        var path = Stage("mcp-mid-file.yaml");
        var original = File.ReadAllText(path);
        Assert.Contains("toolsets:", original, StringComparison.Ordinal);
        Assert.Contains("hermes-cli", original, StringComparison.Ordinal);

        var (servers, token) = HermesYamlConfig.Load(path);
        var next = new List<McpServerEntry>(servers)
        {
            new("extra", JsonObj("""{ "command": "uvx", "args": ["e"] }""")),
        };
        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);
        Assert.Contains("toolsets:", written, StringComparison.Ordinal);
        Assert.Contains("hermes-cli", written, StringComparison.Ordinal);
        Assert.Contains("agent:", written, StringComparison.Ordinal);
        Assert.Contains("max_turns: 150", written, StringComparison.Ordinal);
        Assert.Contains("reasoning_effort: xhigh", written, StringComparison.Ordinal);
    }

    // ---- platform_toolsets.api_server splice ------------------------------
    //
    // These tests cover the auto-sync behaviour that fixes the WinUI
    // chat's "MCP tools never appear" problem. The Hermes API server
    // platform passes `include_default_mcp_servers=False` to the
    // toolset loader, so MCP servers must be explicitly named under
    // `platform_toolsets.api_server` for the gateway to expose them
    // over HTTP. We splice that block alongside `mcp_servers` whenever
    // the caller passes the optional toolset list.

    [Fact]
    public void PlatformToolsets_ApiServer_Created_When_Missing_Entirely()
    {
        // Fixture has NO platform_toolsets key at all.
        var path = Stage("no-platform-toolsets.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);
        Assert.Single(servers);

        var apiServerToolsets = new List<string>
        {
            HermesYamlConfig.DefaultApiServerToolset,
            "workiq",
        };

        var result = HermesYamlConfig.Save(path, token, servers, apiServerToolsets);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);
        Assert.Contains("platform_toolsets:", written, StringComparison.Ordinal);
        Assert.Contains("api_server:", written, StringComparison.Ordinal);
        Assert.Contains("- hermes-api-server", written, StringComparison.Ordinal);
        Assert.Contains("- workiq", written, StringComparison.Ordinal);

        // mcp_servers block untouched: the same one server still loads.
        var (reloaded, _) = HermesYamlConfig.Load(path);
        Assert.Single(reloaded);
    }

    [Fact]
    public void PlatformToolsets_ApiServer_Added_To_Existing_Block()
    {
        // Fixture has platform_toolsets with cli + telegram, no api_server.
        var path = Stage("platform-toolsets-without-api-server.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        var apiServerToolsets = new List<string>
        {
            HermesYamlConfig.DefaultApiServerToolset,
            "workiq",
        };
        var result = HermesYamlConfig.Save(path, token, servers, apiServerToolsets);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);
        // New entry is present.
        Assert.Contains("api_server:", written, StringComparison.Ordinal);
        Assert.Contains("- hermes-api-server", written, StringComparison.Ordinal);
        Assert.Contains("- workiq", written, StringComparison.Ordinal);
        // Pre-existing sibling entries (cli, telegram) survive.
        Assert.Contains("cli:", written, StringComparison.Ordinal);
        Assert.Contains("- hermes-cli", written, StringComparison.Ordinal);
        Assert.Contains("telegram:", written, StringComparison.Ordinal);
        Assert.Contains("- hermes-telegram", written, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformToolsets_ApiServer_Replaced_When_Already_Present()
    {
        // Fixture has platform_toolsets.api_server containing a stale
        // entry that no longer exists in mcp_servers. The save MUST
        // replace it wholesale, not append.
        var path = Stage("platform-toolsets-with-api-server.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        var apiServerToolsets = new List<string>
        {
            HermesYamlConfig.DefaultApiServerToolset,
            "workiq",
        };
        var result = HermesYamlConfig.Save(path, token, servers, apiServerToolsets);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);
        Assert.Contains("- hermes-api-server", written, StringComparison.Ordinal);
        Assert.Contains("- workiq", written, StringComparison.Ordinal);
        // Stale entry from the fixture is GONE.
        Assert.DoesNotContain("stale-mcp", written, StringComparison.Ordinal);
        // Siblings still there.
        Assert.Contains("cli:", written, StringComparison.Ordinal);
        Assert.Contains("telegram:", written, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformToolsets_ApiServer_NoOp_When_Already_Matches()
    {
        // Both mcp_servers AND api_server already match the requested
        // state. Save MUST be a true no-op (no write, stamp unchanged).
        var path = Stage("platform-toolsets-with-api-server.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        // Match the fixture's existing api_server list exactly.
        var apiServerToolsets = new List<string>
        {
            HermesYamlConfig.DefaultApiServerToolset,
            "stale-mcp",
        };

        var originalBytes = File.ReadAllBytes(path);
        var originalStamp = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(50);

        var result = HermesYamlConfig.Save(path, token, servers, apiServerToolsets);
        Assert.Equal(YamlSaveStatus.Unchanged, result.Status);

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Equal(originalStamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void PlatformToolsets_ApiServer_Save_Writes_Only_If_ApiServer_Changed()
    {
        // mcp_servers matches (no change there), but api_server does
        // NOT match the request — we expect a Saved status, not Unchanged.
        var path = Stage("platform-toolsets-with-api-server.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        var apiServerToolsets = new List<string>
        {
            HermesYamlConfig.DefaultApiServerToolset,
            "workiq",
        };
        var result = HermesYamlConfig.Save(path, token, servers, apiServerToolsets);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);
    }

    // ---- enabled: false round-trip ----------------------------------------
    //
    // These tests pin the per-server "enabled" key behaviour the MCP
    // Enabled toggle relies on. The Hermes startup filter
    // (tools/mcp_tool.py) reads `enabled` with a default of true, so:
    //   * enabled: false  → server skipped at startup, kept in config.
    //   * key absent      → server enabled (Hermes default).
    // We never write `enabled: true` because that's the default — the
    // toggle going from off → on REMOVES the key. The fixture exercises
    // both shapes (one server with enabled:false mid-block, two without).

    [Fact]
    public void EnabledFalse_Round_Trips_Unchanged()
    {
        // Load a fixture that already has `enabled: false` on one server,
        // re-save with the exact same servers (no edits), expect Unchanged
        // — the field must round-trip through ExtractMcpServers without
        // losing or normalizing the boolean.
        var path = Stage("mcp-with-disabled-server.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);
        Assert.Equal(3, servers.Count);

        var filesystem = servers.Single(s => s.Name == "filesystem");
        Assert.True(filesystem.Body.TryGetProperty("enabled", out var enFs));
        Assert.Equal(JsonValueKind.False, enFs.ValueKind);

        var workiq = servers.Single(s => s.Name == "workiq");
        Assert.False(workiq.Body.TryGetProperty("enabled", out _));

        var originalBytes = File.ReadAllBytes(path);
        Thread.Sleep(50);

        var result = HermesYamlConfig.Save(path, token, servers);
        Assert.Equal(YamlSaveStatus.Unchanged, result.Status);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Add_Server_With_EnabledFalse()
    {
        // Adding a brand-new server that already has enabled:false in its
        // JSON body should land in YAML with the field intact — this is
        // the path taken when the user toggles Enabled off in the editor
        // pane while creating a fresh server.
        var path = Stage("existing-mcp-block.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        var next = new List<McpServerEntry>(servers)
        {
            new("github",
                JsonObj("""{ "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"], "enabled": false }""")),
        };

        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);
        Assert.Contains("github:", written, StringComparison.Ordinal);
        Assert.Contains("enabled: false", written, StringComparison.Ordinal);

        var (reloaded, _) = HermesYamlConfig.Load(path);
        var gh = reloaded.Single(s => s.Name == "github");
        Assert.True(gh.Body.TryGetProperty("enabled", out var en));
        Assert.Equal(JsonValueKind.False, en.ValueKind);
    }

    [Fact]
    public void Save_Without_Enabled_Removes_Field()
    {
        // Load a server with enabled:false, save it with the field
        // stripped from its JSON body (what the editor toggle does when
        // flipped from off to on), confirm the field is GONE from disk
        // and not just normalised to `enabled: true`.
        var path = Stage("mcp-with-disabled-server.yaml");
        var (servers, token) = HermesYamlConfig.Load(path);

        var filesystem = servers.Single(s => s.Name == "filesystem");
        var bodyWithoutEnabled = JsonObj(
            """{ "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"] }""");
        var next = servers
            .Select(s => s.Name == "filesystem"
                ? new McpServerEntry("filesystem", bodyWithoutEnabled)
                : s)
            .ToList();

        var result = HermesYamlConfig.Save(path, token, next);
        Assert.Equal(YamlSaveStatus.Saved, result.Status);

        var written = File.ReadAllText(path);
        // The `enabled` line must be gone for the filesystem entry.
        // Other entries don't have the field, so a substring check is
        // enough — we don't expect "enabled" to appear anywhere.
        Assert.DoesNotContain("enabled:", written, StringComparison.Ordinal);

        var (reloaded, _) = HermesYamlConfig.Load(path);
        var fs = reloaded.Single(s => s.Name == "filesystem");
        Assert.False(fs.Body.TryGetProperty("enabled", out _));
    }
}
