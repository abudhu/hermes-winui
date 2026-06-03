using System;
using System.Threading.Tasks;

namespace Hermes.App.Pages.Chat;

/// <summary>
/// Builds the landing-page greeting string and probes Windows for the
/// signed-in user's first name. Lives apart from <see cref="ChatPage"/>
/// because the time-of-day formatter, the title-case helper, and the
/// three-layer WinRT/Win32 name probe form a self-contained concern that
/// has nothing to do with the page's VM wiring or composer behaviour.
/// Pure data helper — no dispatcher hops, no UI mutation; the caller owns
/// applying the result on the UI thread.
/// </summary>
internal static class GreetingProvider
{
    /// <summary>
    /// Synchronous fallback used by the ctor before the async upgrade
    /// can land. Wraps <see cref="BuildGreeting"/> with a title-cased
    /// <see cref="Environment.UserName"/>, which is always available
    /// without I/O.
    /// </summary>
    public static string BuildInitialGreeting()
        => BuildGreeting(TitleCase(Environment.UserName));

    /// <summary>"Good morning, Amit — let's get something done" — time-of-day
    /// prefix + the supplied name + tagline. Falls back to a generic phrase
    /// if <paramref name="name"/> is null or whitespace.</summary>
    public static string BuildGreeting(string? name)
    {
        var hour = DateTime.Now.Hour;
        var timeOfDay = hour switch
        {
            < 5 => "Working late",
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            < 21 => "Good evening",
            _ => "Working late",
        };

        return string.IsNullOrWhiteSpace(name)
            ? $"{timeOfDay} — let's get something done"
            : $"{timeOfDay}, {name} — let's get something done";
    }

    /// <summary>
    /// Looks up the user's actual first name through Windows. Three-layer
    /// probe with progressively weaker guarantees:
    ///
    /// <list type="number">
    /// <item><c>Windows.System.User.GetPropertyAsync(KnownUserProperties.FirstName)</c>
    /// — works for users signed in with a Microsoft Account or set up an
    /// account profile; returns the registered first name verbatim.</item>
    /// <item><c>GetUserNameExW(NameDisplay)</c> — Win32 fallback for AD-joined
    /// users; returns "First Last", we split on whitespace.</item>
    /// <item>If both come back empty, returns <c>null</c> so the caller can
    /// keep whatever synchronous fallback it already has on screen.</item>
    /// </list>
    ///
    /// Returns <c>null</c> (not <c>string.Empty</c>) for the "no name found"
    /// case to give callers a single contract — they don't have to handle
    /// both null and empty.
    /// </summary>
    public static async Task<string?> GetFirstNameAsync()
    {
        string? firstName = null;
        try
        {
            // Layer 1: WinRT User API.
            var users = await Windows.System.User.FindAllAsync(
                Windows.System.UserType.LocalUser,
                Windows.System.UserAuthenticationStatus.LocallyAuthenticated);
            foreach (var u in users)
            {
                var value = await u.GetPropertyAsync(Windows.System.KnownUserProperties.FirstName);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    firstName = s.Trim();
                    break;
                }
            }
        }
        catch
        {
            // Capability denied / no users found — fall through.
        }

        if (string.IsNullOrEmpty(firstName))
        {
            // Layer 2: Win32 EXTENDED_NAME_FORMAT::NameDisplay.
            var win32 = TryGetFirstNameFromWin32();
            firstName = string.IsNullOrEmpty(win32) ? null : win32;
        }

        return string.IsNullOrEmpty(firstName) ? null : firstName;
    }

    private static string TitleCase(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Single-pass title-case: capitalize first letter, lowercase rest.
        // Deliberately doesn't try to split "first.last" or "FirstLast"
        // forms — those land cleanly enough as-is for a greeting, and
        // GetFirstNameAsync will replace this with a real name when
        // Windows exposes one.
        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Calls GetUserNameExW with NameDisplay (=3) and returns the part
    /// before the first whitespace. Empty string on any failure — including
    /// the very-common-on-local-accounts case where Windows has no display
    /// name configured and the API returns false with ERROR_NONE_MAPPED.
    /// (The public <see cref="GetFirstNameAsync"/> normalises this empty
    /// to <c>null</c> at the API boundary.)
    /// </summary>
    private static string TryGetFirstNameFromWin32()
    {
        try
        {
            var buffer = new System.Text.StringBuilder(256);
            uint size = (uint)buffer.Capacity;
            if (!Hermes.App.NativeMethods.GetUserNameExW(
                    Hermes.App.NativeMethods.NameDisplay, buffer, ref size))
            {
                return string.Empty;
            }
            var display = buffer.ToString();
            if (string.IsNullOrWhiteSpace(display)) return string.Empty;
            // Strip any "DOMAIN\" prefix if present, then take everything
            // before the first space. AD users often come back with a
            // domain qualifier; MSA / local profiles don't.
            var slash = display.IndexOf('\\');
            if (slash >= 0) display = display[(slash + 1)..];
            var space = display.IndexOf(' ');
            return space > 0 ? display[..space] : display;
        }
        catch
        {
            return string.Empty;
        }
    }
}
