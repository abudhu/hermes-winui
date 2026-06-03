using System.IO;
using System.Text.Json;

namespace Hermes.ApiClient;

/// <summary>
/// Snapshot of the running Hermes gateway as recorded in
/// <c>{ConfigDirectory}/gateway.pid</c>. Hermes writes this file when
/// the gateway starts and keeps it up to date. Used by the "Restart
/// Hermes" affordance in MCP settings to tailor instructions to whether
/// a gateway is currently running, and (if it is) which PID + invocation
/// the user should restart.
/// </summary>
/// <param name="Pid">The OS-level process id Hermes recorded for itself.
/// Note: this is the PID of the underlying Python interpreter that hosts
/// the gateway — NOT the PID of any <c>hermes.exe</c> shim. Killing by
/// process name is unsafe; this is the only reliable handle.</param>
/// <param name="Kind">The <c>kind</c> field Hermes writes (e.g.
/// <c>"hermes-gateway"</c>). Preserved verbatim so the UI can confirm
/// it's looking at a gateway rather than some other Hermes process kind
/// that might one day reuse the file format.</param>
/// <param name="Argv">The original argv Hermes was launched with, as
/// recorded at startup. Useful for human-readable diagnostics in the
/// dialog (so users can recognise their own command).</param>
public sealed record HermesGatewayInfo(int Pid, string? Kind, string[] Argv);

/// <summary>
/// Reads the <c>gateway.pid</c> JSON file Hermes drops in its config
/// directory. Pure I/O — no process introspection here. Designed to
/// fail soft: any I/O, parsing, or schema error returns <c>null</c>
/// rather than throwing, because the caller's correct behaviour for
/// "we can't tell whether Hermes is running" and "Hermes definitely
/// isn't running" is identical: show the same restart-help dialog
/// with the "no gateway detected" variant.
/// </summary>
public static class HermesGatewayProbe
{
    /// <summary>Filename Hermes uses inside its config directory.
    /// Matches the constant baked into <c>hermes_cli</c>; if Hermes
    /// ever renames it we'd update this and ship.</summary>
    public const string PidFileName = "gateway.pid";

    /// <summary>Reads and parses the gateway PID file from the given
    /// Hermes config directory. Returns <c>null</c> when the file is
    /// missing, unreadable, or malformed. Never throws.</summary>
    public static HermesGatewayInfo? Probe(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory)) return null;

        var path = Path.Combine(configDirectory, PidFileName);
        return ProbeFile(path);
    }

    /// <summary>Lower-level variant that takes an explicit path. Exposed
    /// for tests so they can feed in synthetic fixtures without staging
    /// a full Hermes config directory.</summary>
    public static HermesGatewayInfo? ProbeFile(string pidFilePath)
    {
        string text;
        try
        {
            if (!File.Exists(pidFilePath)) return null;
            text = File.ReadAllText(pidFilePath);
        }
        catch
        {
            // Locked, permissions-denied, race with Hermes rewriting it,
            // etc. We can't recover; the dialog's "no gateway detected"
            // path is the safe degraded behaviour.
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Pid is the only field we require — without it the file is
            // not usable for our purposes. Hermes always writes it as
            // an integer; we reject anything else rather than coercing.
            if (!root.TryGetProperty("pid", out var pidEl)) return null;
            if (pidEl.ValueKind != JsonValueKind.Number) return null;
            if (!pidEl.TryGetInt32(out var pid)) return null;
            if (pid <= 0) return null;

            string? kind = null;
            if (root.TryGetProperty("kind", out var kindEl)
                && kindEl.ValueKind == JsonValueKind.String)
            {
                kind = kindEl.GetString();
            }

            var argv = System.Array.Empty<string>();
            if (root.TryGetProperty("argv", out var argvEl)
                && argvEl.ValueKind == JsonValueKind.Array)
            {
                var list = new System.Collections.Generic.List<string>(argvEl.GetArrayLength());
                foreach (var item in argvEl.EnumerateArray())
                {
                    // Skip non-string entries silently — argv from
                    // Python is always strings; anything else is a
                    // forward-compat surprise we'd rather drop than
                    // misrepresent in the UI.
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        list.Add(item.GetString() ?? "");
                    }
                }
                argv = list.ToArray();
            }

            return new HermesGatewayInfo(pid, kind, argv);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
