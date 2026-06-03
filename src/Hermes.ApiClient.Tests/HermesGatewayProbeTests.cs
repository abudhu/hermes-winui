using System.IO;
using Hermes.ApiClient;

namespace Hermes.ApiClient.Tests;

/// <summary>
/// Pins the behaviour of <see cref="HermesGatewayProbe"/> against the
/// shapes Hermes actually writes to <c>gateway.pid</c> plus the failure
/// modes the UI relies on (any error → null, never throw).
/// </summary>
public class HermesGatewayProbeTests : IDisposable
{
    private readonly string _scratch;

    public HermesGatewayProbeTests()
    {
        _scratch = Path.Combine(
            Path.GetTempPath(),
            $"hermes-probe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string filename, string content)
    {
        var path = Path.Combine(_scratch, filename);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Returns_Info_For_Well_Formed_Hermes_Pid_File()
    {
        // Real shape captured from a running Hermes install.
        var path = Write("gateway.pid", """
            {"pid": 38580, "kind": "hermes-gateway", "argv": ["C:\\Users\\me\\AppData\\Local\\hermes\\hermes-agent\\hermes_cli\\main.py", "gateway", "run", "--replace"], "start_time": null}
            """);

        var info = HermesGatewayProbe.ProbeFile(path);

        Assert.NotNull(info);
        Assert.Equal(38580, info!.Pid);
        Assert.Equal("hermes-gateway", info.Kind);
        Assert.Equal(4, info.Argv.Length);
        Assert.Equal("gateway", info.Argv[1]);
        Assert.Equal("--replace", info.Argv[3]);
    }

    [Fact]
    public void Returns_Null_When_File_Is_Missing()
    {
        var info = HermesGatewayProbe.ProbeFile(
            Path.Combine(_scratch, "does-not-exist.pid"));
        Assert.Null(info);
    }

    [Fact]
    public void Returns_Null_When_File_Is_Empty()
    {
        var path = Write("gateway.pid", "");
        Assert.Null(HermesGatewayProbe.ProbeFile(path));
    }

    [Fact]
    public void Returns_Null_When_Json_Is_Malformed()
    {
        var path = Write("gateway.pid", "{ this is not json");
        Assert.Null(HermesGatewayProbe.ProbeFile(path));
    }

    [Fact]
    public void Returns_Null_When_Pid_Is_Missing()
    {
        var path = Write("gateway.pid", """{"kind": "hermes-gateway", "argv": []}""");
        Assert.Null(HermesGatewayProbe.ProbeFile(path));
    }

    [Fact]
    public void Returns_Null_When_Pid_Is_Not_An_Integer()
    {
        var path = Write("gateway.pid", """{"pid": "not-a-number", "kind": "hermes-gateway"}""");
        Assert.Null(HermesGatewayProbe.ProbeFile(path));
    }

    [Fact]
    public void Returns_Null_When_Pid_Is_Zero_Or_Negative()
    {
        // 0 and negatives can't be real process ids on Windows; treat
        // them as garbage rather than passing them through to a future
        // Stop-Process call.
        var path = Write("gateway.pid", """{"pid": 0}""");
        Assert.Null(HermesGatewayProbe.ProbeFile(path));

        path = Write("gateway.pid", """{"pid": -1}""");
        Assert.Null(HermesGatewayProbe.ProbeFile(path));
    }

    [Fact]
    public void Tolerates_Missing_Optional_Fields()
    {
        // Minimum viable: just a positive pid. kind/argv default to
        // null/empty so the UI can degrade its diagnostic text gracefully.
        var path = Write("gateway.pid", """{"pid": 12345}""");
        var info = HermesGatewayProbe.ProbeFile(path);

        Assert.NotNull(info);
        Assert.Equal(12345, info!.Pid);
        Assert.Null(info.Kind);
        Assert.Empty(info.Argv);
    }

    [Fact]
    public void Skips_Non_String_Argv_Entries_Silently()
    {
        // Defensive: if a future Hermes ever writes mixed-type argv we'd
        // rather drop the surprises than misrepresent them in the UI.
        var path = Write("gateway.pid", """{"pid": 1, "argv": ["a", 42, "b", null, "c"]}""");
        var info = HermesGatewayProbe.ProbeFile(path);

        Assert.NotNull(info);
        Assert.Equal(new[] { "a", "b", "c" }, info!.Argv);
    }

    [Fact]
    public void Probe_With_ConfigDirectory_Resolves_Pid_File()
    {
        // The directory-overload is what production code uses; verify
        // it composes the path correctly.
        Write("gateway.pid", """{"pid": 99}""");
        var info = HermesGatewayProbe.Probe(_scratch);
        Assert.NotNull(info);
        Assert.Equal(99, info!.Pid);
    }

    [Fact]
    public void Probe_With_Null_Or_Whitespace_Directory_Returns_Null()
    {
        Assert.Null(HermesGatewayProbe.Probe(""));
        Assert.Null(HermesGatewayProbe.Probe("   "));
        Assert.Null(HermesGatewayProbe.Probe(null!));
    }
}
