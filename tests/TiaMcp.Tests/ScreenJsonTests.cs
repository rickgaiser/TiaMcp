using System.Text.Json.Nodes;

namespace TiaMcp.Tests;

public sealed class ScreenJsonTests
{
    private const string Screen = """
        {
          "name": "Overview", "BackColor": "#FFB6B6B6", "Width": 1920,
          "items": [
            { "name": "Title", "type": "Text", "Left": 20, "Top": 10, "Text": "Overview", "Font": { "Size": 20 } },
            { "name": "Start", "type": "Button", "Left": 160, "Top": 80, "events": { "Tapped": "Start();" } },
            { "name": "Motor_1", "type": "FaceplateContainer", "ContainedType": "V1.0.0\\FP_Motor", "Left": 300,
              "Interface": { "Motor": "Motor_1", "Label": "M1" } }
          ]
        }
        """;

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Edit(Action<JsonObject> edit)
    {
        var json = Parse(Screen);
        edit(json);
        return Parse(json.ToJsonString());
    }

    private static JsonObject Item(JsonObject screen, string name) =>
        screen["items"]!.AsArray().Select(i => i!.AsObject()).Single(i => i["name"]!.GetValue<string>() == name);

    [Fact]
    public void Unchanged_WritesNothing_ButKeepsItemsArray()
    {
        var patch = ScreenJson.ChangedSince(Parse(Screen), Parse(Screen));

        Assert.Equal("""{"items":[]}""", patch.ToJsonString());
    }

    [Fact]
    public void ChangedScreenProperty_OnlyThatProperty()
    {
        var patch = ScreenJson.ChangedSince(Edit(s => s["BackColor"] = "#FF000000"), Parse(Screen));

        Assert.Equal("""{"BackColor":"#FF000000","items":[]}""", patch.ToJsonString());
    }

    [Fact]
    public void ChangedItemProperty_NameAndChangedValuesOnly()
    {
        var patch = ScreenJson.ChangedSince(Edit(s => Item(s, "Title")["Left"] = 30), Parse(Screen));

        Assert.Equal("""{"items":[{"name":"Title","Left":30}]}""", patch.ToJsonString());
    }

    [Fact]
    public void ChangedNestedPart_WholePart()
    {
        var patch = ScreenJson.ChangedSince(Edit(s => Item(s, "Motor_1")["Interface"]!["Label"] = "M2"), Parse(Screen));

        Assert.Equal("""{"items":[{"name":"Motor_1","Interface":{"Motor":"Motor_1","Label":"M2"}}]}""", patch.ToJsonString());
    }

    [Fact]
    public void NewItem_Whole()
    {
        var json = Edit(s => s["items"]!.AsArray().Add(Parse("""{ "name": "Lamp", "type": "Circle", "Left": 5 }""")));

        var patch = ScreenJson.ChangedSince(json, Parse(Screen));

        Assert.Equal("""{"items":[{"name":"Lamp","type":"Circle","Left":5}]}""", patch.ToJsonString());
    }

    [Fact]
    public void ChangedType_WholeItem()
    {
        var patch = ScreenJson.ChangedSince(Edit(s => Item(s, "Start")["type"] = "Rectangle"), Parse(Screen));

        var item = Assert.Single(patch["items"]!.AsArray())!.AsObject();
        Assert.True(JsonNode.DeepEquals(Item(Edit(s => Item(s, "Start")["type"] = "Rectangle"), "Start"), item));
    }

    [Fact]
    public void ChangedContainedType_WholeItem()
    {
        var patch = ScreenJson.ChangedSince(Edit(s => Item(s, "Motor_1")["ContainedType"] = "V1.0.1\\FP_Motor"), Parse(Screen));

        var item = Assert.Single(patch["items"]!.AsArray())!.AsObject();
        Assert.Equal("V1.0.1\\FP_Motor", item["ContainedType"]!.GetValue<string>());
        Assert.NotNull(item["Interface"]);
    }

    [Fact]
    public void RemovedPropertyOrItem_NotInPatch()
    {
        var json = Edit(s =>
        {
            Item(s, "Title").Remove("Font");
            s["items"]!.AsArray().RemoveAt(1);
        });

        var patch = ScreenJson.ChangedSince(json, Parse(Screen));

        Assert.Equal("""{"items":[]}""", patch.ToJsonString());
    }

    [Fact]
    public void ClonedValues_KeepTheirType()
    {
        // The writer reads interface values as JsonElement-backed numbers and strings.
        var patch = ScreenJson.ChangedSince(Edit(s => Item(s, "Title")["Top"] = 11), Parse(Screen));

        var top = patch["items"]![0]!["Top"]!.AsValue();
        Assert.Equal(11, top.GetValue<int>());
    }
}
