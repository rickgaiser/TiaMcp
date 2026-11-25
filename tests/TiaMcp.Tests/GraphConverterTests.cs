using System;
using System.IO;

namespace TiaMcp.Tests;

public class GraphConverterTests
{
    private static readonly string SampleXml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "SampleSequence.graph.xml"));

    [Fact]
    public void XmlToIL_RendersSample()
    {
        var il = GraphConverter.XmlToIL(SampleXml);

        Assert.Contains("GRAPH_BLOCK \"SampleSequence\"", il);
        Assert.Contains("    Start : Bool;  // Start the sequence", il);
        Assert.DoesNotContain("VAR /* Base */", il);
        Assert.Contains("SEQUENCE \"Main\"", il);
        Assert.Contains("  STEP 1 \"Idle\" INIT MAX_TIME=T#10S WARN_TIME=T#7S", il);
        Assert.Contains("    ACTION N: #Motor", il);
        Assert.Contains("  TRANSITION 1 \"StartRequested\": (#Start AND NOT #Ready)", il);
        Assert.Contains("  TRANSITION 2 \"Finished\": #Done", il);
        Assert.Contains("    TRANSITION 2 -> STEP 1 [JUMP]", il);
    }

    [Fact]
    public void UnchangedIL_RoundTrips()
    {
        var il = GraphConverter.XmlToIL(SampleXml);

        var xml = GraphConverter.ILToXml(il, SampleXml);

        Assert.Equal(il, GraphConverter.XmlToIL(xml));
    }

    [Fact]
    public void EditedStepAndCondition_AreWritten()
    {
        var il = GraphConverter.XmlToIL(SampleXml)
            .Replace("STEP 2 \"Running\"", "STEP 2 \"Working\" MAX_TIME=T#1M")
            .Replace("(#Start AND NOT #Ready)", "(#Start OR #Ready)")
            .Replace("ACTION N: #Motor", "ACTION S: #Motor");
        Assert.Contains("STEP 2 \"Working\" MAX_TIME=T#1M", il);
        Assert.Contains("(#Start OR #Ready)", il);
        Assert.Contains("ACTION S: #Motor", il);

        var xml = GraphConverter.ILToXml(il, SampleXml);

        Assert.Equal(il, GraphConverter.XmlToIL(xml));
    }

    [Fact]
    public void ChangedConditionWithFunctionCall_IsRefused()
    {
        var il = GraphConverter.XmlToIL(SampleXml).Replace(": #Done", ": CMP(#Done, #Ready)");
        Assert.Contains("CMP(#Done, #Ready)", il);

        Assert.Throws<InvalidOperationException>(() => GraphConverter.ILToXml(il, SampleXml));
    }

    [Fact]
    public void ChangedConditionWithTimeComparison_IsWritten()
    {
        var il = GraphConverter.XmlToIL(SampleXml).Replace("(#Start AND NOT #Ready)", "(#Start AND Ge(#Idle.T, T#1s))");
        Assert.Contains("(#Start AND Ge(#Idle.T, T#1s))", il);

        var xml = GraphConverter.ILToXml(il, SampleXml);

        Assert.Contains("<TemplateValue Name=\"SrcType\" Type=\"Type\">Time</TemplateValue>", xml);
        Assert.Equal(il, GraphConverter.XmlToIL(xml));
    }

    [Fact]
    public void ChangedConditionWithUntypedComparison_IsRefused()
    {
        var il = GraphConverter.XmlToIL(SampleXml).Replace("(#Start AND NOT #Ready)", "(#Start AND Ge(#Count, 5))");

        Assert.Throws<InvalidOperationException>(() => GraphConverter.ILToXml(il, SampleXml));
    }

    [Fact]
    public void NonGraphXml_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => GraphConverter.XmlToIL("<Document><Other /></Document>"));
    }
}
