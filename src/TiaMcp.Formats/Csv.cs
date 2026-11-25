using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TiaMcp.Formats;

/// <summary>
/// RFC 4180 CSV as Excel expects it when a .csv is opened directly rather than through its
/// text-import wizard: comma separated, CRLF line endings, a header row. The UTF-8 BOM is added
/// when the file is written, not here.
/// </summary>
public static class Csv
{
    public static string Format(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(Field))).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(Field))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Field(string? value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    // Reverse of Format: header row used as each returned row's keys.
    public static List<Dictionary<string, string>> ParseRecords(string text)
    {
        var rows = ParseRows(text);
        if (rows.Count == 0) return new List<Dictionary<string, string>>();

        var headers = rows[0];
        var result = new List<Dictionary<string, string>>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var dict = new Dictionary<string, string>();
            for (var c = 0; c < headers.Count; c++)
                dict[headers[c]] = c < row.Count ? row[c] : "";
            result.Add(dict);
        }
        return result;
    }

    // Reverse of Format, without interpreting the header: CRLF or LF line endings, blank lines
    // dropped. A leading UTF-8 BOM (if the caller didn't strip it) is ignored.
    public static List<List<string>> ParseRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; i++; continue;
                case ',': row.Add(field.ToString()); field.Clear(); i++; continue;
                case '\r': i++; continue;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>();
                    i++; continue;
                default: field.Append(c); i++; continue;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows.Where(r => r.Count > 1 || r[0].Length > 0).ToList();
    }

    // net48's Dictionary<TKey, TValue> has no GetValueOrDefault.
    internal static string? Get(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var v) ? v : null;

    // An empty cell means "leave unchanged" on import.
    internal static string? GetOptional(Dictionary<string, string> row, string key)
    {
        var value = Get(row, key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static bool? GetBool(Dictionary<string, string> row, string column, int line)
    {
        var value = Get(row, column)?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value == "1" || value!.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new FormatException($"CSV line {line}: {column} must be True/False/1/0, got '{value}'.");
    }
}
