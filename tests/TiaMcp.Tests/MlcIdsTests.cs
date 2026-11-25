namespace TiaMcp.Tests;

public class MlcIdsTests
{
    private const string Dcl =
        "FUNCTION_BLOCK \"Sample\"\r\n" +
        "{ S7_BlockTitle := \"MLC_9xQ\" }\r\n" +
        "   VAR_INPUT\r\n" +
        "      Start { S7_MLC := \"MLC_3ab\" } : Bool;\r\n" +
        "      {\r\n" +
        "         S7_MLC := \"MLC_Zz\";\r\n" +
        "         S7_Visibility := \"True\"\r\n" +
        "      }\r\n" +
        "      \"Stop\" : Bool;\r\n" +
        "      Motor : Struct\r\n" +
        "         Start { S7_MLC := \"MLC_m1\" } : Bool;\r\n" +
        "      END_STRUCT;\r\n" +
        "   END_VAR\r\n" +
        "   {\r\n" +
        "      S7_NetworkTitle := \"MLC_net1t\"\r\n" +
        "   }\r\n" +
        "   NETWORK\r\n" +
        "   END_NETWORK\r\n" +
        "   {\r\n" +
        "      S7_NetworkComment := \"MLC_3ab\";\r\n" +
        "      S7_NetworkTitle := \"MLC_k7\"\r\n" +
        "   }\r\n" +
        "   NETWORK\r\n" +
        "   END_NETWORK\r\n";

    private const string Res =
        "MultiLingualTexts:\r\n" +
        "  - id: MLC_Zz\r\n" +
        "    en-US: Stop request, see MLC_3ab\r\n" +
        "  - id: MLC_3ab\r\n" +
        "    en-US: Start request\r\n" +
        "  - id: MLC_9xQ\r\n" +
        "    en-US: Sample block\r\n" +
        "  - id: MLC_net1t\r\n" +
        "    en-US: First network\r\n" +
        "  - id: MLC_m1\r\n" +
        "    en-US: Motor start\r\n" +
        "  - id: MLC_k7\r\n" +
        "    en-US: Second network\r\n" +
        "  - id: MLC_unused\r\n" +
        "    en-US: Not referenced\r\n";

    [Fact]
    public void Renumber_NamesIdsAfterWhatTheyDescribe()
    {
        var (dcl, res) = MlcIds.Renumber(Dcl, Res);

        Assert.Contains("{ S7_BlockTitle := \"MLC_title\" }", dcl);
        Assert.Contains("Start { S7_MLC := \"MLC_Start\" } : Bool;", dcl);
        Assert.Contains("S7_MLC := \"MLC_Stop\";", dcl);
        Assert.Contains("Start { S7_MLC := \"MLC_Start_2\" } : Bool;", dcl);
        Assert.Contains("S7_NetworkTitle := \"MLC_net1t\"", dcl);
        // An id TIA Portal shared between two texts keeps the name of its first use.
        Assert.Contains("S7_NetworkComment := \"MLC_Start\";", dcl);
        Assert.Contains("S7_NetworkTitle := \"MLC_net2t\"", dcl);
        Assert.Contains("  - id: MLC_Stop\r\n    en-US: Stop request, see MLC_3ab\r\n", res);
        Assert.Contains("  - id: MLC_Start\r\n    en-US: Start request\r\n", res);
        Assert.Contains("  - id: MLC_title\r\n    en-US: Sample block\r\n", res);
        Assert.Contains("  - id: MLC_Start_2\r\n    en-US: Motor start\r\n", res);
        Assert.Contains("  - id: MLC_net2t\r\n    en-US: Second network\r\n", res);
        Assert.Contains("  - id: MLC_unused1\r\n    en-US: Not referenced\r\n", res);
    }

    [Fact]
    public void Renumber_KeepsIdsOfOtherNetworksWhenOneIsInserted()
    {
        var inserted = Dcl.Replace("   END_VAR\r\n", "   END_VAR\r\n   NETWORK\r\n   END_NETWORK\r\n");

        var (dcl, _) = MlcIds.Renumber(inserted, Res);

        // Network numbers move with the insert, as in TIA Portal; members and the block keep theirs.
        Assert.Contains("S7_NetworkTitle := \"MLC_net2t\"", dcl);
        Assert.Contains("S7_NetworkTitle := \"MLC_net3t\"", dcl);
        Assert.Contains("Start { S7_MLC := \"MLC_Start\" } : Bool;", dcl);
        Assert.Contains("{ S7_BlockTitle := \"MLC_title\" }", dcl);
    }

    [Fact]
    public void Renumber_GivesSameOutputForDifferentlyNumberedExports()
    {
        // Only the ids change, not the text that happens to mention one.
        static string Reid(string s) => s
            .Replace("\"MLC_3ab\"", "\"MLC_7Kp\"").Replace("id: MLC_3ab", "id: MLC_7Kp")
            .Replace("\"MLC_Zz\"", "\"MLC_4n\"").Replace("id: MLC_Zz", "id: MLC_4n");

        Assert.Equal(MlcIds.Renumber(Dcl, Res), MlcIds.Renumber(Reid(Dcl), Reid(Res)));
    }

    [Fact]
    public void Renumber_IsIdempotent()
    {
        var once = MlcIds.Renumber(Dcl, Res);

        Assert.Equal(once, MlcIds.Renumber(once.Dcl, once.Res));
    }

    [Fact]
    public void Renumber_KeepsSharedIdShared()
    {
        var res = "MultiLingualTexts:\n  - id: MLC_dup\n    en-US: One\n  - id: MLC_dup\n    en-US: Two\n";

        var (_, renumbered) = MlcIds.Renumber("x { S7_MLC := \"MLC_dup\" } : Bool;", res);

        Assert.Equal("MultiLingualTexts:\n  - id: MLC_x\n    en-US: One\n  - id: MLC_x\n    en-US: Two\n", renumbered);
    }

    [Fact]
    public void Renumber_WithoutResourceFile()
    {
        var (dcl, res) = MlcIds.Renumber("x { S7_MLC := \"MLC_q\" } : Bool;", null);

        Assert.Equal("x { S7_MLC := \"MLC_x\" } : Bool;", dcl);
        Assert.Null(res);
    }
}
