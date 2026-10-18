using System.Text.RegularExpressions;

namespace TiaMcp.Mcp;

/// <summary>The registered kind handlers. Adding an object type means adding a handler here.</summary>
internal static class SourceKinds
{
    // Per family in write order - what an item references is written before it: tags can be
    // UDT-typed, and blocks reference both UDTs and tags (a block imported before what it
    // references fails to import); the PLC alarm text lists' messages belong to the alarms the
    // blocks define. An HMI's screens use its tags (a classic HMI's also its text lists, templates and
    // pop-ups), and Unified alarms their alarm classes.
    public static IReadOnlyList<ISourceKind> All { get; } = new ISourceKind[]
    {
        new UdtKind(),
        new PlcTagTableKind(),
        new BlockKind(),
        new PlcAlarmTextListKind(),

        new HmiTagTableKind(),
        new HmiScreenKind(),
        HmiAlarmsKind.Classes,
        HmiAlarmsKind.Discrete,
        HmiAlarmsKind.Analog,

        HmiClassicKind.TextList,
        HmiClassicKind.TagTable,
        HmiClassicKind.ScreenTemplate,
        HmiClassicKind.PopupScreen,
        HmiClassicKind.Screen,
    };

    // The order of list_project and _export_summary.txt, which differs from the write order.
    private static readonly string[] ListOrder = { "block", "tag table", "udt", "alarm text list", "HMI tag table", "HMI text list", "HMI screen template", "HMI pop-up screen", "HMI screen", "HMI DiscreteAlarms", "HMI AnalogAlarms", "HMI AlarmClasses" };

    public static IEnumerable<ISourceKind> Of(DeviceFamily family) => All.Where(k => k.Family == family);

    // By name only: the kinds that share a name (classic and Unified HMI tag tables) share their
    // section and files too.
    public static ISourceKind? Find(string name) => All.FirstOrDefault(k => k.Name == name);

    public static bool IsFixed(string name) => Find(name)?.FixedName != null;

    public static int ListIndex(string name) => Array.IndexOf(ListOrder, name);

    // An item line as list_project and _export_summary.txt show it, with an optional status:
    // "OK     block 'Group/Name' [SCL]", "tag table 'Name'", "HMI DiscreteAlarms".
    public static Regex ListedItem { get; } = BuildListedItem();

    private static Regex BuildListedItem()
    {
        // Longest first, so "HMI screen template" isn't read as "HMI screen".
        string Alternatives(bool isFixed) => string.Join("|", All.Where(k => (k.FixedName != null) == isFixed)
            .Select(k => k.Name).Distinct().OrderByDescending(n => n.Length).Select(Regex.Escape));
        return new Regex($@"^(?:(?<status>OK|FAILED|STALE)\s+)?(?:(?<kind>{Alternatives(true)})\b|(?<kind>{Alternatives(false)}) '(?<label>[^']*)')", RegexOptions.Compiled);
    }
}
