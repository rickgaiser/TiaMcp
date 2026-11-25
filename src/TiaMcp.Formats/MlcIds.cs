using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TiaMcp.Formats;

/// <summary>
/// The text ids ("MLC_xxx") that link a block's .s7dcl to the comment/title texts in its .s7res.
/// TIA Portal makes them up per import, so a block that is re-imported before every export (the
/// relay read of a block with embedded STL networks) gets different ids on every read, although
/// nothing changed. Renaming them after what they describe makes such a read deterministic, and
/// keeps a diff local when a network or member is added.
/// </summary>
public static class MlcIds
{
    // A reference in the .s7dcl: S7_MLC, S7_NetworkTitle, S7_NetworkComment, S7_BlockTitle, ...
    private static readonly Regex Reference = new(@"(\b(S7_[A-Za-z]+)\s*:=\s*"")(MLC_[^""]+)("")");

    // An entry in the .s7res: "  - id: MLC_xxx".
    private static readonly Regex Definition = new(@"^(\s*-\s*id:\s*)(MLC_\S+)(\s*)$", RegexOptions.Multiline);

    private static readonly Regex NetworkStart = new(@"^\s*NETWORK\b", RegexOptions.Multiline);

    // A member name, plain or quoted, at the start of the text.
    private static readonly Regex LeadingName = new(@"^\s*(""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)");

    // Renames every id after what it describes: MLC_title / MLC_comment for the block,
    // MLC_net<N>t / MLC_net<N>c for the title and comment of network N (as TIA Portal numbers
    // them), MLC_<member> for a member, with _2, _3, ... when a name comes up again (e.g. the same
    // member name in two structs). An id only defined in the .s7res becomes MLC_unused<N>. Texts
    // and structure are untouched, and an id TIA Portal gave to two texts (see ResourceFileGuard)
    // stays shared - renaming neither hides nor repairs that.
    public static (string Dcl, string? Res) Renumber(string dcl, string? res)
    {
        var map = new Dictionary<string, string>();
        var used = new HashSet<string>();
        string Unique(string id)
        {
            var candidate = id;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{id}_{n}";
            return candidate;
        }

        foreach (Match m in Reference.Matches(dcl))
        {
            var id = m.Groups[3].Value;
            if (!map.ContainsKey(id)) map[id] = Unique(NameFor(dcl, m.Index, m.Groups[2].Value));
        }
        if (res != null)
        {
            var unused = 0;
            foreach (Match m in Definition.Matches(res))
            {
                var id = m.Groups[2].Value;
                if (!map.ContainsKey(id)) map[id] = Unique($"MLC_unused{++unused}");
            }
        }

        return (Reference.Replace(dcl, m => m.Groups[1].Value + map[m.Groups[3].Value] + m.Groups[4].Value),
                res == null ? null : Definition.Replace(res, m => m.Groups[1].Value + map[m.Groups[2].Value] + m.Groups[3].Value));
    }

    // What the attribute block holding the reference at position (an attribute) applies to.
    private static string NameFor(string dcl, int position, string attribute)
    {
        switch (attribute)
        {
            case "S7_BlockTitle": return "MLC_title";
            case "S7_BlockComment": return "MLC_comment";
            case "S7_NetworkTitle":
            case "S7_NetworkComment":
                var network = NetworkStart.Matches(dcl.Substring(0, position)).Count + 1;
                return $"MLC_net{network}{(attribute == "S7_NetworkTitle" ? "t" : "c")}";
        }

        // A member: 'name { S7_MLC := ... } : Type' names it before the attribute block, on the
        // same line; the multi-line form '{ ... }' on lines of their own is followed by it.
        string? name = null;
        var open = dcl.LastIndexOf('{', position);
        if (open >= 0)
        {
            var lineStart = dcl.LastIndexOf('\n', open) + 1;
            var before = LeadingName.Match(dcl.Substring(lineStart, open - lineStart));
            if (before.Success) name = before.Groups[1].Value;
        }
        var close = dcl.IndexOf('}', position);
        if (name == null && close >= 0)
        {
            var after = LeadingName.Match(dcl.Substring(close + 1));
            if (after.Success) name = after.Groups[1].Value;
        }
        return name == null ? "MLC_" + attribute.Substring("S7_".Length) : "MLC_" + Regex.Replace(name.Trim('"'), "[^A-Za-z0-9_]", "_");
    }
}
