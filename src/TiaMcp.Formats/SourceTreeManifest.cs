using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMcp.Formats;

/// <summary>
/// What read_source_tree last exported into a source tree, kept as '_manifest.json' at its root:
/// per item its status and formats, and per file a hash of it as exported and its format.
/// write_source_tree compares those hashes with the files on disk (to write back only what was
/// edited) and with a fresh export (to refuse overwriting what was changed in TIA Portal since the
/// read). Version 1 (file path -> bare hash, no formats) is still read, and saved as version 2.
/// </summary>
public sealed class SourceTreeManifest
{
    public const string FileName = "_manifest.json";
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Version { get; set; } = CurrentVersion;
    public string? Project { get; set; }
    public DateTime? LastRead { get; set; }

    // read_source_tree's hmiTagLength: HMI tag table CSVs have the Length column. Null is off.
    public bool? HmiTagLength { get; set; }
    public List<SourceTreeItem> Items { get; set; } = new();

    public static SourceTreeManifest? Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        using (var doc = JsonDocument.Parse(json))
        {
            var hasVersion = doc.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "version", StringComparison.OrdinalIgnoreCase));
            if (!hasVersion) return FromV1(JsonSerializer.Deserialize<V1Manifest>(json, JsonOptions)!);
        }

        var manifest = JsonSerializer.Deserialize<SourceTreeManifest>(json, JsonOptions)!;
        if (manifest.Version > CurrentVersion)
            throw new InvalidDataException($"{FileName} is version {manifest.Version}, written by a newer TiaMcp; this one reads up to version {CurrentVersion}.");
        return manifest;
    }

    public void Save(string root)
    {
        Version = CurrentVersion;
        File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(this, JsonOptions));
    }

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

    // The SourceFormat a file belongs to: a .s7res and the read-only companions go with the .s7dcl.
    public static string FormatOf(string path)
    {
        var name = Path.GetFileName(path);
        if (IsReadOnlyCompanion(name) || name.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return SourceFormat.S7dcl;
        if (name.EndsWith(SourceFormat.Extension(SourceFormat.GraphIL), StringComparison.OrdinalIgnoreCase)) return SourceFormat.GraphIL;
        return Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
    }

    private static SourceTreeManifest FromV1(V1Manifest v1) => new()
    {
        Project = v1.Project,
        LastRead = v1.LastRead,
        Items = v1.Items.Select(i =>
        {
            var item = new SourceTreeItem { Device = i.Device, Kind = i.Kind, GroupPath = i.GroupPath, Name = i.Name, Ok = i.Ok, Error = i.Error, Stale = i.Stale };
            foreach (var file in i.Files) item.Files[file.Key] = new SourceTreeFile { Hash = file.Value, Format = FormatOf(file.Key) };
            // Version 1 didn't record the available formats; the next read of the item does.
            item.Formats = item.Files.Values.Select(f => f.Format).Distinct().ToList();
            return item;
        }).ToList(),
    };

    private sealed class V1Manifest
    {
        public string? Project { get; set; }
        public DateTime? LastRead { get; set; }
        public List<V1Item> Items { get; set; } = new();
    }

    private sealed class V1Item
    {
        public string Device { get; set; } = "";
        public string Kind { get; set; } = "";
        public string GroupPath { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Ok { get; set; } = true;
        public string? Error { get; set; }
        public bool Stale { get; set; }
        public Dictionary<string, string> Files { get; set; } = new();
    }
}

public sealed class SourceTreeItem
{
    public string Device { get; set; } = "";
    public string Kind { get; set; } = "";
    public string GroupPath { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }

    // A failed item whose files from an earlier read were renamed '.stale' (see MarkStale).
    public bool Stale { get; set; }

    // The formats the item can be read in, friendly format first - as list_project shows them.
    public List<string> Formats { get; set; } = new();

    // Root-relative path ('/'-separated) -> the file as exported.
    public Dictionary<string, SourceTreeFile> Files { get; set; } = new();

    public bool Is(string device, string kind, string name) =>
        string.Equals(Device, device, StringComparison.OrdinalIgnoreCase)
        && Kind == kind
        && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, SourceTreeFile>> WritableFiles =>
        Files.Where(f => !SourceTreeManifest.IsReadOnlyCompanion(f.Key));
}

// One file of an item. An object rather than a bare hash, so that later fields (multiuser state,
// timestamps) can be added without another version.
public sealed class SourceTreeFile
{
    // SourceTreeManifest.Hash of the content as exported.
    public string Hash { get; set; } = "";

    // See SourceTreeManifest.FormatOf.
    public string Format { get; set; } = "";
}
