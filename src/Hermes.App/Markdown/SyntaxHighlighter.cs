using System;
using System.Collections.Generic;

namespace Hermes.App.Markdown;

/// <summary>Token categories produced by <see cref="SyntaxHighlighter"/>.
/// Each maps to a themed brush at render time.</summary>
public enum TokenKind
{
    Plain,
    Keyword,
    String,
    Number,
    Comment,
    Type,
    Punctuation,
}

public readonly record struct TokenSpan(string Text, TokenKind Kind);

/// <summary>
/// Tiny single-pass tokenizer for the ~handful of languages most likely to
/// appear in a chat reply (csharp, python, javascript/typescript, json,
/// bash, sql, go, rust, powershell). Per-language scanners share a single
/// character-cursor model — for each position, "what kind of token starts
/// here?" — so we don't accidentally mis-classify a string opener as a
/// keyword the way independent regex passes would.
///
/// <para>
/// Constraints (deliberately): this is for chat readability, not
/// IDE-grade highlighting. It does not understand string interpolation,
/// nested generics, or context-dependent tokens. Inputs over
/// <see cref="MaxHighlightChars"/> fall through to a single plain span
/// so a 200KB code dump doesn't pin the UI thread.
/// </para>
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Don't even try to highlight code larger than this; emit one
    /// big plain span instead. Picked so a typical "huge JSON paste" still
    /// highlights but a multi-MB blob doesn't peg layout.</summary>
    public const int MaxHighlightChars = 50_000;

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while", "var", "async", "await",
        "dynamic", "get", "set", "init", "record", "yield", "partial", "global", "when",
        "where", "from", "select", "into", "join", "let", "orderby", "group", "by", "on",
        "equals", "ascending", "descending", "nameof", "with", "and", "or", "not", "required",
        "file", "scoped",
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class",
        "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global",
        "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield", "match", "case", "self", "cls",
    };

    private static readonly HashSet<string> JavaScriptKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "any", "as", "async", "await", "boolean", "break", "case", "catch",
        "class", "const", "constructor", "continue", "debugger", "declare", "default",
        "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for",
        "from", "function", "get", "if", "implements", "import", "in", "instanceof",
        "interface", "is", "keyof", "let", "module", "namespace", "never", "new", "null",
        "number", "object", "of", "package", "private", "protected", "public", "readonly",
        "require", "return", "set", "static", "string", "super", "switch", "this", "throw",
        "true", "try", "type", "typeof", "undefined", "unknown", "var", "void", "while",
        "with", "yield", "satisfies",
    };

    private static readonly HashSet<string> GoKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "chan", "const", "continue", "default", "defer", "else", "fallthrough",
        "for", "func", "go", "goto", "if", "import", "interface", "map", "package", "range",
        "return", "select", "struct", "switch", "type", "var", "true", "false", "nil",
        "iota", "any",
    };

    private static readonly HashSet<string> RustKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "await", "break", "const", "continue", "crate", "dyn", "else", "enum",
        "extern", "false", "fn", "for", "if", "impl", "in", "let", "loop", "match", "mod",
        "move", "mut", "pub", "ref", "return", "self", "Self", "static", "struct", "super",
        "trait", "true", "type", "unsafe", "use", "where", "while", "box", "do", "final",
        "macro", "override", "priv", "typeof", "unsized", "virtual", "yield",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE",
        "CREATE", "TABLE", "DROP", "ALTER", "ADD", "COLUMN", "INDEX", "VIEW", "PROCEDURE",
        "FUNCTION", "TRIGGER", "DATABASE", "SCHEMA", "AS", "ON", "JOIN", "LEFT", "RIGHT",
        "INNER", "OUTER", "FULL", "CROSS", "GROUP", "BY", "HAVING", "ORDER", "ASC", "DESC",
        "LIMIT", "OFFSET", "UNION", "ALL", "DISTINCT", "AND", "OR", "NOT", "IN", "BETWEEN",
        "LIKE", "IS", "NULL", "TRUE", "FALSE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "WITH", "RECURSIVE", "EXISTS", "PRIMARY", "FOREIGN", "KEY", "REFERENCES", "UNIQUE",
        "DEFAULT", "CHECK", "CONSTRAINT", "INT", "INTEGER", "VARCHAR", "TEXT", "DATE",
        "TIMESTAMP", "BOOLEAN", "REAL", "BLOB", "IF", "RETURNING",
    };

    private static readonly HashSet<string> BashKeywords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "fi", "case", "esac", "for", "select", "while", "until",
        "do", "done", "in", "function", "time", "coproc", "return", "exit", "break", "continue",
        "local", "export", "readonly", "declare", "typeset", "unset", "shift", "set", "test",
        "true", "false", "source",
    };

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "continue", "data", "define", "do", "dynamicparam",
        "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach", "from",
        "function", "hidden", "if", "in", "param", "process", "return", "static", "switch",
        "throw", "trap", "try", "until", "using", "var", "while", "true", "false",
    };

    private static readonly HashSet<string> JsonKeywords = new(StringComparer.Ordinal)
    {
        "true", "false", "null",
    };

    /// <summary>Normalize common language aliases that markdown code fences
    /// typically use to a canonical scanner key.</summary>
    public static string NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return "";
        return lang.Trim().ToLowerInvariant() switch
        {
            "c#" or "cs" or "csharp" or "dotnet" or ".net" => "csharp",
            "py" or "python" or "python3" => "python",
            "js" or "javascript" or "node" or "nodejs" => "javascript",
            "ts" or "typescript" => "javascript",
            "json" or "json5" => "json",
            "sh" or "bash" or "shell" or "zsh" => "bash",
            "sql" or "mysql" or "postgresql" or "postgres" or "sqlite" or "tsql" => "sql",
            "go" or "golang" => "go",
            "rust" or "rs" => "rust",
            "ps" or "ps1" or "powershell" or "pwsh" => "powershell",
            _ => "",
        };
    }

    /// <summary>Tokenize <paramref name="code"/> into spans tagged by kind.
    /// Unknown languages or oversized inputs fall through to a single
    /// <see cref="TokenKind.Plain"/> span — the renderer will still display
    /// them, just without coloring.</summary>
    public static IReadOnlyList<TokenSpan> Tokenize(string code, string? language)
    {
        if (string.IsNullOrEmpty(code)) return Array.Empty<TokenSpan>();
        if (code.Length > MaxHighlightChars)
            return new[] { new TokenSpan(code, TokenKind.Plain) };

        var lang = NormalizeLanguage(language);
        return lang switch
        {
            "csharp"     => ScanCLike(code, CSharpKeywords, lineComment: "//", blockComment: ("/*", "*/"), allowChar: true),
            "javascript" => ScanCLike(code, JavaScriptKeywords, lineComment: "//", blockComment: ("/*", "*/"), allowChar: false, allowTemplate: true),
            "go"         => ScanCLike(code, GoKeywords, lineComment: "//", blockComment: ("/*", "*/"), allowChar: true, allowBacktickString: true),
            "rust"       => ScanCLike(code, RustKeywords, lineComment: "//", blockComment: ("/*", "*/"), allowChar: true),
            "json"       => ScanCLike(code, JsonKeywords, lineComment: null, blockComment: null, allowChar: false),
            "sql"        => ScanCLike(code, SqlKeywords, lineComment: "--", blockComment: ("/*", "*/"), allowChar: false, doubleQuoteIsString: true),
            "python"     => ScanPython(code),
            "bash"       => ScanBash(code, BashKeywords),
            "powershell" => ScanPowerShell(code),
            _            => new[] { new TokenSpan(code, TokenKind.Plain) },
        };
    }

    // -----------------------------------------------------------------------
    // C-like scanner — handles csharp/js/ts/go/rust/json/sql with feature flags
    // -----------------------------------------------------------------------
    private static IReadOnlyList<TokenSpan> ScanCLike(
        string code,
        HashSet<string> keywords,
        string? lineComment,
        (string Open, string Close)? blockComment,
        bool allowChar,
        bool allowTemplate = false,
        bool allowBacktickString = false,
        bool doubleQuoteIsString = true)
    {
        var spans = new List<TokenSpan>();
        var sb = new System.Text.StringBuilder();
        int i = 0;

        while (i < code.Length)
        {
            if (lineComment != null && Match(code, i, lineComment))
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && code[i] != '\n') i++;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Comment));
                continue;
            }
            if (blockComment is { } bc && Match(code, i, bc.Open))
            {
                FlushPlain();
                int start = i;
                i += bc.Open.Length;
                while (i < code.Length && !Match(code, i, bc.Close)) i++;
                if (i < code.Length) i += bc.Close.Length;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Comment));
                continue;
            }

            char c = code[i];

            if (doubleQuoteIsString && c == '"')
            {
                FlushPlain();
                spans.Add(new TokenSpan(ConsumeStringLiteral(code, ref i, '"'), TokenKind.String));
                continue;
            }
            if (allowChar && c == '\'')
            {
                FlushPlain();
                spans.Add(new TokenSpan(ConsumeStringLiteral(code, ref i, '\''), TokenKind.String));
                continue;
            }
            if ((allowTemplate || allowBacktickString) && c == '`')
            {
                FlushPlain();
                spans.Add(new TokenSpan(ConsumeStringLiteral(code, ref i, '`'), TokenKind.String));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < code.Length && char.IsDigit(code[i + 1])))
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_')) i++;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Number));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '$')) i++;
                var word = code[start..i];
                if (keywords.Contains(word))
                    spans.Add(new TokenSpan(word, TokenKind.Keyword));
                else if (char.IsUpper(word[0]) && word.Length > 1)
                    spans.Add(new TokenSpan(word, TokenKind.Type));
                else
                    spans.Add(new TokenSpan(word, TokenKind.Plain));
                continue;
            }

            sb.Append(c);
            i++;
        }

        FlushPlain();
        return spans;

        void FlushPlain()
        {
            if (sb.Length > 0)
            {
                spans.Add(new TokenSpan(sb.ToString(), TokenKind.Plain));
                sb.Clear();
            }
        }
    }

    private static IReadOnlyList<TokenSpan> ScanPython(string code)
    {
        var spans = new List<TokenSpan>();
        var sb = new System.Text.StringBuilder();
        int i = 0;

        while (i < code.Length)
        {
            if (code[i] == '#')
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && code[i] != '\n') i++;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Comment));
                continue;
            }
            if (Match(code, i, "\"\"\"") || Match(code, i, "'''"))
            {
                FlushPlain();
                var triple = code.Substring(i, 3);
                int start = i;
                i += 3;
                while (i + 2 < code.Length && !Match(code, i, triple)) i++;
                if (i + 2 < code.Length) i += 3;
                else i = code.Length;
                spans.Add(new TokenSpan(code[start..i], TokenKind.String));
                continue;
            }
            if (code[i] == '"' || code[i] == '\'')
            {
                FlushPlain();
                spans.Add(new TokenSpan(ConsumeStringLiteral(code, ref i, code[i]), TokenKind.String));
                continue;
            }
            if (char.IsDigit(code[i]))
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_')) i++;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Number));
                continue;
            }
            if (char.IsLetter(code[i]) || code[i] == '_')
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                var word = code[start..i];
                if (PythonKeywords.Contains(word))
                    spans.Add(new TokenSpan(word, TokenKind.Keyword));
                else if (char.IsUpper(word[0]) && word.Length > 1)
                    spans.Add(new TokenSpan(word, TokenKind.Type));
                else
                    spans.Add(new TokenSpan(word, TokenKind.Plain));
                continue;
            }

            sb.Append(code[i]);
            i++;
        }

        FlushPlain();
        return spans;

        void FlushPlain()
        {
            if (sb.Length > 0)
            {
                spans.Add(new TokenSpan(sb.ToString(), TokenKind.Plain));
                sb.Clear();
            }
        }
    }

    private static IReadOnlyList<TokenSpan> ScanBash(string code, HashSet<string> keywords)
    {
        var spans = new List<TokenSpan>();
        var sb = new System.Text.StringBuilder();
        int i = 0;

        while (i < code.Length)
        {
            if (code[i] == '#' && (i == 0 || char.IsWhiteSpace(code[i - 1])))
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && code[i] != '\n') i++;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Comment));
                continue;
            }
            if (code[i] == '"' || code[i] == '\'')
            {
                FlushPlain();
                spans.Add(new TokenSpan(ConsumeStringLiteral(code, ref i, code[i]), TokenKind.String));
                continue;
            }
            if (char.IsLetter(code[i]) || code[i] == '_')
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '-')) i++;
                var word = code[start..i];
                if (keywords.Contains(word))
                    spans.Add(new TokenSpan(word, TokenKind.Keyword));
                else
                    spans.Add(new TokenSpan(word, TokenKind.Plain));
                continue;
            }

            sb.Append(code[i]);
            i++;
        }

        FlushPlain();
        return spans;

        void FlushPlain()
        {
            if (sb.Length > 0)
            {
                spans.Add(new TokenSpan(sb.ToString(), TokenKind.Plain));
                sb.Clear();
            }
        }
    }

    private static IReadOnlyList<TokenSpan> ScanPowerShell(string code)
    {
        var spans = new List<TokenSpan>();
        var sb = new System.Text.StringBuilder();
        int i = 0;

        while (i < code.Length)
        {
            if (code[i] == '#')
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && code[i] != '\n') i++;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Comment));
                continue;
            }
            if (Match(code, i, "<#"))
            {
                FlushPlain();
                int start = i;
                i += 2;
                while (i < code.Length && !Match(code, i, "#>")) i++;
                if (i < code.Length) i += 2;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Comment));
                continue;
            }
            if (code[i] == '"' || code[i] == '\'')
            {
                FlushPlain();
                spans.Add(new TokenSpan(ConsumeStringLiteral(code, ref i, code[i]), TokenKind.String));
                continue;
            }
            if (char.IsDigit(code[i]))
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '.')) i++;
                spans.Add(new TokenSpan(code[start..i], TokenKind.Number));
                continue;
            }
            if (char.IsLetter(code[i]) || code[i] == '_' || code[i] == '-' || code[i] == '$')
            {
                FlushPlain();
                int start = i;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_' || code[i] == '-' || code[i] == '$')) i++;
                var word = code[start..i];
                if (PowerShellKeywords.Contains(word.TrimStart('-')))
                    spans.Add(new TokenSpan(word, TokenKind.Keyword));
                else
                    spans.Add(new TokenSpan(word, TokenKind.Plain));
                continue;
            }

            sb.Append(code[i]);
            i++;
        }

        FlushPlain();
        return spans;

        void FlushPlain()
        {
            if (sb.Length > 0)
            {
                spans.Add(new TokenSpan(sb.ToString(), TokenKind.Plain));
                sb.Clear();
            }
        }
    }

    /// <summary>Consume an unterminated-string-safe literal starting at <paramref name="i"/>.
    /// Handles backslash escapes; if the closing quote is missing (e.g. mid-stream
    /// rendering), consumes to EOL to avoid swallowing the rest of the code block.</summary>
    private static string ConsumeStringLiteral(string code, ref int i, char quote)
    {
        int start = i;
        i++; // opener
        while (i < code.Length && code[i] != quote)
        {
            if (code[i] == '\\' && i + 1 < code.Length) i += 2;
            else if (code[i] == '\n')
            {
                return code[start..i];
            }
            else i++;
        }
        if (i < code.Length) i++; // closer
        return code[start..i];
    }

    private static bool Match(string s, int start, string needle)
    {
        if (start + needle.Length > s.Length) return false;
        for (int k = 0; k < needle.Length; k++)
            if (s[start + k] != needle[k]) return false;
        return true;
    }
}
