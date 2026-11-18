using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness;

// A block's number as the PLC addresses it: OBs, FBs, FCs and DBs (global, instance and array DBs
// share one space) each have their own number space.
public static class BlockNumbers
{
    public const int Max = 65535;

    public static string? Space(PlcBlock block) => block switch
    {
        OB => "OB",
        FB => "FB",
        FC => "FC",
        DataBlock => "DB",
        _ => null,
    };

    // ("FB44", automatic numbering), or (null, true) for a block outside the number spaces.
    public static (string? Number, bool Auto) Of(PlcBlock block)
    {
        var space = Space(block);
        return space == null ? (null, true) : ($"{space}{block.Number}", block.AutoNumber);
    }

    // "DB6 auto" (numbered automatically) or "DB6 fixed".
    public static string Describe(string space, int number, bool auto) => $"{space}{number} {Mode(auto)}";

    public static string Mode(bool auto) => auto ? "auto" : "fixed";
}

/// <summary>
/// Renumbering blocks: a lifecycle change like a rename, made on the block itself (PlcBlock.Number
/// and AutoNumber), not through its source files.
/// </summary>
public sealed partial class TiaConnection
{
    private sealed class NumberedBlock
    {
        public NumberedBlock(PlcBlock block, string? space, bool user)
        {
            Block = block; Space = space; User = user; Number = block.Number; Auto = block.AutoNumber;
        }
        public PlcBlock Block { get; }
        public string? Space { get; }
        // In the program blocks (not a system block, which can't be renumbered here).
        public bool User { get; }
        public int Number { get; set; }
        public bool Auto { get; set; }
    }

    // Gives each requested block its number (null: automatic numbering). A number taken by a block
    // that keeps it is refused for that block only; numbers swapped or shifted within the request
    // are applied in an order that frees each number first, through a free number where they form a
    // cycle. With dryRun nothing changes, and the result is what would be done.
    public IReadOnlyList<BlockNumberChange> RenumberBlocks(string plcName, IReadOnlyList<BlockNumberRequest> requests, bool dryRun)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var all = new List<NumberedBlock>();
        CollectBlocks(software.BlockGroup.Blocks, software.BlockGroup.Groups, all);
        CollectSystemBlocks(software.BlockGroup.SystemBlockGroups, all);

        var results = new BlockNumberChange?[requests.Count];
        var moves = new List<(int Index, NumberedBlock Block, int? Target)>();
        var seen = new HashSet<NumberedBlock>();
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var block = FindNumbered(all, request.Block);
            if (block == null) { results[i] = new BlockNumberChange(request.Block, null, null, $"no block named '{request.Block}' in PLC '{plcName}'"); continue; }
            var from = Describe(block);
            if (block.Space == null) { results[i] = new BlockNumberChange(block.Block.Name, null, null, "this kind of block has no number"); continue; }
            if (request.Space != null && !string.Equals(request.Space, block.Space, StringComparison.OrdinalIgnoreCase)) { results[i] = new BlockNumberChange(block.Block.Name, from, null, $"it is {(block.Space is "OB" or "FB" or "FC" ? "an" : "a")} {block.Space}, not {(request.Space.ToUpperInvariant() is "OB" or "FB" or "FC" ? "an" : "a")} {request.Space.ToUpperInvariant()}"); continue; }
            if (!seen.Add(block)) { results[i] = new BlockNumberChange(block.Block.Name, from, null, "the block is in the list more than once"); continue; }
            if (request.Number is int n && (n < 1 || n > BlockNumbers.Max)) { results[i] = new BlockNumberChange(block.Block.Name, from, null, $"{n} is not a block number (1..{BlockNumbers.Max})"); continue; }
            if (request.Number == null ? block.Auto : request.Number == block.Number && !block.Auto)
            {
                results[i] = new BlockNumberChange(block.Block.Name, from, from, null, "unchanged");
                continue;
            }
            // Told before anything changes, so a dry run shows it too.
            if (NotRenumberable(block, request.Number == null || block.Auto, request.Number != null && request.Number != block.Number) is { } reason)
            {
                results[i] = new BlockNumberChange(block.Block.Name, from, null, reason);
                continue;
            }
            moves.Add((i, block, request.Number));
        }

        // Two requests for one number: neither is done.
        foreach (var clash in moves.Where(m => m.Target != null).GroupBy(m => (m.Block.Space, m.Target)).Where(g => g.Count() > 1).ToList())
        {
            foreach (var m in clash)
                results[m.Index] = new BlockNumberChange(m.Block.Block.Name, Describe(m.Block), null,
                    $"{clash.Key.Space}{clash.Key.Target} is requested for {string.Join(", ", clash.Select(c => $"'{c.Block.Block.Name}'"))}");
            moves.RemoveAll(m => clash.Contains(m));
        }

        // A number is free for a move when every block holding it moves away to a fixed number. A
        // block going to automatic numbering keeps its number, so it doesn't free one.
        bool MovesAway(NumberedBlock b) => moves.Any(m => m.Block == b && m.Target != null);
        foreach (var m in moves.Where(m => m.Target != null).ToList())
        {
            var holder = all.FirstOrDefault(b => b != m.Block && b.Space == m.Block.Space && b.Number == m.Target && !MovesAway(b));
            if (holder == null) continue;
            results[m.Index] = new BlockNumberChange(m.Block.Block.Name, Describe(m.Block), null,
                $"{m.Block.Space}{m.Target} is taken by '{holder.Block.Name}'{(holder.User ? "" : " (a system block)")}");
            moves.Remove(m);
        }
        // Refusing one move can leave a number held that another move counted on: check again until stable.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var m in moves.Where(m => m.Target != null).ToList())
            {
                var holder = all.FirstOrDefault(b => b != m.Block && b.Space == m.Block.Space && b.Number == m.Target && !MovesAway(b));
                if (holder == null) continue;
                results[m.Index] = new BlockNumberChange(m.Block.Block.Name, Describe(m.Block), null,
                    $"{m.Block.Space}{m.Target} stays with '{holder.Block.Name}', whose renumbering was refused");
                moves.Remove(m);
                changed = true;
            }
        }

        if (dryRun)
        {
            foreach (var m in moves)
                results[m.Index] = new BlockNumberChange(m.Block.Block.Name, Describe(m.Block), m.Target is int t ? BlockNumbers.Describe(m.Block.Space!, t, false) : BlockNumbers.Describe(m.Block.Space!, m.Block.Number, true), null);
            return results.Select(r => r!).ToList();
        }

        // Fixed numbers first, each once nobody else holds it; a cycle (a swap) goes through a free number.
        var original = moves.ToDictionary(m => m.Index, m => Describe(m.Block));
        var pending = moves.Where(m => m.Target != null).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(m => !all.Any(b => b != m.Block && b.Space == m.Block.Space && b.Number == m.Target)).ToList();
            if (ready.Count == 0)
            {
                // Held by a block whose own renumbering just failed: that number won't come free.
                var blocked = pending.Select(m => (Move: m, Holder: all.First(b => b != m.Block && b.Space == m.Block.Space && b.Number == m.Target)))
                    .Where(x => !pending.Any(p => p.Block == x.Holder)).ToList();
                foreach (var (m, holder) in blocked)
                {
                    var reason = $"{m.Block.Space}{m.Target} stays with '{holder.Block.Name}', whose renumbering failed";
                    if (Describe(m.Block) != original[m.Index]) reason += $" - it is now {Describe(m.Block)}";
                    results[m.Index] = new BlockNumberChange(m.Block.Block.Name, original[m.Index], null, reason);
                    pending.Remove(m);
                }
                if (blocked.Count > 0) continue;

                // Only blocks of this request hold the numbers: a cycle, broken through a free number.
                var stuck = pending[0];
                var spare = FreeNumber(all, stuck.Block.Space!);
                var error = spare == null ? $"no free {stuck.Block.Space} number to swap through" : SetNumber(stuck.Block, spare.Value);
                if (error != null)
                {
                    results[stuck.Index] = new BlockNumberChange(stuck.Block.Block.Name, original[stuck.Index], null, error);
                    pending.Remove(stuck);
                }
                continue;
            }
            foreach (var m in ready)
            {
                var error = SetNumber(m.Block, m.Target!.Value);
                // A block moved to a spare number first and then failed keeps that number: say so.
                if (error != null && Describe(m.Block) != original[m.Index]) error += $" - it is now {Describe(m.Block)}";
                results[m.Index] = new BlockNumberChange(m.Block.Block.Name, original[m.Index], error == null ? Describe(m.Block) : null, error);
                pending.Remove(m);
            }
        }

        foreach (var m in moves.Where(m => m.Target == null))
        {
            var from = Describe(m.Block);
            string? error = null;
            try { m.Block.Block.AutoNumber = true; m.Block.Auto = true; m.Block.Number = m.Block.Block.Number; }
            catch (Exception ex) { error = IsReadOnlyError(ex) ? ReadOnlyMessage(m.Block, "its numbering mode") : ex.Message; }
            results[m.Index] = new BlockNumberChange(m.Block.Block.Name, from, error == null ? Describe(m.Block) : null, error);
        }

        return results.Select(r => r!).ToList();
    }

    // Why the block's numbering can't be changed as asked, or null. Some blocks have read-only
    // Number/AutoNumber attributes, e.g. the error OBs (OB86, OB122): their number comes with
    // their type.
    private static string? NotRenumberable(NumberedBlock block, bool setsAutoNumber, bool setsNumber)
    {
        var readOnly = new List<string>();
        if (setsAutoNumber && !Writable(block.Block, "AutoNumber")) readOnly.Add("its numbering mode");
        if (setsNumber && !Writable(block.Block, "Number")) readOnly.Add("its number");
        return readOnly.Count == 0 ? null : ReadOnlyMessage(block, string.Join(" and ", readOnly));
    }

    private static string ReadOnlyMessage(NumberedBlock block, string what) =>
        $"TIA Portal doesn't let {what} be changed" +
        (block.Space == "OB" ? $" - {block.Space}{block.Number}'s number comes with its OB type (as for the error OBs), so it stays {Describe(block)}" : $"; it stays {Describe(block)}");

    // When Openness can't tell, assume it can be set: the attempt then reports TIA Portal's own error.
    private static bool Writable(PlcBlock block, string attribute)
    {
        try
        {
            var info = block.GetAttributeInfos().FirstOrDefault(a => a.Name == attribute);
            return info == null || (info.AccessMode & EngineeringAttributeAccessMode.Write) != 0;
        }
        catch
        {
            return true;
        }
    }

    // Fixes the block's number; null when it got it, else why not.
    private static string? SetNumber(NumberedBlock block, int number)
    {
        try
        {
            if (block.Block.AutoNumber) block.Block.AutoNumber = false;
            if (block.Block.Number != number) block.Block.Number = number;
            block.Number = block.Block.Number;
            block.Auto = block.Block.AutoNumber;
            return block.Number == number ? null : $"TIA Portal gave it {block.Space}{block.Number} instead";
        }
        catch (Exception ex)
        {
            return IsReadOnlyError(ex) ? ReadOnlyMessage(block, "its numbering") : ex.Message;
        }
    }

    private static bool IsReadOnlyError(Exception ex) => ex.Message.IndexOf("read-only", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Describe(NumberedBlock block) => BlockNumbers.Describe(block.Space ?? "", block.Number, block.Auto);

    private static int? FreeNumber(List<NumberedBlock> all, string space)
    {
        var used = new HashSet<int>(all.Where(b => b.Space == space).Select(b => b.Number));
        for (var n = used.Count == 0 ? 1 : used.Max() + 1; n <= BlockNumbers.Max; n++) if (!used.Contains(n)) return n;
        for (var n = BlockNumbers.Max; n >= 1; n--) if (!used.Contains(n)) return n;
        return null;
    }

    // The block name, or 'group/path/Name' (see FindBlockByName); program blocks only.
    private static NumberedBlock? FindNumbered(List<NumberedBlock> all, string name)
    {
        var user = all.Where(b => b.User).ToList();
        var slash = name.LastIndexOf('/');
        return user.FirstOrDefault(b => b.Block.Name == name)
            ?? user.FirstOrDefault(b => string.Equals(b.Block.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (slash >= 0 ? user.FirstOrDefault(b => string.Equals(b.Block.Name, name.Substring(slash + 1), StringComparison.OrdinalIgnoreCase)) : null);
    }

    // Every program block, whatever its language: they all hold a number.
    private static void CollectBlocks(PlcBlockComposition blocks, PlcBlockUserGroupComposition groups, List<NumberedBlock> all)
    {
        foreach (PlcBlock block in blocks) all.Add(new NumberedBlock(block, BlockNumbers.Space(block), user: true));
        foreach (var group in groups) CollectBlocks(group.Blocks, group.Groups, all);
    }

    private static void CollectSystemBlocks(PlcSystemBlockGroupComposition groups, List<NumberedBlock> all)
    {
        foreach (var group in groups)
        {
            foreach (PlcBlock block in group.Blocks) all.Add(new NumberedBlock(block, BlockNumbers.Space(block), user: false));
            CollectSystemBlocks(group.Groups, all);
        }
    }
}
