using System.IO;

namespace TiaMcp.Tests;

public sealed class SourceTreeManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TiaMcp.Tests", Guid.NewGuid().ToString("N"));

    public SourceTreeManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ManifestText => File.ReadAllText(Path.Combine(_root, SourceTreeManifest.FileName));

    [Fact]
    public void Load_WithoutManifest_ReturnsNull()
    {
        Assert.Null(SourceTreeManifest.Load(_root));
    }

    [Fact]
    public void Load_Version1_ConvertsFilesAndFormats()
    {
        File.WriteAllText(Path.Combine(_root, SourceTreeManifest.FileName), """
            {
              "Project": "SampleProject",
              "LastRead": "2026-01-02T03:04:05",
              "Items": [
                {
                  "Device": "PLC_1", "Kind": "block", "GroupPath": "Motors", "Name": "Pump",
                  "Ok": true, "Error": null, "Stale": false,
                  "Files": {
                    "PLC_1/Program blocks/Motors/Pump.s7dcl": "aa",
                    "PLC_1/Program blocks/Motors/Pump.s7res": "bb",
                    "PLC_1/Program blocks/Motors/Pump.stl-networks.awl": "cc"
                  }
                },
                { "Device": "PLC_1", "Kind": "tag table", "GroupPath": "", "Name": "Tags", "Files": { "PLC_1/PLC tags/Tags.csv": "dd" } }
              ]
            }
            """);

        var manifest = SourceTreeManifest.Load(_root)!;

        Assert.Equal(SourceTreeManifest.CurrentVersion, manifest.Version);
        Assert.Equal("SampleProject", manifest.Project);
        var block = manifest.Find("PLC_1", "block", "Pump")!;
        Assert.Equal("Motors", block.GroupPath);
        Assert.Equal(new[] { "s7dcl" }, block.Formats);
        Assert.Equal("bb", block.Files["PLC_1/Program blocks/Motors/Pump.s7res"].Hash);
        Assert.Equal("s7dcl", block.Files["PLC_1/Program blocks/Motors/Pump.stl-networks.awl"].Format);
        Assert.Equal(2, block.WritableFiles.Count());
        Assert.Equal("csv", manifest.Find("PLC_1", "tag table", "Tags")!.Files["PLC_1/PLC tags/Tags.csv"].Format);
    }

    [Fact]
    public void Save_WritesVersion2AndLoadsBack()
    {
        var manifest = new SourceTreeManifest { Project = "SampleProject" };
        var item = new SourceTreeItem { Device = "PLC_1", Kind = "tag table", Name = "Tags", Formats = { "csv", "xml" } };
        item.Files["PLC_1/PLC tags/Tags.csv"] = new SourceTreeFile { Hash = "dd", Format = "csv" };
        manifest.Set(item);

        manifest.Save(_root);

        Assert.Contains("\"version\": 2", ManifestText);
        Assert.Contains("\"formats\": [", ManifestText);
        Assert.Contains("\"hash\": \"dd\"", ManifestText);
        var loaded = SourceTreeManifest.Load(_root)!.Find("PLC_1", "tag table", "Tags")!;
        Assert.Equal(new[] { "csv", "xml" }, loaded.Formats);
        Assert.Equal("csv", loaded.Files["PLC_1/PLC tags/Tags.csv"].Format);
    }

    [Fact]
    public void Save_Multiuser_RoundTripsAndIsLeftOutWhenNull()
    {
        var manifest = new SourceTreeManifest();
        manifest.Set(new SourceTreeItem { Device = "PLC_1", Kind = "block", Name = "Main" });
        manifest.Set(new SourceTreeItem
        {
            Device = "PLC_1", Kind = "tag table", Name = "Tags",
            Multiuser = new SourceTreeMarks { Objects = 3, ByMe = 1, ByOthers = 2, ByOthersNames = new() { "Tag_A", "Tag_B" } },
        });
        manifest.Save(_root);

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(ManifestText, "\"multiuser\"").Count);
        var loaded = SourceTreeManifest.Load(_root)!;
        Assert.Null(loaded.Find("PLC_1", "block", "Main")!.Multiuser);
        var marks = loaded.Find("PLC_1", "tag table", "Tags")!.Multiuser!;
        Assert.Equal(new[] { "Tag_A", "Tag_B" }, marks.ByOthersNames);
        Assert.Equal("marked by another user: 2 of 3 (Tag_A, Tag_B); marked by me: 1 of 3", marks.Describe());
    }

    [Theory]
    [InlineData(1, 0, 0, "marked by me")]
    [InlineData(0, 1, 0, "marked by another user")]
    [InlineData(0, 0, 1, "not up to date with the server")]
    [InlineData(0, 0, 0, "")]
    public void Marks_Describe_SingleObject(int byMe, int byOthers, int notUpToDate, string expected)
    {
        var marks = new SourceTreeMarks { Objects = 1, ByMe = byMe, ByOthers = byOthers, NotUpToDate = notUpToDate };
        Assert.Equal(expected, marks.Describe());
    }

    [Fact]
    public void Load_NewerVersion_IsRefused()
    {
        File.WriteAllText(Path.Combine(_root, SourceTreeManifest.FileName), """{ "version": 99, "items": [] }""");

        Assert.Throws<InvalidDataException>(() => SourceTreeManifest.Load(_root));
    }

    [Theory]
    [InlineData("D/Program blocks/Main.s7dcl", "s7dcl")]
    [InlineData("D/Program blocks/Main.s7res", "s7dcl")]
    [InlineData("D/Program blocks/Main.interface.txt", "s7dcl")]
    [InlineData("D/Program blocks/Main.stl-networks.xml", "s7dcl")]
    [InlineData("D/Program blocks/Seq.graph.il", "graph.il")]
    [InlineData("D/Program blocks/Calc.awl", "awl")]
    [InlineData("D/PLC tags/Tags.csv", "csv")]
    [InlineData("D/Screens/Start.XML", "xml")]
    public void FormatOf_GroupsFilesByFormat(string path, string format)
    {
        Assert.Equal(format, SourceTreeManifest.FormatOf(path));
    }
}
