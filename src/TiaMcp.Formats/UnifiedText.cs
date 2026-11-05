using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaMcp.Formats;

/// <summary>
/// WinCC Unified texts (screen item texts, alarm event/info texts) are stored as an HTML fragment
/// "&lt;body&gt;&lt;p&gt;...&lt;/p&gt;&lt;/body&gt;", line breaks as &lt;br /&gt;. Plain paragraphs are shown as plain text (lines
/// joined with \n); text with any other markup (formatting spans) stays raw, so a round trip keeps it.
/// </summary>
public static class UnifiedText
{
    private static readonly Regex SimpleHtml = new(@"^<body>((?:<p>.*?</p>)*)</body>$", RegexOptions.Singleline);
    private static readonly Regex Paragraph = new(@"<p>(.*?)</p>", RegexOptions.Singleline);
    private static readonly Regex LineBreak = new(@"<br\s*/?>");

    // One level: the plain text of a simple fragment, else the value unchanged.
    public static string HtmlToPlain(string raw)
    {
        var match = SimpleHtml.Match(raw.Replace("<p/>", "<p></p>"));
        if (!match.Success) return raw;
        var paragraphs = Paragraph.Matches(match.Groups[1].Value).Cast<Match>().Select(m => LineBreak.Replace(m.Groups[1].Value, "\n")).ToList();
        if (paragraphs.Any(p => p.Contains('<'))) return raw;
        return string.Join("\n", paragraphs.Select(p => p.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")));
    }

    public static string PlainToHtml(string value) =>
        "<body><p>" + string.Join("<br />", value.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"))) + "</p></body>";

    // As HtmlToPlain, but also undoes the damage of earlier TiaMcp versions, which wrote a read
    // fragment back as plain text, wrapping it once more per round trip - and sometimes several
    // languages joined with " | " ("<body><p>A</p></body> | <body><p/></body>"): the fragment is
    // unwrapped as long as it holds another one, and of joined fragments the first non-empty is kept.
    public static string Repair(string raw)
    {
        var text = HtmlToPlain(raw);
        for (var depth = 0; depth < 32; depth++)
        {
            var next = Joined(text) ?? HtmlToPlain(text);
            if (next == text) break;
            text = next;
        }
        return text;
    }

    private static string? Joined(string text)
    {
        if (!text.StartsWith("<body", StringComparison.Ordinal) || !text.Contains(" | ")) return null;
        var parts = text.Split(new[] { " | " }, StringSplitOptions.None);
        if (!parts.All(p => p.StartsWith("<body", StringComparison.Ordinal) && p.EndsWith("</body>", StringComparison.Ordinal))) return null;
        var plain = parts.Select(Repair).ToList();
        return plain.FirstOrDefault(p => p.Length > 0) ?? "";
    }
}
