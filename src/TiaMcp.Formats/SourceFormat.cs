namespace TiaMcp.Formats;

/// <summary>
/// The file formats an item can be read and written in, as list_project, the manifest and the
/// read calls name them. A kind's own format comes first; "xml" is the raw Openness export.
/// </summary>
public static class SourceFormat
{
    // TIA Portal's document format: '.s7dcl' plus '.s7res' (blocks, UDTs).
    public const string S7dcl = "s7dcl";

    // GRAPH blocks, converted from the raw XML (see GraphConverter).
    public const string GraphIL = "graph.il";

    // Whole-block STL, TIA Portal's 'Generate source' text.
    public const string Awl = "awl";

    // Tables, converted from the raw XML or the object model.
    public const string Csv = "csv";

    // Object models without an Openness export, as JSON (WinCC Unified screens, see ScreenJson).
    public const string Json = "json";

    // The raw SimaticML export, as Openness writes and imports it (without DocumentInfo).
    public const string Xml = "xml";

    public static readonly IReadOnlyList<string> All = new[] { S7dcl, GraphIL, Awl, Csv, Json, Xml };

    // The file extension of a format's main file.
    public static string Extension(string format) => "." + format;
}
