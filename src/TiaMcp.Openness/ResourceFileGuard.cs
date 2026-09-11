using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TiaMcp.Openness;

/// <summary>
/// Detects the duplicate multilingual-text-ID defect found in Phase 0: TIA Portal's own
/// ExportAsDocuments can generate an .s7res file where two different comment/title texts
/// share the same generated "MLC_xxx" ID. Re-importing such a file fails with "The
/// resource file contains corrupted data". This only detects the problem - see
/// TiaConnection.WriteBlock for why automatic repair is deliberately not attempted.
/// </summary>
public static class ResourceFileGuard
{
    private static readonly Regex IdLine = new(@"^\s*-\s*id:\s*(\S+)\s*$", RegexOptions.Multiline);

    public static IReadOnlyList<string> FindDuplicateIds(string s7resContent)
    {
        var counts = new Dictionary<string, int>();
        foreach (Match m in IdLine.Matches(s7resContent))
        {
            var id = m.Groups[1].Value;
            counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
        }

        var duplicates = new List<string>();
        foreach (var kv in counts)
        {
            if (kv.Value > 1) duplicates.Add(kv.Key);
        }
        return duplicates;
    }
}
