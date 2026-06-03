using System;
using System.Collections.Generic;
using System.IO;

namespace Hermes.ApiClient;

/// <summary>
/// Resolves the local Hermes config directory and loads the .env file
/// so the tray app can talk to the gateway without hardcoded credentials.
/// </summary>
public sealed class HermesConfig
{
    public string ConfigDirectory { get; }
    public string Host { get; }
    public int Port { get; }
    public string? ApiKey { get; }
    public string ModelName { get; }
    public Uri BaseAddress => new($"http://{Host}:{Port}/");

    /// <summary>Full path to the <c>.env</c> file inside
    /// <see cref="ConfigDirectory"/>. Exposed so the Settings page can
    /// edit it (via <c>EnvFileWriter.Save</c>) without re-deriving the
    /// path.</summary>
    public string EnvFilePath => Path.Combine(ConfigDirectory, ".env");

    private HermesConfig(string dir, IReadOnlyDictionary<string, string> env)
    {
        ConfigDirectory = dir;
        Host = env.GetValueOrDefault("API_SERVER_HOST", "127.0.0.1");
        Port = int.TryParse(env.GetValueOrDefault("API_SERVER_PORT", "8642"), out var p) ? p : 8642;
        ApiKey = env.GetValueOrDefault("API_SERVER_KEY");
        ModelName = env.GetValueOrDefault("API_SERVER_MODEL_NAME", "hermes-agent");
    }

    public static HermesConfig Load()
    {
        var dir = ResolveConfigDirectory();
        var envPath = Path.Combine(dir, ".env");
        var env = File.Exists(envPath) ? ParseEnv(envPath) : new Dictionary<string, string>();
        return new HermesConfig(dir, env);
    }

    /// <summary>
    /// Probes the three locations Hermes can live in, in priority order:
    /// 1. $HERMES_HOME (explicit override)
    /// 2. %LOCALAPPDATA%\hermes (Windows-native install layout)
    /// 3. %USERPROFILE%\.hermes (Linux/macOS-style layout — used when running under WSL or by tooling that follows the docs verbatim)
    /// Falls back to (2) even if it doesn't exist so error messages point somewhere sensible.
    /// </summary>
    public static string ResolveConfigDirectory()
    {
        var explicitHome = Environment.GetEnvironmentVariable("HERMES_HOME");
        if (!string.IsNullOrWhiteSpace(explicitHome) && Directory.Exists(explicitHome))
            return explicitHome;

        var localAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hermes");
        if (Directory.Exists(localAppData)) return localAppData;

        var dotHermes = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hermes");
        if (Directory.Exists(dotHermes)) return dotHermes;

        return localAppData;
    }

    private static Dictionary<string, string> ParseEnv(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            // Strip a trailing inline comment that isn't inside quotes
            if (!value.StartsWith('"') && !value.StartsWith('\''))
            {
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) value = value[..hash].TrimEnd();
            }

            // Strip matching surrounding quotes
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }
        return result;
    }
}
