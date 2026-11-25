using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaMcp.Tests;

/// <summary>
/// Round-trips real GRAPH exports (raw *.xml from PlcBlock.Export) from a folder outside the
/// repository, named by the TIAMCP_GRAPH_SAMPLES environment variable. Real exports are customer
/// data: never copy them into the repository. Without the variable this test does nothing.
/// </summary>
public class GraphRealSampleTests
{
    [Fact]
    public void RealExports_RoundTripUnchanged()
    {
        var dir = Environment.GetEnvironmentVariable("TIAMCP_GRAPH_SAMPLES");
        if (string.IsNullOrEmpty(dir)) return;
        Assert.True(Directory.Exists(dir), $"TIAMCP_GRAPH_SAMPLES '{dir}' does not exist.");

        var files = Directory.GetFiles(dir, "*.xml");
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var xml = File.ReadAllText(file);
                var il = GraphConverter.XmlToIL(xml);
                var roundTripped = GraphConverter.XmlToIL(GraphConverter.ILToXml(il, xml));
                if (roundTripped != il) failures.Add($"{Path.GetFileName(file)}: IL changed after an unedited round trip, first difference: {FirstDifference(il, roundTripped)}");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {files.Length} export(s) failed:\n" + string.Join("\n", failures));
    }

    private static string FirstDifference(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i].TrimEnd('\r') : "<end>";
            var y = i < b.Length ? b[i].TrimEnd('\r') : "<end>";
            if (x != y) return $"line {i + 1}: expected '{x}', got '{y}'";
        }
        return "none";
    }
}
