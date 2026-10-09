using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Mcp;

/// <summary>
/// What read_source_tree last exported into a source tree, kept as '_manifest.json' at its root:
/// per item its status and a hash of each file as exported. write_source_tree compares those
/// hashes with the files on disk (to write back only what was edited) and with a fresh export
/// (to refuse overwriting what was changed in TIA Portal since the read).
/// </summary>
internal sealed class SourceTreeManifest
{
    public const string FileName = "_manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string? Project { get; set; }
    public DateTime? LastRead { get; set; }
    public List<SourceTreeItem> Items { get; set; } = new();

    public static SourceTreeManifest? Load(string root)
    {
        var path = Path.Combine(root, FileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<SourceTreeManifest>(File.ReadAllText(path), JsonOptions) : null;
    }

    public void Save(string root) =>
        File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(this, JsonOptions));

    public SourceTreeItem? Find(string device, string kind, string name) =>
        Items.FirstOrDefault(i => i.Is(device, kind, name));

    public void Set(SourceTreeItem item)
    {
        Items.RemoveAll(i => i.Is(item.Device, item.Kind, item.Name));
        Items.Add(item);
    }

    // Line endings are left out of the hash, so an editor that rewrites them doesn't make an
    // unedited file look changed.
    public static string Hash(string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")));
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    // Files read_source_tree writes for information only and write_source_tree never imports:
    // an instance DB's resolved interface and the STL networks of a mixed-language block.
    public static bool IsReadOnlyCompanion(string path) =>
        path.EndsWith(".interface.txt", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).IndexOf(".stl-networks.", StringComparison.OrdinalIgnoreCase) >= 0;
}

internal sealed class SourceTreeItem
{
    public string Device { get; set; } = "";
    public string Kind { get; set; } = "";
    public string GroupPath { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }

    // A failed item whose files from an earlier read were renamed '.stale' (see MarkStale).
    public bool Stale { get; set; }

    // Root-relative path ('/'-separated) -> SourceTreeManifest.Hash of the content as exported.
    public Dictionary<string, string> Files { get; set; } = new();

    public bool Is(string device, string kind, string name) =>
        string.Equals(Device, device, StringComparison.OrdinalIgnoreCase)
        && Kind == kind
        && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, string>> WritableFiles =>
        Files.Where(f => !SourceTreeManifest.IsReadOnlyCompanion(f.Key));
}
