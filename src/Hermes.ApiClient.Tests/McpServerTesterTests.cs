using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Xunit;

namespace Hermes.ApiClient.Tests;

public sealed class McpServerTesterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ============================================================
    // ExtractCommand
    // ============================================================

    [Fact]
    public void ExtractCommand_returns_string()
    {
        var body = Parse(@"{""command"":""npx""}");
        Assert.Equal("npx", McpServerTester.ExtractCommand(body));
    }

    [Fact]
    public void ExtractCommand_missing_returns_null()
    {
        var body = Parse(@"{""url"":""http://x""}");
        Assert.Null(McpServerTester.ExtractCommand(body));
    }

    [Fact]
    public void ExtractCommand_wrong_type_returns_null()
    {
        var body = Parse(@"{""command"":42}");
        Assert.Null(McpServerTester.ExtractCommand(body));
    }

    // ============================================================
    // ExtractArgs
    // ============================================================

    [Fact]
    public void ExtractArgs_returns_strings()
    {
        var body = Parse(@"{""args"":[""-y"",""@modelcontextprotocol/server-filesystem"",""C:\\""]}");
        var args = McpServerTester.ExtractArgs(body);
        Assert.Equal(3, args.Count);
        Assert.Equal("-y", args[0]);
        Assert.Equal(@"C:\", args[2]);
    }

    [Fact]
    public void ExtractArgs_missing_returns_empty()
    {
        var body = Parse(@"{""command"":""npx""}");
        Assert.Empty(McpServerTester.ExtractArgs(body));
    }

    [Fact]
    public void ExtractArgs_non_array_returns_empty()
    {
        var body = Parse(@"{""args"":""--all""}");
        Assert.Empty(McpServerTester.ExtractArgs(body));
    }

    [Fact]
    public void ExtractArgs_coerces_numbers_and_bools()
    {
        var body = Parse(@"{""args"":[""--port"",8080,true]}");
        var args = McpServerTester.ExtractArgs(body);
        Assert.Equal(new[] { "--port", "8080", "True" }, args);
    }

    [Fact]
    public void ExtractArgs_skips_objects_and_arrays()
    {
        var body = Parse(@"{""args"":[""ok"",{""bad"":1},[""nope""],""also-ok""]}");
        var args = McpServerTester.ExtractArgs(body);
        Assert.Equal(new[] { "ok", "also-ok" }, args);
    }

    // ============================================================
    // ExtractEnv
    // ============================================================

    [Fact]
    public void ExtractEnv_returns_dict()
    {
        var body = Parse(@"{""env"":{""GITHUB_TOKEN"":""ghp_xxx"",""TZ"":""UTC""}}");
        var env = McpServerTester.ExtractEnv(body);
        Assert.Equal(2, env.Count);
        Assert.Equal("ghp_xxx", env["GITHUB_TOKEN"]);
        Assert.Equal("UTC", env["TZ"]);
    }

    [Fact]
    public void ExtractEnv_coerces_numbers_and_bools()
    {
        var body = Parse(@"{""env"":{""PORT"":8080,""DEBUG"":true,""DRY"":false}}");
        var env = McpServerTester.ExtractEnv(body);
        Assert.Equal("8080", env["PORT"]);
        Assert.Equal("true", env["DEBUG"]);
        Assert.Equal("false", env["DRY"]);
    }

    [Fact]
    public void ExtractEnv_non_object_returns_empty()
    {
        var body = Parse(@"{""env"":[""KEY=val""]}");
        Assert.Empty(McpServerTester.ExtractEnv(body));
    }

    [Fact]
    public void ExtractEnv_is_case_sensitive_for_keys()
    {
        // Process env vars are case-sensitive on POSIX and the dict
        // should preserve the exact casing the user wrote even on Windows
        // so we don't quietly mangle 'Path' into 'PATH'.
        var body = Parse(@"{""env"":{""Foo"":""1"",""foo"":""2""}}");
        var env = McpServerTester.ExtractEnv(body);
        Assert.Equal(2, env.Count);
        Assert.Equal("1", env["Foo"]);
        Assert.Equal("2", env["foo"]);
    }

    // ============================================================
    // ExtractUrl / ExtractHeaders
    // ============================================================

    [Fact]
    public void ExtractUrl_returns_string()
    {
        var body = Parse(@"{""url"":""https://api.example.com/mcp""}");
        Assert.Equal("https://api.example.com/mcp", McpServerTester.ExtractUrl(body));
    }

    [Fact]
    public void ExtractHeaders_is_case_insensitive()
    {
        var body = Parse(@"{""headers"":{""Authorization"":""Bearer x"",""x-custom"":""y""}}");
        var headers = McpServerTester.ExtractHeaders(body);
        Assert.Equal("Bearer x", headers["authorization"]);
        Assert.Equal("y", headers["X-Custom"]);
    }

    // ============================================================
    // ExpandPlaceholders
    // ============================================================

    [Fact]
    public void ExpandPlaceholders_substitutes_from_dict()
    {
        var env = new Dictionary<string, string> { ["TOKEN"] = "abc123" };
        Assert.Equal("Bearer abc123",
            McpServerTester.ExpandPlaceholders("Bearer ${TOKEN}", env));
    }

    [Fact]
    public void ExpandPlaceholders_handles_multiple()
    {
        var env = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };
        Assert.Equal("1-2-1",
            McpServerTester.ExpandPlaceholders("${A}-${B}-${A}", env));
    }

    [Fact]
    public void ExpandPlaceholders_leaves_unknown_keys_literal()
    {
        var env = new Dictionary<string, string>();
        Assert.Equal("missing ${NOPE}",
            McpServerTester.ExpandPlaceholders("missing ${NOPE}", env));
    }

    [Fact]
    public void ExpandPlaceholders_no_placeholder_returns_input()
    {
        var env = new Dictionary<string, string> { ["TOKEN"] = "abc" };
        Assert.Equal("plain text",
            McpServerTester.ExpandPlaceholders("plain text", env));
    }

    [Fact]
    public void ExpandPlaceholders_empty_input_returns_input()
    {
        var env = new Dictionary<string, string>();
        Assert.Equal("", McpServerTester.ExpandPlaceholders("", env));
    }

    // ============================================================
    // BuildInitializeRequest
    // ============================================================

    [Fact]
    public void BuildInitializeRequest_has_required_jsonrpc_fields()
    {
        var req = McpServerTester.BuildInitializeRequest(id: 1);
        var json = JsonSerializer.Serialize(req);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("initialize", root.GetProperty("method").GetString());
        var p = root.GetProperty("params");
        Assert.False(string.IsNullOrEmpty(p.GetProperty("protocolVersion").GetString()));
        Assert.Equal("hermes-winui", p.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(p.GetProperty("clientInfo").GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.Object, p.GetProperty("capabilities").ValueKind);
    }

    // ============================================================
    // ParseInitializeResponse
    // ============================================================

    [Fact]
    public void ParseInitializeResponse_success_with_serverInfo()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":1,""result"":{""serverInfo"":{""name"":""fs"",""version"":""1.2.3""},""capabilities"":{}}}";
        var (ok, name, version, err) = McpServerTester.ParseInitializeResponse(json);
        Assert.True(ok);
        Assert.Equal("fs", name);
        Assert.Equal("1.2.3", version);
        Assert.Null(err);
    }

    [Fact]
    public void ParseInitializeResponse_success_without_serverInfo()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":1,""result"":{""capabilities"":{}}}";
        var (ok, name, version, err) = McpServerTester.ParseInitializeResponse(json);
        Assert.True(ok);
        Assert.Null(name);
        Assert.Null(version);
        Assert.Null(err);
    }

    [Fact]
    public void ParseInitializeResponse_error_response()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":1,""error"":{""code"":-32601,""message"":""Method not found""}}";
        var (ok, _, _, err) = McpServerTester.ParseInitializeResponse(json);
        Assert.False(ok);
        Assert.Contains("Method not found", err);
        Assert.Contains("-32601", err);
    }

    [Fact]
    public void ParseInitializeResponse_missing_result_and_error()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":1}";
        var (ok, _, _, err) = McpServerTester.ParseInitializeResponse(json);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Fact]
    public void ParseInitializeResponse_malformed_json()
    {
        var (ok, _, _, err) = McpServerTester.ParseInitializeResponse("not json");
        Assert.False(ok);
        Assert.Contains("parse", err, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // ParseToolsListResponse
    // ============================================================

    [Fact]
    public void ParseToolsListResponse_counts_tools()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":2,""result"":{""tools"":[{""name"":""a""},{""name"":""b""},{""name"":""c""}]}}";
        var count = McpServerTester.ParseToolsListResponse(json, out var hasMore);
        Assert.Equal(3, count);
        Assert.False(hasMore);
    }

    [Fact]
    public void ParseToolsListResponse_empty_array()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":2,""result"":{""tools"":[]}}";
        var count = McpServerTester.ParseToolsListResponse(json, out _);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ParseToolsListResponse_detects_pagination()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":2,""result"":{""tools"":[{""name"":""a""}],""nextCursor"":""abc""}}";
        var count = McpServerTester.ParseToolsListResponse(json, out var hasMore);
        Assert.Equal(1, count);
        Assert.True(hasMore);
    }

    [Fact]
    public void ParseToolsListResponse_no_tools_field_returns_null()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":2,""result"":{}}";
        Assert.Null(McpServerTester.ParseToolsListResponse(json, out _));
    }

    [Fact]
    public void ParseToolsListResponse_error_returns_null()
    {
        var json = @"{""jsonrpc"":""2.0"",""id"":2,""error"":{""code"":1,""message"":""nope""}}";
        Assert.Null(McpServerTester.ParseToolsListResponse(json, out _));
    }

    // ============================================================
    // ResolveExecutable
    // ============================================================

    [Fact]
    public void ResolveExecutable_finds_on_overridden_path()
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Path, "myprog.exe");
        File.WriteAllText(exe, "stub");
        var resolved = McpServerTester.ResolveExecutable("myprog", temp.Path, ".EXE");
        // PATHEXT entries are upper-case so resolved keeps the upper-case
        // suffix even though File.Exists is case-insensitive on Windows.
        Assert.Equal(Path.GetFullPath(exe), resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveExecutable_respects_pathext_order()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "thing.bat"), "stub");
        File.WriteAllText(Path.Combine(temp.Path, "thing.cmd"), "stub");
        var resolved = McpServerTester.ResolveExecutable("thing", temp.Path, ".BAT;.CMD");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(temp.Path, "thing.bat")),
            resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveExecutable_returns_null_when_missing()
    {
        using var temp = new TempDir();
        Assert.Null(McpServerTester.ResolveExecutable("nope-xyz", temp.Path, ".EXE"));
    }

    [Fact]
    public void ResolveExecutable_returns_explicit_path_as_is_when_it_exists()
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Path, "abs.exe");
        File.WriteAllText(exe, "stub");
        Assert.Equal(Path.GetFullPath(exe),
            McpServerTester.ResolveExecutable(exe));
    }

    [Fact]
    public void ResolveExecutable_returns_null_for_nonexistent_explicit_path()
    {
        var fake = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".exe");
        Assert.Null(McpServerTester.ResolveExecutable(fake));
    }

    [Fact]
    public void ResolveExecutable_with_extension_prefers_bare_name()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "tool.cmd"), "stub");
        var resolved = McpServerTester.ResolveExecutable("tool.cmd", temp.Path, ".EXE;.CMD");
        Assert.Equal(Path.GetFullPath(Path.Combine(temp.Path, "tool.cmd")), resolved);
    }

    // ============================================================
    // NeedsCmdShim
    // ============================================================

    [Fact]
    public void NeedsCmdShim_true_for_cmd_and_bat_on_windows()
    {
        Assert.True(McpServerTester.NeedsCmdShim(@"C:\bin\npx.cmd"));
        Assert.True(McpServerTester.NeedsCmdShim(@"C:\bin\thing.BAT"));
        Assert.False(McpServerTester.NeedsCmdShim(@"C:\bin\python.exe"));
    }

    // ============================================================
    // QuoteForCmd
    // ============================================================

    [Theory]
    [InlineData("simple", "\"simple\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("path\\to\\thing", "\"path\\to\\thing\"")]
    // Trailing backslash before closing quote must be doubled (CommandLineToArgvW)
    [InlineData("C:\\path with space\\", "\"C:\\path with space\\\\\"")]
    // Embedded quote: preceded by one backslash → escaped to \\\"
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("", "\"\"")]
    public void QuoteForCmd_quotes_arg(string input, string expected)
    {
        Assert.Equal(expected, McpServerTester.QuoteForCmd(input));
    }

    [Fact]
    public void BuildCmdShimArguments_wraps_in_outer_quotes()
    {
        var s = McpServerTester.BuildCmdShimArguments(
            @"C:\Program Files\nodejs\npx.cmd",
            new[] { "-y", "@modelcontextprotocol/server-filesystem", @"C:\my data" });
        Assert.StartsWith("/d /s /c \"", s);
        Assert.EndsWith("\"", s);
        Assert.Contains(@"""C:\Program Files\nodejs\npx.cmd""", s);
        Assert.Contains("\"-y\"", s);
        Assert.Contains(@"""C:\my data""", s);
    }

    // ============================================================
    // ExtractFirstJsonObject
    // ============================================================

    [Fact]
    public void ExtractFirstJsonObject_plain_json()
    {
        var s = @"{""hello"":""world""}";
        Assert.Equal(s, McpServerTester.ExtractFirstJsonObject(s, "application/json"));
    }

    [Fact]
    public void ExtractFirstJsonObject_sse_data_line()
    {
        var sse = "event: message\ndata: {\"hello\":\"world\"}\n\n";
        Assert.Equal(@"{""hello"":""world""}",
            McpServerTester.ExtractFirstJsonObject(sse, "text/event-stream"));
    }

    [Fact]
    public void ExtractFirstJsonObject_sse_multiline_data()
    {
        var sse = "data: {\"a\":\ndata: 1}\n\n";
        Assert.Equal("{\"a\":1}",
            McpServerTester.ExtractFirstJsonObject(sse, "text/event-stream"));
    }

    [Fact]
    public void ExtractFirstJsonObject_non_object_returns_null()
    {
        Assert.Null(McpServerTester.ExtractFirstJsonObject(@"[1,2]", "application/json"));
        Assert.Null(McpServerTester.ExtractFirstJsonObject("plain text", "application/json"));
        Assert.Null(McpServerTester.ExtractFirstJsonObject("", "application/json"));
    }

    // ============================================================
    // TestAsync — config validation
    // ============================================================

    [Fact]
    public async Task TestAsync_no_transport_returns_ConfigInvalid()
    {
        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(@"{""enabled"":true}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.ConfigInvalid, result.Status);
    }

    [Fact]
    public async Task TestAsync_both_transports_returns_ConfigInvalid()
    {
        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(@"{""command"":""x"",""url"":""http://y""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.ConfigInvalid, result.Status);
        Assert.Contains("Ambiguous", result.Title);
    }

    [Fact]
    public async Task TestAsync_non_object_body_returns_ConfigInvalid()
    {
        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(@"[]");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.ConfigInvalid, result.Status);
    }

    [Fact]
    public async Task TestAsync_stdio_command_not_found()
    {
        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(@"{""command"":""hermes-test-nonexistent-xyz-abc""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.CommandNotFound, result.Status);
        Assert.Contains("not found", result.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAsync_http_invalid_url_returns_ConfigInvalid()
    {
        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(@"{""url"":""not-a-url""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.ConfigInvalid, result.Status);
    }

    // ============================================================
    // TestAsync — HTTP path with FakeHandler
    // ============================================================

    [Fact]
    public async Task TestAsync_http_success()
    {
        var handler = new FakeHttpHandler((req, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""result"":{""serverInfo"":{""name"":""remote-fs"",""version"":""0.1""},""capabilities"":{}}}",
                    Encoding.UTF8, "application/json"),
            });
        });
        var tester = new McpServerTester(handler, new Dictionary<string, string>());
        var body = Parse(@"{""url"":""https://example.com/mcp"",""headers"":{""Authorization"":""Bearer x""}}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.Success, result.Status);
        Assert.Equal("remote-fs", result.ServerName);
        Assert.Equal("0.1", result.ServerVersion);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task TestAsync_http_expands_url_placeholder()
    {
        var handler = new FakeHttpHandler((req, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""result"":{""capabilities"":{}}}",
                    Encoding.UTF8, "application/json"),
            });
        });
        var env = new Dictionary<string, string> { ["MCP_HOST"] = "remote.example.com" };
        var tester = new McpServerTester(handler, env);
        var body = Parse(@"{""url"":""https://${MCP_HOST}/mcp""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.Success, result.Status);
        Assert.Equal("https://remote.example.com/mcp", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task TestAsync_http_expands_header_placeholder()
    {
        var handler = new FakeHttpHandler((req, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""result"":{""capabilities"":{}}}",
                    Encoding.UTF8, "application/json"),
            });
        });
        var env = new Dictionary<string, string> { ["TOKEN"] = "abc123" };
        var tester = new McpServerTester(handler, env);
        var body = Parse(@"{""url"":""https://example.com/mcp"",""headers"":{""Authorization"":""Bearer ${TOKEN}""}}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.Success, result.Status);
        Assert.Equal("Bearer abc123",
            handler.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task TestAsync_http_server_returns_error_response()
    {
        var handler = new FakeHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    @"{""jsonrpc"":""2.0"",""id"":1,""error"":{""code"":-32603,""message"":""Internal error""}}",
                    Encoding.UTF8, "application/json"),
            }));
        var tester = new McpServerTester(handler, new Dictionary<string, string>());
        var body = Parse(@"{""url"":""https://example.com/mcp""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.ProtocolError, result.Status);
        Assert.Contains("Internal error", result.Detail);
    }

    [Fact]
    public async Task TestAsync_http_non_200_returns_NetworkError()
    {
        var handler = new FakeHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                ReasonPhrase = "Unauthorized",
                Content = new StringContent("auth required"),
            }));
        var tester = new McpServerTester(handler, new Dictionary<string, string>());
        var body = Parse(@"{""url"":""https://example.com/mcp""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.NetworkError, result.Status);
        Assert.Contains("401", result.Title);
    }

    [Fact]
    public async Task TestAsync_http_sse_response_is_parsed()
    {
        var handler = new FakeHttpHandler((_, _) =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"serverInfo\":{\"name\":\"sse-srv\"},\"capabilities\":{}}}\n\n",
                    Encoding.UTF8),
            };
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(msg);
        });
        var tester = new McpServerTester(handler, new Dictionary<string, string>());
        var body = Parse(@"{""url"":""https://example.com/mcp""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.Success, result.Status);
        Assert.Equal("sse-srv", result.ServerName);
    }

    [Fact]
    public async Task TestAsync_http_network_error()
    {
        var handler = new FakeHttpHandler((_, _) =>
            throw new HttpRequestException("connection refused"));
        var tester = new McpServerTester(handler, new Dictionary<string, string>());
        var body = Parse(@"{""url"":""http://127.0.0.1:1/mcp""}");
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.NetworkError, result.Status);
        Assert.Contains("connection refused", result.Detail);
    }

    // ============================================================
    // TestAsync — stdio integration with a fake server (Windows-only)
    // Validates cmd.exe shim wrapping, line-scanning past log noise,
    // env-var passing, and the full handshake on a real process.
    // ============================================================

    [Fact]
    public async Task TestAsync_stdio_fake_server_via_cmd_shim_succeeds()
    {
        using var temp = new TempDir();
        // A fake "MCP server" implemented as a powershell one-liner wrapped
        // in a .cmd shim. It prints one log line to stdout (the tester
        // must skip past it), then a valid JSON-RPC initialize response,
        // then waits for two more reads (initialized notification +
        // tools/list) and answers tools/list with two tools.
        var psScript = Path.Combine(temp.Path, "fake-mcp.ps1");
        File.WriteAllText(psScript, @"
$ErrorActionPreference = 'Stop'
[Console]::Out.WriteLine('starting fake mcp server')
[Console]::Out.Flush()
$null = [Console]::In.ReadLine()  # initialize request
[Console]::Out.WriteLine('{""jsonrpc"":""2.0"",""id"":1,""result"":{""serverInfo"":{""name"":""fake-mcp"",""version"":""9.9""},""capabilities"":{}}}')
[Console]::Out.Flush()
$null = [Console]::In.ReadLine()  # initialized notification
$null = [Console]::In.ReadLine()  # tools/list request
[Console]::Out.WriteLine('{""jsonrpc"":""2.0"",""id"":2,""result"":{""tools"":[{""name"":""a""},{""name"":""b""}]}}')
[Console]::Out.Flush()
Start-Sleep -Seconds 30
");
        var cmdShim = Path.Combine(temp.Path, "fake mcp.cmd"); // intentional space in filename
        File.WriteAllText(cmdShim,
            "@echo off\r\n" +
            $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{psScript}\" %*\r\n");

        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(JsonSerializer.Serialize(new
        {
            command = cmdShim,
            args = new[] { "ignored-arg" },
        }));
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.Success, result.Status);
        Assert.Equal("fake-mcp", result.ServerName);
        Assert.Equal("9.9", result.ServerVersion);
        Assert.Equal(2, result.ToolCount);
    }

    [Fact]
    public async Task TestAsync_stdio_fake_server_initialize_error()
    {
        using var temp = new TempDir();
        var psScript = Path.Combine(temp.Path, "fake-err.ps1");
        File.WriteAllText(psScript, @"
$null = [Console]::In.ReadLine()
[Console]::Out.WriteLine('{""jsonrpc"":""2.0"",""id"":1,""error"":{""code"":-32603,""message"":""nope""}}')
[Console]::Out.Flush()
Start-Sleep -Seconds 10
");
        var cmdShim = Path.Combine(temp.Path, "fake-err.cmd");
        File.WriteAllText(cmdShim,
            "@echo off\r\n" +
            $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{psScript}\"\r\n");

        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(JsonSerializer.Serialize(new { command = cmdShim }));
        var result = await tester.TestAsync(body);
        Assert.Equal(McpTestStatus.ProtocolError, result.Status);
        Assert.Contains("nope", result.Detail);
    }

    [Fact]
    public async Task TestAsync_stdio_fake_server_silent_times_out()
    {
        using var temp = new TempDir();
        var psScript = Path.Combine(temp.Path, "silent.ps1");
        File.WriteAllText(psScript, "Start-Sleep -Seconds 60\n");
        var cmdShim = Path.Combine(temp.Path, "silent.cmd");
        File.WriteAllText(cmdShim,
            "@echo off\r\n" +
            $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{psScript}\"\r\n");

        // Hand the tester a short external cancel so we don't actually
        // wait 12s for the built-in timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tester = new McpServerTester(dotEnv: new Dictionary<string, string>());
        var body = Parse(JsonSerializer.Serialize(new { command = cmdShim }));
        var result = await tester.TestAsync(body, cts.Token);
        Assert.Equal(McpTestStatus.Timeout, result.Status);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return _send(request, ct);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "hermes-mcp-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
