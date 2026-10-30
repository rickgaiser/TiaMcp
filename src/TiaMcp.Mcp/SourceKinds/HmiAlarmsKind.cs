using TiaMcp.Openness;

namespace TiaMcp.Mcp;

// WinCC Unified alarms: three fixed files per HMI ('HMI alarms/DiscreteAlarms.csv', AnalogAlarms.csv,
// AlarmClasses.csv, see HmiAlarmsCsv) - alarms and alarm classes have no groups in Openness. Each
// file is one item with an empty name.
internal sealed class HmiAlarmsKind : ISourceKind
{
    public static readonly HmiAlarmsKind Classes = new("AlarmClasses", null);
    public static readonly HmiAlarmsKind Discrete = new("DiscreteAlarms", "Discrete");
    public static readonly HmiAlarmsKind Analog = new("AnalogAlarms", "Analog");

    // alarmType: "Discrete"/"Analog" as Openness names it, null for the alarm classes.
    private readonly string? _alarmType;

    private HmiAlarmsKind(string fileName, string? alarmType)
    {
        FixedName = fileName;
        _alarmType = alarmType;
    }

    public string Name => "HMI " + FixedName;
    public string Section => "HMI alarms";
    public DeviceFamily Family => DeviceFamily.HmiUnified;
    public string? FixedName { get; }
    public bool XmlNeedsCompile => false;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Csv };

    public Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string hmi) =>
        Task.FromResult<IReadOnlyList<ListedEntry>>(new[] { new ListedEntry(Section, FixedName!, Name, Formats) });

    public async Task Export(TiaSession session, string hmi, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        if (formats("", "") == null) return;
        try
        {
            var csv = _alarmType == null
                ? HmiAlarmsCsv.FormatClasses(await session.ListHmiAlarmClassesAsync(hmi))
                : HmiAlarmsCsv.FormatAlarms(await session.ListHmiAlarmsAsync(hmi, _alarmType));
            items.Add(ExportedItem.FromCsv(hmi, Name, "", "", csv, null));
        }
        catch (Exception ex)
        {
            items.Add(ExportedItem.FromCsv(hmi, Name, "", "", null, ex.Message));
        }
    }

    // Alarms/alarm classes are always upsert-by-name (see TiaConnection.WriteHmiAlarm/WriteHmiAlarmClass),
    // so createMissing/overwriteExisting are applied per row here rather than per file, unlike
    // every other kind where one item is one write call. A removed row is not deleted. The file
    // counts as written only if every row was - see write_source_tree's refresh.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var session = context.Session;
        var hmi = context.Device;
        var file = Path.Combine(context.DeviceDir, Section, FixedName + ".csv");
        if (!File.Exists(file)) { log.Failed.Add($"{Name}: no .csv file found at {file}"); return log; }

        if (_alarmType == null)
        {
            HmiAlarmClassSpec[] classes;
            try { classes = HmiAlarmsCsv.ParseClasses(File.ReadAllText(file)); }
            catch (FormatException ex) { log.Failed.Add($"{Name}: {ex.Message}"); return log; }

            var existing = (await session.ListHmiAlarmClassesAsync(hmi)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in classes)
            {
                var label = $"HMI alarm class '{spec.Name}'";
                var exists = existing.Contains(spec.Name);
                if (!Allowed(context, log, label, exists)) continue;
                var result = await session.WriteHmiAlarmClassAsync(hmi, spec);
                if (result.Success) (exists ? log.Updated : log.Created).Add(label);
                else log.Failed.Add($"{label}: {result.Error}");
            }
        }
        else
        {
            HmiAlarmSpec[] alarms;
            try { alarms = HmiAlarmsCsv.ParseAlarms(File.ReadAllText(file), _alarmType); }
            catch (FormatException ex) { log.Failed.Add($"{Name}: {ex.Message}"); return log; }

            var existing = (await session.ListHmiAlarmsAsync(hmi, _alarmType)).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in alarms)
            {
                var label = $"HMI alarm '{spec.Name}'";
                var exists = existing.Contains(spec.Name);
                if (!Allowed(context, log, label, exists)) continue;
                var result = await session.WriteHmiAlarmAsync(hmi, spec);
                if (result.Success) (exists ? log.Updated : log.Created).Add(label);
                else log.Failed.Add($"{label}: {result.Error}");
            }
        }

        if (!context.DryRun && log.Failed.Count == 0) context.Written.AddRange(items);
        return log;
    }

    // Whether a row is to be written: false (and logged) when createMissing/overwriteExisting rule
    // it out or this is a dry run.
    private static bool Allowed(WriteContext context, WriteLog log, string label, bool exists)
    {
        if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); return false; }
        if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); return false; }
        if (context.DryRun) { (exists ? log.Updated : log.Created).Add($"{label} (dry run)"); return false; }
        return true;
    }
}
