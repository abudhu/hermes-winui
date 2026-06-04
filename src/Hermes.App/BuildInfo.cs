using System;
using System.Reflection;

namespace Hermes.App;

/// <summary>
/// Build-time identity surfaced in the Settings → About pane.
/// </summary>
/// <remarks>
/// The commit SHA comes from <see cref="AssemblyInformationalVersionAttribute"/>,
/// which the .NET SDK populates as <c>"{Version}+{SourceRevisionId}"</c>
/// when <c>SourceRevisionId</c> is set during the build. See the
/// <c>SetSourceRevisionIdFromGit</c> target in Hermes.App.csproj.
/// </remarks>
public static class BuildInfo
{
    public const string RepoUrl = "https://github.com/abudhu/hermes-winui";
    public static string ReleasesUrl => RepoUrl + "/releases";

    /// <summary>"1.0.0"-style version from the assembly metadata.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>Full commit SHA the assembly was built from, or
    /// <c>null</c> if the build wasn't run from a git checkout.</summary>
    public static string? CommitSha
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (string.IsNullOrEmpty(info)) return null;

            // Informational version looks like "1.0.0+abc123def...". The
            // SDK may append additional "+<buildmetadata>" segments
            // (e.g. ".Branch.main"); we only want the leading hex run
            // immediately after the first '+' so the SHA is clean for
            // both display and the github.com/.../commit/{sha} URL.
            var plus = info.IndexOf('+');
            if (plus < 0 || plus + 1 >= info.Length) return null;

            var rest = info[(plus + 1)..];
            var end = 0;
            while (end < rest.Length && IsHex(rest[end])) end++;
            return end > 0 ? rest[..end] : null;
        }
    }

    /// <summary>First 7 chars of <see cref="CommitSha"/> for display,
    /// or <c>null</c> if no SHA is available.</summary>
    public static string? ShortCommit
    {
        get
        {
            var sha = CommitSha;
            if (sha is null) return null;
            return sha.Length >= 7 ? sha[..7] : sha;
        }
    }

    /// <summary>GitHub permalink to the exact build commit, or
    /// <c>null</c> if no SHA was embedded.</summary>
    public static string? CommitUrl =>
        CommitSha is null ? null : $"{RepoUrl}/commit/{CommitSha}";

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
