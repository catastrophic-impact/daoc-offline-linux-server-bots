using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Daoc.WorldBuilder;

/// <summary>One "REPLACE/INSERT INTO `table` (`a`, `b`) VALUES (...), (...);" statement.</summary>
public sealed record DumpInsert(string Table, IReadOnlyList<string> Columns, List<object?[]> Rows);

/// <summary>
/// Reads the data statements of a MySQL/MariaDB dump (HeidiSQL/mysqldump style, as used by
/// OpenDAoC-Database). Schema statements are skipped: the schema comes from the server's own
/// DataObjects. Values become string, long, double or null.
/// </summary>
public sealed class MySqlDumpReader(string text)
{
    private int _pos;

    public IEnumerable<DumpInsert> ReadInserts()
    {
        while (true)
        {
            int start = NextInsert();
            if (start < 0)
                yield break;
            _pos = start;
            yield return ReadInsert();
        }
    }

    /// <summary>Position of the next INSERT/REPLACE keyword at statement start, skipping strings and comments.</summary>
    private int NextInsert()
    {
        while (_pos < text.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= text.Length)
                return -1;
            if (StartsWithWord("REPLACE INTO") || StartsWithWord("INSERT INTO") || StartsWithWord("INSERT IGNORE INTO"))
                return _pos;
            SkipStatement();
        }
        return -1;
    }

    private DumpInsert ReadInsert()
    {
        // keyword(s) up to INTO
        _pos = text.IndexOf("INTO", _pos, StringComparison.OrdinalIgnoreCase) + 4;
        SkipWhitespaceAndComments();
        string table = ReadIdentifier();
        SkipWhitespaceAndComments();

        var columns = new List<string>();
        if (Peek() == '(')
        {
            _pos++;
            while (true)
            {
                SkipWhitespaceAndComments();
                columns.Add(ReadIdentifier());
                SkipWhitespaceAndComments();
                char c = Next();
                if (c == ')') break;
                if (c != ',') throw Error($"expected , or ) in column list of {table}");
            }
            SkipWhitespaceAndComments();
        }
        if (!StartsWithWord("VALUES"))
            throw Error($"expected VALUES for {table}");
        _pos += "VALUES".Length;

        var rows = new List<object?[]>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (Next() != '(')
                throw Error($"expected ( in VALUES of {table}");
            var row = new List<object?>(columns.Count);
            while (true)
            {
                SkipWhitespaceAndComments();
                row.Add(ReadValue());
                SkipWhitespaceAndComments();
                char c = Next();
                if (c == ')') break;
                if (c != ',') throw Error($"expected , or ) in a row of {table}");
            }
            rows.Add(row.ToArray());
            SkipWhitespaceAndComments();
            char sep = Next();
            if (sep == ';') break;
            if (sep != ',') throw Error($"expected , or ; after a row of {table}");
        }
        return new DumpInsert(table, columns, rows);
    }

    private object? ReadValue()
    {
        char c = Peek();
        if (c == '\'')
            return ReadString();
        if (StartsWithWord("NULL"))
        {
            _pos += 4;
            return null;
        }
        if (c == '_' && StartsWithWord("_binary"))
        {
            _pos += "_binary".Length;
            SkipWhitespaceAndComments();
            return ReadString();
        }
        int start = _pos;
        while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '-' or '+' or '.'))
            _pos++;
        string token = text[start.._pos];
        if (token.Length == 0)
            throw Error("expected a value");
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
            return whole;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
            return real;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.FromHexString(token[2..]);
        throw Error($"unsupported value '{token}'");
    }

    private string ReadString()
    {
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= text.Length)
                throw Error("unterminated string");
            char c = text[_pos++];
            if (c == '\\')
            {
                char e = text[_pos++];
                sb.Append(e switch
                {
                    '0' => '\0',
                    'b' => '\b',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'Z' => (char)26,
                    _ => e, // \' \" \\ \% \_ and anything else: the character itself
                });
            }
            else if (c == '\'')
            {
                if (_pos < text.Length && text[_pos] == '\'') { sb.Append('\''); _pos++; }
                else return sb.ToString();
            }
            else
            {
                sb.Append(c);
            }
        }
    }

    private string ReadIdentifier()
    {
        if (Peek() == '`')
        {
            int end = text.IndexOf('`', _pos + 1);
            string id = text[(_pos + 1)..end];
            _pos = end + 1;
            return id;
        }
        int start = _pos;
        while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '.'))
            _pos++;
        return text[start.._pos];
    }

    /// <summary>Skips to just after the next top-level ';'.</summary>
    private void SkipStatement()
    {
        while (_pos < text.Length)
        {
            char c = text[_pos];
            if (c == '\'' ) { ReadString(); continue; }
            if (c == '`') { ReadIdentifier(); continue; }
            if (c == '/' && Peek(1) == '*') { SkipBlockComment(); continue; }
            if ((c == '-' && Peek(1) == '-') || c == '#') { SkipLine(); continue; }
            _pos++;
            if (c == ';') return;
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < text.Length)
        {
            char c = text[_pos];
            if (char.IsWhiteSpace(c)) { _pos++; continue; }
            // MySQL conditional comments (/*!40101 ... */) are settings; skip them like comments.
            if (c == '/' && Peek(1) == '*') { SkipBlockComment(); continue; }
            if ((c == '-' && Peek(1) == '-') || c == '#') { SkipLine(); continue; }
            return;
        }
    }

    private void SkipBlockComment()
    {
        int end = text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
        _pos = end < 0 ? text.Length : end + 2;
        // A conditional comment may be followed by its statement terminator.
        SkipSpaces();
        if (Peek() == ';') _pos++;
    }

    private void SkipLine()
    {
        int end = text.IndexOf('\n', _pos);
        _pos = end < 0 ? text.Length : end + 1;
    }

    private void SkipSpaces()
    {
        while (_pos < text.Length && text[_pos] is ' ' or '\t')
            _pos++;
    }

    private bool StartsWithWord(string word) =>
        string.Compare(text, _pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
        (_pos + word.Length >= text.Length || !char.IsLetterOrDigit(text[_pos + word.Length]));

    private char Peek(int offset = 0) => _pos + offset < text.Length ? text[_pos + offset] : '\0';

    private char Next() => _pos < text.Length ? text[_pos++] : throw Error("unexpected end of file");

    private FormatException Error(string message)
    {
        int line = 1;
        for (int i = 0; i < Math.Min(_pos, text.Length); i++)
            if (text[i] == '\n') line++;
        return new FormatException($"line {line}: {message}");
    }
}
