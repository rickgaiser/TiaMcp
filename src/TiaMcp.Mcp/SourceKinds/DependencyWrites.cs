using System.Text.RegularExpressions;
using TiaMcp.Openness;

namespace TiaMcp.Mcp;

// One UDT or block write (update or create) decided by UdtKind/BlockKind. A plain class rather
// than a record - see ImportListItem.
internal sealed class PendingWrite
{
    public PendingWrite(ImportListItem item, string label, List<BlockDocument> docs, bool isCreate, Func<Task<ImportResult>> write)
    {
        Item = item; Label = label; Docs = docs; IsCreate = isCreate; Write = write;
    }
    public ImportListItem Item { get; }
    public string Name => Item.ItemName;
    public string Label { get; }
    public List<BlockDocument> Docs { get; }
    public bool IsCreate { get; }
    public Func<Task<ImportResult>> Write { get; }
}

internal static class DependencyWrites
{
    // Writes UDTs/blocks after what they reference: an import that references something not
    // imported yet fails ("Dependent object ... not found") or, for a UDT nesting another UDT,
    // succeeds but silently drops the nested start values unless the nested UDT is also compiled
    // - hence compileBetweenLayers for UDTs. Blocks only need the order (a full round trip of a
    // ~500-block PLC came back identical without compiles between block layers). That gives UDTs in nesting
    // order, and blocks as global DBs/FBs before the FBs that call them and the instance DBs on
    // them. References are read from the source text (see Layers), so a create that
    // still fails is retried after a compile, as long as a pass creates something.
    public static async Task Write(WriteContext context, List<PendingWrite> writes, WriteLog log, bool compileBetweenLayers)
    {
        var lastError = new Dictionary<PendingWrite, string>();
        var retry = new List<PendingWrite>();
        var layers = Layers(writes);
        var kind = writes.Count == 0 ? "" : writes[0].Item.Kind;
        var done = 0;
        for (var i = 0; i < layers.Count; i++)
        {
            if (i > 0 && compileBetweenLayers && !context.Progress.Cancelled)
            {
                context.Progress.Note($"{context.Device}: compiling before layer {i + 1} of {layers.Count}");
                await context.Session.CompileAsync(context.Device);
            }
            foreach (var write in layers[i])
            {
                if (context.Progress.Cancelled) { log.Skipped.Add($"{write.Label} - not written, the call was cancelled"); continue; }
                context.Progress.Item($"{context.Device}: {kind}", ++done, writes.Count, write.Label);
                var result = await write.Write();
                if (result.Success) { (write.IsCreate ? log.Created : log.Updated).Add(write.Label); context.Written.Add(write.Item); }
                else if (write.IsCreate) { retry.Add(write); lastError[write] = string.Join(" | ", result.Messages); }
                else log.Failed.Add($"{write.Label}: {string.Join(" | ", result.Messages)}");
            }
        }

        while (retry.Count > 0 && !context.Progress.Cancelled)
        {
            context.Progress.Note($"{context.Device}: compiling to retry {retry.Count} failed create(s)");
            await context.Session.CompileAsync(context.Device);
            var stillFailing = new List<PendingWrite>();
            foreach (var write in retry)
            {
                if (context.Progress.Cancelled) { stillFailing.Add(write); continue; }
                context.Progress.Note($"{context.Device}: {write.Label} (retry)");
                var result = await write.Write();
                if (result.Success) { log.Created.Add(write.Label); context.Written.Add(write.Item); }
                else { stillFailing.Add(write); lastError[write] = string.Join(" | ", result.Messages); }
            }
            if (stillFailing.Count == retry.Count) break;
            retry = stillFailing;
        }
        foreach (var write in retry)
        {
            if (context.Progress.Cancelled) log.Skipped.Add($"{write.Label} - not created, the call was cancelled (last attempt: {lastError[write]})");
            else log.Failed.Add($"{write.Label}: {lastError[write]}");
        }
    }

    private static readonly Regex IdentifierToken = new("\"([^\"]+)\"|[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // Groups writes into layers where each item only references items of earlier layers. A
    // reference is any identifier in the item's source (not its .s7res texts) that names another
    // item in the same set - e.g. "DATA_BLOCK "x" : FB_y", "member : _.UDT_z". A cycle, or a name
    // that happens to match a variable, ends up in the last layer, where the retry in
    // Write still covers it.
    private static List<List<PendingWrite>> Layers(List<PendingWrite> writes)
    {
        var names = new HashSet<string>(writes.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
        var dependencies = writes.ToDictionary(w => w, w => IdentifierToken
            .Matches(string.Join("\n", w.Docs.Where(d => !d.FileName.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase)).Select(d => d.Content)))
            .Cast<Match>()
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value)
            .Where(t => names.Contains(t) && !string.Equals(t, w.Name, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var layers = new List<List<PendingWrite>>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = writes;
        while (remaining.Count > 0)
        {
            var layer = remaining.Where(w => dependencies[w].All(placed.Contains)).ToList();
            if (layer.Count == 0) layer = remaining;
            layers.Add(layer);
            foreach (var w in layer) placed.Add(w.Name);
            remaining = remaining.Except(layer).ToList();
        }
        return layers;
    }
}
