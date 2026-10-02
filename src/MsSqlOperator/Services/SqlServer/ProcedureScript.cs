using System.Security.Cryptography;
using System.Text;

namespace MsSqlOperator.Services.SqlServer;

/// <summary>One stored procedure ready to deploy: its batch and the session settings it was written for.</summary>
/// <param name="Schema">Schema of the procedure (dbo when the name is not qualified).</param>
/// <param name="Name">Procedure name.</param>
/// <param name="Batch">The <c>CREATE [OR ALTER] PROCEDURE</c> batch, executed alone.</param>
/// <param name="AnsiNulls">SET ANSI_NULLS in effect for this procedure in its script (default ON).</param>
/// <param name="QuotedIdentifier">SET QUOTED_IDENTIFIER in effect for this procedure (default ON).</param>
public sealed record ProcedureDefinition(string Schema, string Name, string Batch, bool AnsiNulls = true, bool QuotedIdentifier = true)
{
    /// <summary>The SET batch run right before <see cref="Batch"/>: both settings are stored with the
    /// procedure, so they are always stated instead of inherited from whatever ran before.</summary>
    public string SettingsBatch => $"SET ANSI_NULLS {(AnsiNulls ? "ON" : "OFF")}; SET QUOTED_IDENTIFIER {(QuotedIdentifier ? "ON" : "OFF")};";

    /// <summary>Content hash of what gets deployed (settings + batch), line endings normalized.</summary>
    public string Hash
    {
        get
        {
            var normalized = $"{SettingsBatch}\n{Batch.Trim().ReplaceLineEndings("\n")}";
            return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))}";
        }
    }
}

/// <summary>
/// Turns a procedure script into deployable definitions, refusing anything else.
///
/// A script is split into batches on <c>GO</c> lines, as sqlcmd and SSMS do. Every batch must then be
/// either a <c>CREATE [OR ALTER] PROC[EDURE]</c> / <c>ALTER PROC[EDURE]</c> — SQL Server requires it
/// to be the first statement of its batch, so the rest of the batch is the procedure body and is not
/// executed at deployment — or a bare <c>SET ANSI_NULLS</c> / <c>SET QUOTED_IDENTIFIER</c>. The
/// definition used to be executed verbatim as one batch: <c>EXEC sp_configure …; ALTER SERVER ROLE
/// sysadmin ADD MEMBER …</c> ran as is, as sa with <c>deployAs: Sa</c>, and as arbitrary DDL
/// (<c>DROP TABLE</c>) on every selected database otherwise. Scripts with <c>GO</c>, the usual form in
/// Git repositories, failed instead.
/// </summary>
public static class ProcedureScript
{
    /// <summary>Parses <paramref name="script"/>; throws <see cref="ProcedureScriptException"/> on
    /// anything that is not a procedure definition.</summary>
    public static List<ProcedureDefinition> Parse(string script)
    {
        var definitions = new List<ProcedureDefinition>();
        var ansiNulls = true;
        var quotedIdentifier = true;
        var index = 0;

        foreach (var batch in SplitBatches(script))
        {
            index++;
            var tokens = Tokenize(batch, maxTokens: 12);
            if (tokens.Count == 0)
                continue; // only whitespace and comments

            if (IsSetBatch(tokens, out var option, out var on))
            {
                if (option == "ANSI_NULLS") ansiNulls = on;
                else quotedIdentifier = on;
                continue;
            }

            var nameStart = ProcedureNameStart(tokens);
            if (nameStart < 0)
                throw new ProcedureScriptException(
                    $"batch {index} is not a stored procedure definition: every batch must start with CREATE [OR ALTER] PROCEDURE " +
                    "(or be a bare SET ANSI_NULLS / SET QUOTED_IDENTIFIER). Other statements are refused.");

            var (schema, name) = ParseName(tokens, nameStart)
                ?? throw new ProcedureScriptException($"batch {index}: cannot read the procedure name.");
            definitions.Add(new ProcedureDefinition(schema, name, batch.Trim(), ansiNulls, quotedIdentifier));
        }

        if (definitions.Count == 0)
            throw new ProcedureScriptException("the script defines no stored procedure.");
        return definitions;
    }

    /// <summary>
    /// Splits on lines holding only <c>GO</c> (case-insensitive, optional trailing comment), outside
    /// comments and string literals. <c>GO n</c> (repeat count) is refused: re-running a procedure
    /// definition makes no sense.
    /// </summary>
    internal static List<string> SplitBatches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var state = LexState.Normal;
        var commentDepth = 0;

        foreach (var rawLine in script.ReplaceLineEndings("\n").Split('\n'))
        {
            if (state == LexState.Normal && IsGoLine(rawLine, out var repeat))
            {
                if (repeat)
                    throw new ProcedureScriptException("'GO <count>' is not supported in a procedure script.");
                batches.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(rawLine).Append('\n');
            (state, commentDepth) = Advance(rawLine, state, commentDepth);
        }

        if (state is LexState.String or LexState.QuotedIdentifier or LexState.Bracket)
            throw new ProcedureScriptException("unterminated string literal or quoted identifier.");
        batches.Add(current.ToString());
        return batches;
    }

    private enum LexState { Normal, BlockComment, String, QuotedIdentifier, Bracket }

    private static bool IsGoLine(string line, out bool repeat)
    {
        repeat = false;
        var trimmed = line.Trim();
        var comment = trimmed.IndexOf("--", StringComparison.Ordinal);
        if (comment >= 0)
            trimmed = trimmed[..comment].TrimEnd();
        if (trimmed.Length < 2 || !trimmed.StartsWith("GO", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = trimmed[2..].Trim();
        if (rest.Length == 0)
            return true;
        if (rest.All(char.IsAsciiDigit) && trimmed[2] is ' ' or '\t')
        {
            repeat = true;
            return true;
        }

        return false;
    }

    /// <summary>Lexer state after <paramref name="line"/> (a line comment ends with the line; T-SQL
    /// block comments nest).</summary>
    private static (LexState, int) Advance(string line, LexState state, int depth)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';
            switch (state)
            {
                case LexState.Normal:
                    if (c == '-' && next == '-') return (LexState.Normal, 0);
                    if (c == '/' && next == '*') { state = LexState.BlockComment; depth = 1; i++; }
                    else if (c == '\'') state = LexState.String;
                    else if (c == '"') state = LexState.QuotedIdentifier;
                    else if (c == '[') state = LexState.Bracket;
                    break;
                case LexState.BlockComment:
                    if (c == '/' && next == '*') { depth++; i++; }
                    else if (c == '*' && next == '/') { depth--; i++; if (depth == 0) state = LexState.Normal; }
                    break;
                case LexState.String:
                    if (c == '\'') { if (next == '\'') i++; else state = LexState.Normal; }
                    break;
                case LexState.QuotedIdentifier:
                    if (c == '"') { if (next == '"') i++; else state = LexState.Normal; }
                    break;
                case LexState.Bracket:
                    if (c == ']') { if (next == ']') i++; else state = LexState.Normal; }
                    break;
            }
        }

        return (state, depth);
    }

    /// <summary>The first significant tokens of a batch, comments skipped: words, bracketed or
    /// double-quoted identifiers (with their delimiters), and single punctuation characters.</summary>
    internal static List<string> Tokenize(string batch, int maxTokens)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < batch.Length && tokens.Count < maxTokens)
        {
            var c = batch[i];
            var next = i + 1 < batch.Length ? batch[i + 1] : '\0';
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && next == '-')
            {
                var eol = batch.IndexOf('\n', i);
                i = eol < 0 ? batch.Length : eol + 1;
                continue;
            }

            if (c == '/' && next == '*')
            {
                var depth = 1;
                i += 2;
                while (i < batch.Length && depth > 0)
                {
                    if (batch[i] == '/' && i + 1 < batch.Length && batch[i + 1] == '*') { depth++; i += 2; }
                    else if (batch[i] == '*' && i + 1 < batch.Length && batch[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }

                continue;
            }

            if (c is '[' or '"')
            {
                var close = c == '[' ? ']' : '"';
                var start = i++;
                while (i < batch.Length)
                {
                    if (batch[i] == close)
                    {
                        if (i + 1 < batch.Length && batch[i + 1] == close) { i += 2; continue; }
                        i++;
                        break;
                    }

                    i++;
                }

                tokens.Add(batch[start..i]);
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$')
            {
                var start = i;
                while (i < batch.Length && (char.IsLetterOrDigit(batch[i]) || batch[i] is '_' or '#' or '@' or '$'))
                    i++;
                tokens.Add(batch[start..i]);
                continue;
            }

            tokens.Add(c.ToString());
            i++;
        }

        return tokens;
    }

    private static bool IsSetBatch(List<string> tokens, out string option, out bool on)
    {
        option = string.Empty;
        on = true;
        var count = tokens.Count > 0 && tokens[^1] == ";" ? tokens.Count - 1 : tokens.Count;
        if (count != 3 || !Is(tokens[0], "SET"))
            return false;
        option = tokens[1].ToUpperInvariant();
        if (option is not ("ANSI_NULLS" or "QUOTED_IDENTIFIER"))
            return false;
        if (Is(tokens[2], "ON")) return true;
        if (!Is(tokens[2], "OFF")) return false;
        on = false;
        return true;
    }

    /// <summary>Index of the token after <c>PROC[EDURE]</c> when the batch starts with
    /// CREATE [OR ALTER] PROC[EDURE] or ALTER PROC[EDURE]; -1 otherwise.</summary>
    private static int ProcedureNameStart(List<string> tokens)
    {
        var i = 0;
        if (tokens.Count > 2 && Is(tokens[0], "CREATE") && Is(tokens[1], "OR") && Is(tokens[2], "ALTER"))
            i = 3;
        else if (tokens.Count > 0 && (Is(tokens[0], "CREATE") || Is(tokens[0], "ALTER")))
            i = 1;
        else
            return -1;

        return i < tokens.Count && (Is(tokens[i], "PROC") || Is(tokens[i], "PROCEDURE")) ? i + 1 : -1;
    }

    private static (string Schema, string Name)? ParseName(List<string> tokens, int start)
    {
        if (start >= tokens.Count || !IsIdentifier(tokens[start]))
            return null;
        if (start + 2 < tokens.Count && tokens[start + 1] == "." && IsIdentifier(tokens[start + 2]))
            return (Unquote(tokens[start]), Unquote(tokens[start + 2]));
        return ("dbo", Unquote(tokens[start]));
    }

    private static bool IsIdentifier(string token)
        => token.Length > 0 && (token[0] is '[' or '"' || char.IsLetter(token[0]) || token[0] is '_' or '#' or '@');

    private static string Unquote(string token)
        => token switch
        {
            ['[', .. var inner, ']'] => new string(inner).Replace("]]", "]"),
            ['"', .. var inner, '"'] => new string(inner).Replace("\"\"", "\""),
            _ => token,
        };

    private static bool Is(string token, string keyword) => string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A procedure script that is not made only of procedure definitions.</summary>
public sealed class ProcedureScriptException(string message) : Exception($"Invalid procedure script: {message}");
