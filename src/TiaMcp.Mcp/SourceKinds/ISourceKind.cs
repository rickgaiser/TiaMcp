using TiaMcp.Openness;

namespace TiaMcp.Mcp;

// The software an item lives in. A kind name can exist in more than one (classic and Unified
// HMIs both have "HMI tag table"), so a handler is looked up by family and name.
internal enum DeviceFamily { Plc, HmiUnified, HmiClassic }

/// <summary>
/// Everything the source tree knows about one kind of item (see DESIGN.md): where its files go,
/// how a new one is recognized, how it is listed, exported and written. Handlers hold no state;
/// register a new one in <see cref="SourceKinds"/>.
/// </summary>
internal interface ISourceKind
{
    // The kind as item lines show it ("tag table 'Group/Name'") - also the manifest's Kind.
    string Name { get; }

    // The folder below the device folder, as TIA Portal names that part of the project tree.
    string Section { get; }

    DeviceFamily Family { get; }

    // For a kind with exactly one item per device and a fixed file (the HMI alarm files): that
    // file's name without extension. The item's own name is then empty. Null for other kinds.
    string? FixedName { get; }

    // Whether an XML export needs the item consistent (compiled), as for blocks and UDTs: an
    // import leaves it inconsistent, so write_source_tree compiles the software before reading
    // such an item back as XML.
    bool XmlNeedsCompile { get; }

    // Every SourceFormat an item of this kind can be in, its own formats first and "xml" (the raw
    // export) last where Openness has one. A file in the section with one of their extensions that
    // no read put there is a new item.
    IReadOnlyList<string> Formats { get; }

    // For list_project: the items (and e.g. empty groups) of one device, names only.
    Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string device);

    // Exports the items formats (group path, name) selects into items, in the formats it returns:
    // null skips the item, an empty list means its own format. A failure is per item, not as a whole.
    // options are the tree's read settings (see ExportOptions).
    Task Export(TiaSession session, string device, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items);

    // Writes the given items of one device from their files, each from its ImportListItem.Format;
    // see write_source_tree.
    Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items);
}

// Read settings of a source tree, kept in its manifest so that every export of an item - the read,
// the conflict check and the read-back of a write - gives the same files.
// HmiTagLength: the Length column of HMI tag table CSVs (slow: an Openness call per tag).
internal sealed class ExportOptions
{
    public ExportOptions(bool hmiTagLength) { HmiTagLength = hmiTagLength; }
    public bool HmiTagLength { get; }

    public static ExportOptions Of(SourceTreeManifest? manifest) => new(manifest?.HmiTagLength == true);
}

// One line of list_project, with the item's formats (none for a group line). A group line
// (isItem false) is listed but not counted as an item; an inconsistent item (e.g. a block that
// needs a compile) is counted separately too.
internal sealed class ListedEntry
{
    public ListedEntry(string groupPath, string name, string text, IReadOnlyList<string> formats, bool isItem = true, bool inconsistent = false)
    {
        GroupPath = groupPath; Name = name; Text = text; Formats = formats; IsItem = isItem; Inconsistent = inconsistent;
    }
    public string GroupPath { get; }
    public string Name { get; }
    public string Text { get; }
    public IReadOnlyList<string> Formats { get; }
    public bool IsItem { get; }
    public bool Inconsistent { get; }
}

// What one write_source_tree call passes to a handler for one device.
internal sealed class WriteContext
{
    public WriteContext(TiaSession session, string device, string root, string deviceDir, bool createMissing, bool overwriteExisting, bool dryRun, bool workingCopy,
        SourceTreeManifest? tree, SourceTreeManifest? baseline, List<ImportListItem> written)
    {
        Session = session; Device = device; Root = root; DeviceDir = deviceDir; CreateMissing = createMissing;
        OverwriteExisting = overwriteExisting; DryRun = dryRun; WorkingCopy = workingCopy; Tree = tree; Baseline = baseline; Written = written;
    }
    public TiaSession Session { get; }
    public string Device { get; }

    // Absolute path of the tree's root, and of the device's folder in it.
    public string Root { get; }
    public string DeviceDir { get; }
    public bool CreateMissing { get; }
    public bool OverwriteExisting { get; }
    public bool DryRun { get; }

    // The tree is a working copy of this project: its table files are whole tables as read, so a
    // removed row means a deleted row.
    public bool WorkingCopy { get; }

    // The tree's manifest when it is a working copy: what the last read found. Null otherwise.
    public SourceTreeManifest? Tree { get; }

    // The manifest when the items being written were checked to be unchanged in TIA Portal since
    // the read (a working copy, without force): the project then still matches the tree as read
    // (see SourceTree.ReadBase). Null otherwise.
    public SourceTreeManifest? Baseline { get; }

    // Items written successfully; they are read back into the tree afterward.
    public List<ImportListItem> Written { get; }
}

// One report line per item (per row where a kind writes rows), as write_source_tree reports them.
internal sealed class WriteLog
{
    public List<string> Created { get; } = new();
    public List<string> Updated { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Failed { get; } = new();
}
