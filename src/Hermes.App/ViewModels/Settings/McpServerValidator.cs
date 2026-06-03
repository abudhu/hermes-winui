using System.Text.Json;
using System.Text.RegularExpressions;
using Hermes.ApiClient;

namespace Hermes.App.ViewModels.Settings;

/// <summary>
/// Validates a candidate MCP server entry against the
/// <c>NousResearch/hermes-agent</c> SKILL.md schema. Returns a list of
/// errors and a list of (non-blocking) warnings. UI calls this before
/// saving and surfaces results in the editor InfoBar.
/// </summary>
internal static class McpServerValidator
{
    private static readonly Regex NameAllowed = new(
        @"^[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    /// <summary>Known top-level keys that Hermes recognises on a server
    /// config. Anything outside this set that looks like a typo gets a
    /// warning; everything is passed through to the YAML untouched.</summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "command", "args", "env", "url", "headers",
        "timeout", "connect_timeout", "sampling", "enabled",
        "tools", "auth", "supports_parallel_tool_calls",
    };

    /// <summary>Likely-misspelled keys → suggested correct key. We warn
    /// (not block) so power users with future-Hermes features aren't
    /// stuck. Note: the obsolete "disabled is a typo for enabled" hint
    /// was removed when the Enabled toggle shipped — the UI now writes
    /// <c>enabled</c> directly so users no longer hand-type either key.</summary>
    private static readonly Dictionary<string, string> TypoHints = new(StringComparer.Ordinal)
    {
        ["disabled"] = "Hermes ignores 'disabled' — it filters on 'enabled' (use the Enabled toggle instead of editing this by hand).",
        ["header"] = "Did you mean 'headers'?",
        ["environ"] = "Did you mean 'env'?",
        ["arg"] = "Did you mean 'args'?",
        ["commands"] = "Did you mean 'command'?",
        ["urls"] = "Did you mean 'url'?",
    };

    public sealed record Result(
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings,
        JsonElement? ParsedBody);

    public static Result Validate(
        string name,
        string jsonBody,
        IReadOnlyList<McpServerItemVm> allOtherServers)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // ---- Name -------------------------------------------------------
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Server name is required.");
        }
        else
        {
            var trimmed = name.Trim();
            if (trimmed != name)
            {
                errors.Add("Server name must not have leading or trailing whitespace.");
            }
            if (!NameAllowed.IsMatch(trimmed))
            {
                errors.Add("Server name may only contain letters, digits, '_', '.', and '-'.");
            }
            else
            {
                // Collision against Hermes-style normalization. Hermes
                // replaces every non-[A-Za-z0-9_] character with '_'
                // when building the mcp_{server}_{tool} prefix, so e.g.
                // 'my-api' and 'my_api' would BOTH register as
                // mcp_my_api_*. Block that.
                var normalized = NormalizeName(trimmed);
                foreach (var other in allOtherServers)
                {
                    if (NormalizeName(other.Name) == normalized)
                    {
                        errors.Add(
                            $"Server name '{trimmed}' normalises to '{normalized}', which collides " +
                            $"with the existing server '{other.Name}'. Pick a different name.");
                        break;
                    }
                }
            }
        }

        // ---- Body parse ------------------------------------------------
        JsonElement parsedBody = default;
        var bodyParsed = false;
        if (string.IsNullOrWhiteSpace(jsonBody))
        {
            errors.Add("Server body is required (a JSON object).");
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonBody);
                parsedBody = doc.RootElement.Clone();
                bodyParsed = true;
            }
            catch (JsonException ex)
            {
                errors.Add($"Body is not valid JSON: {ex.Message}");
            }
        }

        if (!bodyParsed)
        {
            return new Result(errors, warnings, null);
        }

        if (parsedBody.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Body must be a JSON object (a mapping of keys to values).");
            return new Result(errors, warnings, null);
        }

        // ---- Schema checks --------------------------------------------
        var hasCommand = parsedBody.TryGetProperty("command", out var cmd);
        var hasUrl = parsedBody.TryGetProperty("url", out var url);

        if (hasCommand && hasUrl)
        {
            errors.Add("Server must have either 'command' (stdio) or 'url' (http), not both.");
        }
        else if (!hasCommand && !hasUrl)
        {
            errors.Add("Server must have either 'command' (stdio) or 'url' (http).");
        }

        if (hasCommand && cmd.ValueKind != JsonValueKind.String)
        {
            errors.Add("'command' must be a string.");
        }
        if (hasUrl && url.ValueKind != JsonValueKind.String)
        {
            errors.Add("'url' must be a string.");
        }

        if (parsedBody.TryGetProperty("args", out var args))
        {
            if (args.ValueKind != JsonValueKind.Array)
            {
                errors.Add("'args' must be a JSON array.");
            }
            else
            {
                int i = 0;
                foreach (var a in args.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"'args[{i}]' must be a string.");
                    }
                    i++;
                }
            }
        }

        ValidateStringMap(parsedBody, "env", errors);
        ValidateStringMap(parsedBody, "headers", errors);
        ValidatePositiveInt(parsedBody, "timeout", errors);
        ValidatePositiveInt(parsedBody, "connect_timeout", errors);

        // 'enabled' is shipped exclusively as a boolean via the Enabled
        // toggle. Hermes's _parse_boolish accepts strings ("true"/"false")
        // and other shapes, but anything other than a JSON true/false from
        // our UI is a sign of a hand-edit that should be cleaned up.
        if (parsedBody.TryGetProperty("enabled", out var enabled)
            && enabled.ValueKind != JsonValueKind.True
            && enabled.ValueKind != JsonValueKind.False)
        {
            errors.Add("'enabled' must be a boolean (true or false). Remove it or use the Enabled toggle.");
        }

        // ---- Typo warnings (non-blocking) ------------------------------
        foreach (var prop in parsedBody.EnumerateObject())
        {
            if (TypoHints.TryGetValue(prop.Name, out var hint))
            {
                warnings.Add(hint);
            }
            else if (!KnownKeys.Contains(prop.Name))
            {
                // Unknown but not a known typo — keep silent. Hermes may
                // have future keys we don't know about.
            }
        }

        return new Result(errors, warnings, parsedBody);
    }

    private static void ValidateStringMap(JsonElement body, string key, List<string> errors)
    {
        if (!body.TryGetProperty(key, out var el)) return;
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"'{key}' must be a JSON object of string-to-string entries.");
            return;
        }
        foreach (var p in el.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"'{key}.{p.Name}' must be a string.");
            }
        }
    }

    private static void ValidatePositiveInt(JsonElement body, string key, List<string> errors)
    {
        if (!body.TryGetProperty(key, out var el)) return;
        if (el.ValueKind != JsonValueKind.Number)
        {
            errors.Add($"'{key}' must be a positive integer (seconds).");
            return;
        }
        if (!el.TryGetInt32(out var n) || n < 1 || n > 86400)
        {
            errors.Add($"'{key}' must be a positive integer between 1 and 86400 seconds.");
        }
    }

    /// <summary>
    /// Replicates Hermes's name-normalisation rule:
    /// <c>re.sub(r"[^A-Za-z0-9_]", "_", value)</c>. Used to detect
    /// names that would collide in the <c>mcp_{server}_{tool}</c>
    /// prefix even though they look different in YAML.
    /// </summary>
    public static string NormalizeName(string value)
        => Regex.Replace(value, "[^A-Za-z0-9_]", "_");
}
