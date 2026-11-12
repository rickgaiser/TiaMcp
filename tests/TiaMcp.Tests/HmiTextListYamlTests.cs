using System;
using System.Linq;

namespace TiaMcp.Tests;

public class HmiTextListYamlTests
{
    // Made up, in the form TIA Portal V21 exports WinCC Unified text lists.
    private const string Lists =
        "#Version: 2.0\r\n\r\nTextListContainers:\r\n  DeviceTextList:\r\n    ResourceListType: TextList\r\n    ResourceLists:\r\n" +
        "      PumpStates:\r\n        Entries:\r\n" +
        "          Text_list_entry_0:\r\n            Text: MyTextLibrary.Text_0\r\n" +
        "          Text_list_entry_1:\r\n            Type: Range\r\n            Value: 1\r\n            FromValue: 1\r\n            ToValue: 5\r\n            Text: MyTextLibrary.Text_1\r\n" +
        "      ValveStates:\r\n        Entries:\r\n" +
        "          Text_list_entry_0:\r\n            Value: 10\r\n            FromValue: 10\r\n            ToValue: 10\r\n            Text: MyTextLibrary.Text_2\r\n";

    private const string Library =
        "#Version: 2.0\r\n\r\nTextLibraries:\r\n  MyTextLibrary:\r\n    Type: Text\r\n    Languages:\r\n    - en-US\r\n    - de-DE\r\n    DefaultLanguage: en-US\r\n    Entries:\r\n" +
        "      Text_0:\r\n        Text:\r\n        - Stopped\r\n        - Gestoppt\r\n" +
        "      Text_1:\r\n        Text:\r\n        - 'Running: fast'\r\n        - ''\r\n" +
        "      Text_2:\r\n        Text:\r\n        - Open\r\n        - \"Line\\nbreak\"\r\n";

    [Fact]
    public void Parse_Emit_RoundTripsTheExport()
    {
        Assert.Equal(Lists, SimpleYaml.Emit(SimpleYaml.Parse(Lists)));
        Assert.Equal(Library, SimpleYaml.Emit(SimpleYaml.Parse(Library)));
    }

    [Fact]
    public void Read_ResolvesTextsPerLanguage()
    {
        var lists = HmiTextListYaml.Read(Lists, Library);

        Assert.Equal(new[] { "PumpStates", "ValveStates" }, lists.Select(l => l.Name));
        Assert.Equal(new[] { "en-US", "de-DE" }, lists[0].Languages);
        var running = lists[0].Entries[1];
        Assert.Equal(("1", "5"), (running.From, running.To));
        Assert.Equal(new[] { "Running: fast", "" }, running.Texts);
        Assert.Equal("Line\nbreak", lists[1].Entries[0].Texts[1]);
    }

    [Fact]
    public void Patch_Unchanged_GivesTheExportBack()
    {
        var lists = HmiTextListYaml.Read(Lists, Library);

        var patch = HmiTextListYaml.Patch(Lists, Library, lists, deleteMissing: true);

        Assert.All(patch.Changes, c => Assert.False(c.Any));
        Assert.Equal(Lists, patch.ListsYaml);
        Assert.Equal(Library, patch.LibraryYaml);
    }

    [Fact]
    public void Patch_ChangesAddsAndDeletesEntries()
    {
        var edited = new PlcAlarmTextList("PumpStates", new[] { "en-US" }, new[]
        {
            new PlcAlarmTextEntry("0", "0", new[] { "Off" }),
            new PlcAlarmTextEntry("7", "7", new[] { "Fault #1" }),
            new PlcAlarmTextEntry("20", "29", new[] { "Range" }),
        });

        var patch = HmiTextListYaml.Patch(Lists, Library, new[] { edited }, deleteMissing: true);

        var change = Assert.Single(patch.Changes);
        Assert.Single(change.Changed);
        Assert.Equal(2, change.Added.Count);
        Assert.Contains("            Type: Range\r\n            Value: 20\r\n            FromValue: 20\r\n            ToValue: 29\r\n", patch.ListsYaml);
        Assert.Single(change.Deleted);
        var back = HmiTextListYaml.Read(patch.ListsYaml, patch.LibraryYaml);
        var pump = back.Single(l => l.Name == "PumpStates");
        Assert.Equal(new[] { "0", "7", "20" }, pump.Entries.Select(e => e.From));
        Assert.Equal(new[] { "Off", "Gestoppt" }, pump.Entries[0].Texts);      // a language not given keeps its text
        Assert.Equal(new[] { "Fault #1", "" }, pump.Entries[1].Texts);
        Assert.Equal("Open", back.Single(l => l.Name == "ValveStates").Entries[0].Texts[0]);
        Assert.DoesNotContain("Running", patch.LibraryYaml);                   // the deleted entry's text is gone
        Assert.Contains("- 'Fault #1'", patch.LibraryYaml);
        Assert.Contains("Text: MyTextLibrary.Text_3", patch.ListsYaml);
    }

    [Fact]
    public void Patch_WithoutDeleteMissing_KeepsEntriesNotGiven()
    {
        var edited = new PlcAlarmTextList("PumpStates", new[] { "en-US" }, new[] { new PlcAlarmTextEntry("9", "9", new[] { "New" }) });

        var patch = HmiTextListYaml.Patch(Lists, Library, new[] { edited }, deleteMissing: false);

        var pump = HmiTextListYaml.Read(patch.ListsYaml, patch.LibraryYaml).Single(l => l.Name == "PumpStates");
        Assert.Equal(new[] { "9", "0", "1" }, pump.Entries.Select(e => e.From));
    }

    [Fact]
    public void Patch_AddsANewList()
    {
        var created = new PlcAlarmTextList("Motor states", new[] { "en-US", "de-DE" }, new[]
        {
            new PlcAlarmTextEntry("0", "0", new[] { "Idle", "Bereit" }),
            new PlcAlarmTextEntry("1", "1", new[] { "true", "x" }),
        });

        var patch = HmiTextListYaml.Patch(Lists, Library, new[] { created }, deleteMissing: true);

        Assert.True(patch.Changes[0].IsNew);
        Assert.Contains("      Motor states:\r\n        Entries:\r\n          Text_list_entry_0:\r\n            Text: MyTextLibrary.Text_3\r\n" +
            "          Text_list_entry_1:\r\n            Value: 1\r\n            FromValue: 1\r\n            ToValue: 1\r\n", patch.ListsYaml);
        Assert.Contains("- 'true'", patch.LibraryYaml);
        var back = HmiTextListYaml.Read(patch.ListsYaml, patch.LibraryYaml);
        Assert.Equal(new[] { "PumpStates", "ValveStates", "Motor states" }, back.Select(l => l.Name));
        Assert.Equal(new[] { "true", "x" }, back[2].Entries[1].Texts);
    }

    [Fact]
    public void Patch_RefusesAnUnknownLanguage()
    {
        var list = new PlcAlarmTextList("PumpStates", new[] { "fr-FR" }, new[] { new PlcAlarmTextEntry("0", "0", new[] { "Arrêt" }) });

        var ex = Assert.Throws<FormatException>(() => HmiTextListYaml.Patch(Lists, Library, new[] { list }, deleteMissing: true));
        Assert.Contains("fr-FR", ex.Message);
    }

    [Fact]
    public void Patch_RefusesAnExportItCannotWriteBackUnchanged()
    {
        var odd = Library.Replace("    - en-US\r\n", "    -   en-US\r\n");

        var ex = Assert.Throws<FormatException>(() => HmiTextListYaml.Patch(Lists, odd, Array.Empty<PlcAlarmTextList>(), deleteMissing: true));
        Assert.Contains("can't write back unchanged", ex.Message);
    }

    // PumpStates with a default entry next to the entry for value 0, as TIA Portal exports it.
    private static readonly string ListsWithDefault = Lists.Replace(
        "          Text_list_entry_1:\r\n            Type: Range",
        "          Text_list_entry_1:\r\n            IsDefaultEntry: True\r\n            Text: MyTextLibrary.Text_3\r\n" +
        "          Text_list_entry_2:\r\n            Type: Range");

    private static readonly string LibraryWithDefault = Library +
        "      Text_3:\r\n        Text:\r\n        - Unknown\r\n        - Unbekannt\r\n";

    [Fact]
    public void Read_KeepsTheDefaultEntryApartFromValue0()
    {
        var pump = HmiTextListYaml.Read(ListsWithDefault, LibraryWithDefault)[0];

        Assert.Equal(new[] { ("0", "0"), ("Default", ""), ("1", "5") }, pump.Entries.Select(e => (e.From, e.To)));
        Assert.Equal(new[] { "Unknown", "Unbekannt" }, pump.Entries[1].Texts);
        var csv = AlarmTextListCsv.Format(pump);
        Assert.Contains("Default,,Unknown,Unbekannt", csv);
        Assert.Equal(pump.Entries.Select(e => (e.From, e.To)), AlarmTextListCsv.Parse("PumpStates", csv, allowDefault: true).Entries.Select(e => (e.From, e.To)));
    }

    [Fact]
    public void Patch_WithADefaultEntry_UnchangedGivesTheExportBack()
    {
        var lists = HmiTextListYaml.Read(ListsWithDefault, LibraryWithDefault);

        var patch = HmiTextListYaml.Patch(ListsWithDefault, LibraryWithDefault, lists, deleteMissing: true);

        Assert.All(patch.Changes, c => Assert.False(c.Any));
        Assert.Equal(ListsWithDefault, patch.ListsYaml);
    }

    [Fact]
    public void Patch_AddsAndChangesTheDefaultEntry()
    {
        var added = new PlcAlarmTextList("ValveStates", new[] { "en-US" }, new[]
        {
            new PlcAlarmTextEntry("10", "10", new[] { "Open" }),
            new PlcAlarmTextEntry("Default", "", new[] { "Unknown" }),
        });
        var patch = HmiTextListYaml.Patch(Lists, Library, new[] { added }, deleteMissing: true);

        Assert.Equal("Default: [en-US] Unknown | [de-DE] ", Assert.Single(patch.Changes[0].Added));
        Assert.Contains("          Text_list_entry_1:\r\n            IsDefaultEntry: True\r\n            Text: MyTextLibrary.Text_3\r\n", patch.ListsYaml);
        var valve = HmiTextListYaml.Read(patch.ListsYaml, patch.LibraryYaml).Single(l => l.Name == "ValveStates");
        Assert.Equal(("Default", "Unknown"), (valve.Entries[1].From, valve.Entries[1].Texts[0]));

        var changed = new PlcAlarmTextList("PumpStates", new[] { "en-US" }, new[]
        {
            new PlcAlarmTextEntry("0", "0", new[] { "Stopped" }),
            new PlcAlarmTextEntry("Default", "", new[] { "Other" }),
            new PlcAlarmTextEntry("1", "5", new[] { "Running: fast" }),
        });
        var patch2 = HmiTextListYaml.Patch(ListsWithDefault, LibraryWithDefault, new[] { changed }, deleteMissing: true);
        Assert.Equal("Default: [en-US] 'Unknown' -> 'Other'", Assert.Single(patch2.Changes[0].Changed));
        Assert.Empty(patch2.Changes[0].Added);
        Assert.Empty(patch2.Changes[0].Deleted);
    }

    [Theory]
    [InlineData("From,To,Text [en-US]\r\nDefault,,a\r\ndefault,,b\r\n", "default entry appears 2 times")]
    [InlineData("From,To,Text [en-US]\r\nDefault,3,a\r\n", "has no To")]
    [InlineData("From,To,Text [en-US]\r\nOther,,a\r\n", "not a number or 'Default'")]
    public void Csv_RefusesABadDefaultRow(string csv, string message)
    {
        var ex = Assert.Throws<FormatException>(() => AlarmTextListCsv.Parse("L", csv, allowDefault: true));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Csv_DefaultRowOnlyWhereAllowed()
    {
        Assert.Throws<FormatException>(() => AlarmTextListCsv.Parse("L", "From,To,Text [en-US]\r\nDefault,,a\r\n"));
    }

    [Theory]
    [InlineData("key: |\n  block\n")]
    [InlineData("key: &a value\n")]
    [InlineData("key:\n- a: b\n")]
    [InlineData("key: 'not closed\n")]
    public void Parse_RefusesYamlItDoesNotRead(string yaml)
    {
        Assert.Throws<FormatException>(() => SimpleYaml.Parse(yaml));
    }

    [Theory]
    [InlineData("Pre start", ' ')]
    [InlineData("", '\'')]
    [InlineData("null", '\'')]
    [InlineData("- dash", '\'')]
    [InlineData("a: b", '\'')]
    [InlineData("two\nlines", '"')]
    public void StyleFor_QuotesWhatPlainWouldChange(string value, char style)
    {
        Assert.Equal(style, SimpleYaml.StyleFor(value));
        var yaml = SimpleYaml.Emit(new SimpleYaml.Document("", Map("k", new YScalar(value)), "\n"));
        Assert.Equal(value, ((YScalar)SimpleYaml.Parse(yaml).Root.Get("k")!).Value);
    }

    // Real exports (pairs <name>.hmi.yml + <name>.TextLibrary.hmi.yml from
    // HmiTextListComposition.Export) from a folder outside the repository, named by
    // TIAMCP_TEXTLIST_SAMPLES. They are customer data: never copy them into the repository.
    [Fact]
    public void RealExports_RoundTripUnchanged()
    {
        var dir = Environment.GetEnvironmentVariable("TIAMCP_TEXTLIST_SAMPLES");
        if (string.IsNullOrEmpty(dir)) return;
        var files = System.IO.Directory.GetFiles(dir, "*.hmi.yml").Where(f => !f.EndsWith(".TextLibrary.hmi.yml")).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var lists = System.IO.File.ReadAllText(file);
            var library = System.IO.File.ReadAllText(file.Substring(0, file.Length - ".hmi.yml".Length) + ".TextLibrary.hmi.yml");
            var read = HmiTextListYaml.Read(lists, library);
            var patch = HmiTextListYaml.Patch(lists, library, read, deleteMissing: true);
            Assert.Equal(lists, patch.ListsYaml);
            Assert.Equal(library, patch.LibraryYaml);
        }
    }

    private static YMap Map(string key, YNode value)
    {
        var map = new YMap();
        map.Set(key, value);
        return map;
    }
}
