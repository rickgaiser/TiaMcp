using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaMcp.Openness;

/// <summary>
/// Converts the GRAPH-only XML produced by PlcBlock.Export/PlcBlockComposition.Import (a
/// different Siemens API from the .s7dcl/.s7res route every other language uses - see the
/// plan doc's "GRAPH (S7-GRAPH/SFC)" section) to and from a compact text intermediate
/// language, so GRAPH blocks can be read/written without exposing the raw graphical
/// FlgNet/Parts/Wires XML to the AI.
///
/// Condition-network rendering (RenderFlgNet et al.) is deliberately honest about its actual
/// coverage: AND/OR gates and tag/instance references are fully understood and rendered as
/// SCL-style boolean text; any other part type (comparisons, timers, moves, etc. - not yet
/// seen in a live sample) falls back to a generic "PartName(args...)" rendering rather than
/// guessing semantics that haven't been verified.
/// </summary>
public static class GraphConverter
{
    private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    private static readonly XNamespace GraphNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6";

    private static readonly HashSet<string> SinkPartNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Coil", "SvCoil", "IlCoil", "TrCoil", "RsCoil", "SCoil", "RCoil"
    };

    public static string XmlToIL(string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var blockElement = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks."));
        if (blockElement == null)
        {
            throw new InvalidOperationException("Unrecognized GRAPH export: no SW.Blocks.* root element found.");
        }

        var attrList = blockElement.Element("AttributeList");
        var name = attrList?.Element("Name")?.Value ?? "?";

        var sb = new StringBuilder();
        sb.AppendLine($"GRAPH_BLOCK \"{name}\"");
        sb.AppendLine();

        var interfaceEl = attrList?.Element("Interface");
        if (interfaceEl != null)
        {
            WriteInterface(sb, interfaceEl);
        }

        var compileUnit = blockElement.Descendants("SW.Blocks.CompileUnit").FirstOrDefault();
        var graphEl = compileUnit?.Descendants(GraphNs + "Graph").FirstOrDefault();
        if (graphEl == null)
        {
            sb.AppendLine("; WARNING: no <Graph> network source found - sequence body could not be converted.");
            return sb.ToString();
        }

        WritePreOperations(sb, graphEl);
        WriteSequences(sb, graphEl);

        return sb.ToString();
    }

    private static void WriteInterface(StringBuilder sb, XElement interfaceEl)
    {
        var sections = interfaceEl.Element(InterfaceNs + "Sections");
        if (sections == null) return;

        sb.AppendLine("INTERFACE");
        foreach (var section in sections.Elements(InterfaceNs + "Section"))
        {
            var sectionName = section.Attribute("Name")?.Value ?? "?";
            // "Base" is GRAPH-internal bookkeeping (e.g. OFF_SQ_BASE), not user-meaningful - skip it.
            if (sectionName == "Base") continue;

            var keyword = sectionName switch
            {
                "Input" => "VAR_INPUT",
                "Output" => "VAR_OUTPUT",
                "InOut" => "VAR_IN_OUT",
                "Static" => "VAR",
                "Temp" => "VAR_TEMP",
                _ => "VAR /* " + sectionName + " */",
            };

            // Only direct-child Members, not recursively expanded nested Sections - a member
            // referencing a named type (e.g. the auto-generated "Steps"/"Transitions" structs)
            // is shown by name only, same as how SCL declarations reference UDT names without
            // inlining their fields.
            var members = section.Elements(InterfaceNs + "Member").ToList();
            if (members.Count == 0) continue;

            sb.AppendLine($"  {keyword}");
            foreach (var member in members)
            {
                var memberName = member.Attribute("Name")?.Value ?? "?";
                var dataType = member.Attribute("Datatype")?.Value ?? "?";
                var comment = member.Element(InterfaceNs + "Comment")
                    ?.Elements(InterfaceNs + "MultiLanguageText")
                    .FirstOrDefault(t => (string?)t.Attribute("Lang") == "en-US")?.Value;
                var commentSuffix = string.IsNullOrWhiteSpace(comment) ? "" : $"  // {comment}";
                sb.AppendLine($"    {memberName} : {dataType};{commentSuffix}");
            }
            sb.AppendLine("  END_VAR");
        }
        sb.AppendLine("END_INTERFACE");
        sb.AppendLine();
    }

    private static void WritePreOperations(StringBuilder sb, XElement graphEl)
    {
        var preOps = graphEl.Element(GraphNs + "PreOperations");
        var ops = preOps?.Elements(GraphNs + "PermanentOperation").ToList();
        if (ops == null || ops.Count == 0) return;

        sb.AppendLine("PREOPERATIONS");
        foreach (var op in ops)
        {
            var title = GetTitle(op);
            var flgNet = op.Element(GraphNs + "FlgNet");
            var expr = flgNet != null ? RenderFlgNet(flgNet) : "?";
            if (!string.IsNullOrEmpty(title)) sb.AppendLine($"  ; {title}");
            sb.AppendLine($"  {expr}");
        }
        sb.AppendLine("END_PREOPERATIONS");
        sb.AppendLine();
    }

    private static void WriteSequences(StringBuilder sb, XElement graphEl)
    {
        foreach (var seq in graphEl.Elements(GraphNs + "Sequence"))
        {
            var title = GetTitle(seq) ?? "";
            sb.AppendLine($"SEQUENCE \"{title}\"");

            foreach (var step in seq.Element(GraphNs + "Steps")?.Elements(GraphNs + "Step") ?? Enumerable.Empty<XElement>())
            {
                WriteStep(sb, step);
            }

            foreach (var trans in seq.Element(GraphNs + "Transitions")?.Elements(GraphNs + "Transition") ?? Enumerable.Empty<XElement>())
            {
                WriteTransition(sb, trans);
            }

            var branches = seq.Element(GraphNs + "Branches")?.Elements(GraphNs + "Branch").ToList();
            if (branches is { Count: > 0 })
            {
                sb.AppendLine("  BRANCHES");
                foreach (var branch in branches)
                {
                    sb.AppendLine($"    BRANCH {branch.Attribute("Number")?.Value} {branch.Attribute("Type")?.Value} CARDINALITY {branch.Attribute("Cardinality")?.Value}");
                }
                sb.AppendLine("  END_BRANCHES");
            }

            var connections = seq.Element(GraphNs + "Connections")?.Elements(GraphNs + "Connection").ToList();
            if (connections is { Count: > 0 })
            {
                sb.AppendLine("  CONNECTIONS");
                foreach (var conn in connections)
                {
                    var from = DescribeNodeRef(conn.Element(GraphNs + "NodeFrom"));
                    var to = DescribeNodeRef(conn.Element(GraphNs + "NodeTo"));
                    var linkType = conn.Element(GraphNs + "LinkType")?.Value ?? "Direct";
                    var suffix = linkType == "Jump" ? " [JUMP]" : "";
                    sb.AppendLine($"    {from} -> {to}{suffix}");
                }
                sb.AppendLine("  END_CONNECTIONS");
            }

            sb.AppendLine("END_SEQUENCE");
            sb.AppendLine();
        }
    }

    private static string DescribeNodeRef(XElement? nodeRefContainer)
    {
        if (nodeRefContainer == null) return "?";
        var stepRef = nodeRefContainer.Element(GraphNs + "StepRef");
        if (stepRef != null) return $"STEP {stepRef.Attribute("Number")?.Value}";
        var transRef = nodeRefContainer.Element(GraphNs + "TransitionRef");
        if (transRef != null) return $"TRANSITION {transRef.Attribute("Number")?.Value}";
        // A fan-in/fan-out node at an alternative (or, unconfirmed, simultaneous) branch point -
        // see the Branches list for this Number's Type/Cardinality. In="i" is the single inbound
        // arm reference; Out="i" is one of the cardinality-many outbound arm references.
        var branchRef = nodeRefContainer.Element(GraphNs + "BranchRef");
        if (branchRef != null)
        {
            var num = branchRef.Attribute("Number")?.Value;
            var inIdx = branchRef.Attribute("In")?.Value;
            var outIdx = branchRef.Attribute("Out")?.Value;
            return inIdx != null ? $"BRANCH {num} IN {inIdx}" : $"BRANCH {num} OUT {outIdx}";
        }
        return "?";
    }

    private static void WriteStep(StringBuilder sb, XElement step)
    {
        var number = step.Attribute("Number")?.Value ?? "?";
        var name = step.Attribute("Name")?.Value ?? "?";
        var init = (string?)step.Attribute("Init") == "true" ? " INIT" : "";
        var maxTime = step.Attribute("MaximumStepTime")?.Value;
        var warnTime = step.Attribute("WarningTime")?.Value;

        sb.Append($"  STEP {number} \"{name}\"{init}");
        if (!string.IsNullOrEmpty(maxTime)) sb.Append($" MAX_TIME={maxTime}");
        if (!string.IsNullOrEmpty(warnTime)) sb.Append($" WARN_TIME={warnTime}");
        sb.AppendLine();

        var actionsEl = step.Element(GraphNs + "Actions");
        if (actionsEl != null)
        {
            var actionsTitle = GetTitle(actionsEl);
            if (!string.IsNullOrEmpty(actionsTitle)) sb.AppendLine($"    ; {actionsTitle}");
            foreach (var action in actionsEl.Elements(GraphNs + "Action"))
            {
                var evt = action.Attribute("Event")?.Value;
                var qualifier = action.Attribute("Qualifier")?.Value;
                var text = string.Concat(action.Elements(GraphNs + "Token").Select(t => (string?)t.Attribute("Text") ?? ""))
                    .Replace("\r", "").Replace("\n", " ").Trim();
                if (string.IsNullOrEmpty(evt) && string.IsNullOrEmpty(qualifier) && string.IsNullOrEmpty(text)) continue;
                var label = evt != null ? $"{evt} {qualifier}" : qualifier ?? "";
                sb.AppendLine($"    ACTION {label}: {text}");
            }
        }

        foreach (var supervision in step.Element(GraphNs + "Supervisions")?.Elements(GraphNs + "Supervision") ?? Enumerable.Empty<XElement>())
        {
            var flgNet = supervision.Element(GraphNs + "FlgNet");
            sb.AppendLine($"    SUPERVISION: {(flgNet != null ? RenderFlgNet(flgNet) : "?")}");
        }

        foreach (var interlock in step.Element(GraphNs + "Interlocks")?.Elements(GraphNs + "Interlock") ?? Enumerable.Empty<XElement>())
        {
            var flgNet = interlock.Element(GraphNs + "FlgNet");
            sb.AppendLine($"    INTERLOCK: {(flgNet != null ? RenderFlgNet(flgNet) : "?")}");
        }

        sb.AppendLine("  END_STEP");
    }

    private static void WriteTransition(StringBuilder sb, XElement trans)
    {
        var number = trans.Attribute("Number")?.Value ?? "?";
        var name = trans.Attribute("Name")?.Value ?? "?";
        var flgNet = trans.Element(GraphNs + "FlgNet");
        var expr = flgNet != null ? RenderFlgNet(flgNet) : "?";
        sb.AppendLine($"  TRANSITION {number} \"{name}\": {expr}");
    }

    private static string? GetTitle(XElement el)
    {
        return el.Element(GraphNs + "Title")
            ?.Elements(GraphNs + "MultiLanguageText")
            .FirstOrDefault(t => (string?)t.Attribute("Lang") == "en-US")?.Value;
    }

    // --- FlgNet (graphical FBD wire-graph) -> boolean/comparison text ---

    private static string RenderFlgNet(XElement flgNet)
    {
        var parts = flgNet.Element(GraphNs + "Parts")?.Elements() ?? Enumerable.Empty<XElement>();
        var wires = (flgNet.Element(GraphNs + "Wires")?.Elements(GraphNs + "Wire") ?? Enumerable.Empty<XElement>()).ToList();

        var nodesByUId = new Dictionary<string, XElement>();
        foreach (var p in parts)
        {
            var uid = p.Attribute("UId")?.Value;
            if (uid != null) nodesByUId[uid] = p;
        }

        // (destination uid, destination pin) -> source uid (or "open"/unconnected).
        var pinSources = new Dictionary<(string uid, string pin), (string? sourceUid, bool open)>();
        foreach (var wire in wires)
        {
            var children = wire.Elements().ToList();
            if (children.Count != 2) continue;
            var src = children[0];
            var dst = children[1];
            if (dst.Name.LocalName != "NameCon") continue;
            var dstUid = dst.Attribute("UId")?.Value;
            var dstPin = dst.Attribute("Name")?.Value;
            if (dstUid == null || dstPin == null) continue;

            pinSources[(dstUid, dstPin)] = src.Name.LocalName == "OpenCon"
                ? (null, true)
                : (src.Attribute("UId")?.Value, false);
        }

        var sink = nodesByUId.Values.FirstOrDefault(p => SinkPartNames.Contains(p.Attribute("Name")?.Value ?? ""));
        if (sink == null)
        {
            return "; NOTE: no recognized sink (Coil-family) part found in this network - could not determine the condition expression.";
        }

        var sinkUid = sink.Attribute("UId")!.Value;
        var pinKey = pinSources.Keys.FirstOrDefault(k => k.uid == sinkUid);
        if (pinKey.uid == null) return "TRUE";
        return RenderPin(pinKey, nodesByUId, pinSources, new HashSet<string>());
    }

    private static string RenderPin(
        (string uid, string pin) pinKey,
        Dictionary<string, XElement> nodesByUId,
        Dictionary<(string uid, string pin), (string? sourceUid, bool open)> pinSources,
        HashSet<string> visiting)
    {
        if (!pinSources.TryGetValue(pinKey, out var source)) return "TRUE";
        if (source.open || source.sourceUid == null) return "TRUE";
        return RenderNode(source.sourceUid, nodesByUId, pinSources, visiting);
    }

    private static string RenderNode(
        string uid,
        Dictionary<string, XElement> nodesByUId,
        Dictionary<(string uid, string pin), (string? sourceUid, bool open)> pinSources,
        HashSet<string> visiting)
    {
        if (!nodesByUId.TryGetValue(uid, out var node)) return $"?{uid}";
        if (!visiting.Add(uid)) return "?cycle"; // defensive only - shouldn't occur in a valid FBD network

        try
        {
            if (node.Name.LocalName == "Access")
            {
                var scope = node.Attribute("Scope")?.Value;

                // A named constant declared in the block's own interface (e.g. "c_True").
                var namedConstant = node.Element(GraphNs + "Constant")?.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(namedConstant)) return "#" + namedConstant;

                // An inline typed literal (e.g. t#10S, 16#0100) - render its value verbatim.
                var constantValue = node.Element(GraphNs + "Constant")?.Element(GraphNs + "ConstantValue")?.Value;
                if (constantValue != null) return constantValue;

                var components = node.Element(GraphNs + "Symbol")
                    ?.Elements(GraphNs + "Component")
                    .Select(c => c.Attribute("Name")?.Value ?? "?")
                    .ToList() ?? new List<string>();
                if (components.Count == 0) return "?";

                return scope == "GlobalVariable"
                    ? "\"" + components[0] + "\"" + (components.Count > 1 ? "." + string.Join(".", components.Skip(1)) : "")
                    : "#" + string.Join(".", components);
            }

            var partName = node.Attribute("Name")?.Value ?? "?";
            if (partName is "A" or "O")
            {
                var op = partName == "A" ? " AND " : " OR ";
                var cardStr = node.Element(GraphNs + "TemplateValue")?.Value;
                var card = int.TryParse(cardStr, out var c) ? c : pinSources.Keys.Count(k => k.uid == uid && k.pin.StartsWith("in"));
                var negated = new HashSet<string>(node.Elements(GraphNs + "Negated").Select(n => n.Attribute("Name")?.Value ?? ""));

                var operands = new List<string>();
                for (var i = 1; i <= card; i++)
                {
                    var pin = "in" + i;
                    var key = (uid, pin);
                    var rendered = pinSources.ContainsKey(key) ? RenderPin(key, nodesByUId, pinSources, visiting) : "TRUE";
                    if (negated.Contains(pin)) rendered = $"NOT {rendered}";
                    operands.Add(rendered);
                }
                return "(" + string.Join(op, operands) + ")";
            }

            // Unrecognized part type (comparisons, timers, moves, etc.) - render generically
            // rather than guessing semantics not yet verified against a real example.
            var inputPins = pinSources.Keys.Where(k => k.uid == uid).Select(k => k.pin).OrderBy(p => p).ToList();
            var args = inputPins.Select(pin => RenderPin((uid, pin), nodesByUId, pinSources, visiting));
            return $"{partName}({string.Join(", ", args)})";
        }
        finally
        {
            visiting.Remove(uid);
        }
    }

    // ============================================================================================
    // IL -> XML (write direction). Scope:
    //  - Edits STEP/TRANSITION attributes (Name/Init/MaxTime/WarnTime), Actions, and
    //    Supervision/Interlock/Transition condition text for steps/transitions that already
    //    exist in the original XML (matched by Number). The INTERFACE section is still parsed
    //    only far enough to be skipped - interface edits aren't supported.
    //  - BRANCHES/CONNECTIONS are now round-tripped: if the IL for a given SEQUENCE contains a
    //    BRANCHES or CONNECTIONS block, that section's XML (<Branches>/<Connections>) is fully
    //    regenerated from the IL text, replacing the original. This is a confirmed-schema
    //    operation - the attribute/element names (Branch Number/Type/Cardinality; Connection
    //    NodeFrom/NodeTo/LinkType; StepRef/TransitionRef/BranchRef) are exactly what
    //    WriteSequences/DescribeNodeRef already read, so regeneration is the verified inverse of
    //    reading, not a guess.
    //  - A step/transition Number present in the original XML but absent from the new IL is
    //    treated as a deletion: its <Step>/<Transition> element is removed, and (if this
    //    sequence's CONNECTIONS weren't explicitly rewritten in the IL) any <Connection>
    //    referencing the deleted Number is also stripped, so no dangling StepRef/TransitionRef
    //    is left behind.
    //  - A step/transition Number in the new IL that does NOT exist in the original XML (i.e. a
    //    genuinely new step/transition) creates a new <Step>/<Transition> element plus its
    //    required Interface Static-section Member (see the "Brand-new STEP/TRANSITION creation"
    //    section below).
    //  - A condition network is only regenerated if its new text actually differs from the
    //    original (compared as rendered text - see RenderFlgNet above). Unchanged conditions are
    //    left as the original XML, byte-for-byte, zero regeneration risk.
    //  - A changed condition is only regenerated if its parse tree contains no opaque
    //    function-call node (comparisons, timers, etc. - the same constructs RenderFlgNet's
    //    fallback produces on read). If it does, the whole write is refused with a clear message
    //    naming the offending step/transition - never a silent, possibly-wrong regeneration.
    // ============================================================================================

    public static string ILToXml(string ilContent, string originalXmlContent)
    {
        var doc = XDocument.Parse(originalXmlContent);
        var blockElement = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks."));
        if (blockElement == null)
        {
            throw new InvalidOperationException("Unrecognized GRAPH XML: no SW.Blocks.* root element found.");
        }

        var attrList = blockElement.Element("AttributeList")!;
        var interfaceEl = attrList.Element("Interface");
        var knownConstants = interfaceEl?.Element(InterfaceNs + "Sections")
            ?.Elements(InterfaceNs + "Section")
            .Where(s => (string?)s.Attribute("Name") == "Constant")
            .Elements(InterfaceNs + "Member")
            .Select(m => m.Attribute("Name")?.Value ?? "")
            .Where(n => n.Length > 0)
            .ToHashSet() ?? new HashSet<string>();

        var compileUnit = blockElement.Descendants("SW.Blocks.CompileUnit").FirstOrDefault();
        var graphEl = compileUnit?.Descendants(GraphNs + "Graph").FirstOrDefault();
        if (graphEl == null)
        {
            throw new InvalidOperationException("Original GRAPH XML has no <Graph> network source - cannot apply edits.");
        }

        var edits = ParseIL(ilContent);

        var sequences = graphEl.Elements(GraphNs + "Sequence").ToList();
        if (edits.Sequences.Count != sequences.Count)
        {
            throw new InvalidOperationException(
                $"The IL text has {edits.Sequences.Count} SEQUENCE block(s) but the original XML has {sequences.Count} - adding or removing a whole SEQUENCE isn't supported. No changes were made.");
        }

        for (var seqIdx = 0; seqIdx < sequences.Count; seqIdx++)
        {
            var seq = sequences[seqIdx];
            var seqEdit = edits.Sequences[seqIdx];

            var stepsEl = seq.Element(GraphNs + "Steps");
            var originalStepNumbers = stepsEl?.Elements(GraphNs + "Step").Select(s => s.Attribute("Number")?.Value ?? "").ToHashSet() ?? new HashSet<string>();
            var transitionsEl = seq.Element(GraphNs + "Transitions");
            var originalTransNumbers = transitionsEl?.Elements(GraphNs + "Transition").Select(t => t.Attribute("Number")?.Value ?? "").ToHashSet() ?? new HashSet<string>();

            var ilSteps = edits.Steps.Where(s => s.SequenceIndex == seqIdx).ToList();
            var ilTransitions = edits.Transitions.Where(t => t.SequenceIndex == seqIdx).ToList();
            var ilStepNumbers = ilSteps.Select(s => s.Number).ToHashSet();
            var ilTransNumbers = ilTransitions.Select(t => t.Number).ToHashSet();

            var newStepNumbers = ilStepNumbers.Except(originalStepNumbers).ToList();
            var newTransNumbers = ilTransNumbers.Except(originalTransNumbers).ToList();

            foreach (var stepEdit in ilSteps.Where(s => originalStepNumbers.Contains(s.Number)))
            {
                var stepEl = stepsEl!.Elements(GraphNs + "Step").First(s => s.Attribute("Number")?.Value == stepEdit.Number);
                ApplyStepEdit(stepEl, stepEdit, knownConstants);
            }

            foreach (var transEdit in ilTransitions.Where(t => originalTransNumbers.Contains(t.Number)))
            {
                var transEl = transitionsEl!.Elements(GraphNs + "Transition").First(t => t.Attribute("Number")?.Value == transEdit.Number);
                ApplyTransitionEdit(transEl, transEdit, knownConstants);
            }

            if (newStepNumbers.Count > 0 || newTransNumbers.Count > 0)
            {
                var staticSection = GetStaticInterfaceSection(interfaceEl);

                // The Step{N}/Trans{N} bookkeeping members must use whatever G7_StepPlus_V*/
                // G7_TransitionPlus_V* library version this project's own blocks already use -
                // it varies per project (an older export used V2, another project's GRAPH
                // template uses V6) and a mismatch makes TIA's importer reject the new member
                // outright ("already exists ... not of the type ... and is not write-protected").
                // Copy it from an existing sibling member rather than hardcoding a version.
                var (stepDatatype, stepVersion) = FindMemberTypeTemplate(staticSection, "G7_StepPlus", "G7_StepPlus_V2", "1.0");
                var (transDatatype, transVersion) = FindMemberTypeTemplate(staticSection, "G7_TransitionPlus", "G7_TransitionPlus_V2", "1.0");

                foreach (var number in newStepNumbers)
                {
                    var stepEdit = ilSteps.First(s => s.Number == number);
                    if (stepsEl == null)
                    {
                        stepsEl = new XElement(GraphNs + "Steps");
                        seq.AddFirst(stepsEl);
                    }
                    var newStepEl = BuildNewStepElement(number);
                    stepsEl.Add(newStepEl);
                    ApplyStepEdit(newStepEl, stepEdit, knownConstants);
                    staticSection.Add(BuildStepInterfaceMember(number,
                        (string?)newStepEl.Attribute("MaximumStepTime") ?? "T#10S",
                        (string?)newStepEl.Attribute("WarningTime") ?? "T#7S",
                        stepDatatype, stepVersion));
                }

                foreach (var number in newTransNumbers)
                {
                    var transEdit = ilTransitions.First(t => t.Number == number);
                    if (transitionsEl == null)
                    {
                        transitionsEl = new XElement(GraphNs + "Transitions");
                        seq.Add(transitionsEl); // Steps always precede Transitions in a Sequence.
                    }
                    var newTransEl = BuildNewTransitionElement(number);
                    transitionsEl.Add(newTransEl);
                    ApplyTransitionEdit(newTransEl, transEdit, knownConstants);
                    staticSection.Add(BuildTransitionInterfaceMember(number, transDatatype, transVersion));
                }
            }

            var removedStepNumbers = originalStepNumbers.Except(ilStepNumbers).ToList();
            var removedTransNumbers = originalTransNumbers.Except(ilTransNumbers).ToList();
            foreach (var num in removedStepNumbers)
            {
                stepsEl!.Elements(GraphNs + "Step").First(s => s.Attribute("Number")?.Value == num).Remove();
            }
            foreach (var num in removedTransNumbers)
            {
                transitionsEl!.Elements(GraphNs + "Transition").First(t => t.Attribute("Number")?.Value == num).Remove();
            }

            // A removed step/transition's own Static-section bookkeeping member is orphaned
            // otherwise - TIA's GUI removes it together with the graph element, so mirror that
            // rather than leaving a stale, unused Step{N}/Trans{N} member behind.
            if (removedStepNumbers.Count > 0 || removedTransNumbers.Count > 0)
            {
                var staticSection = GetStaticInterfaceSection(interfaceEl);
                foreach (var num in removedStepNumbers)
                {
                    staticSection.Elements(InterfaceNs + "Member")
                        .FirstOrDefault(m => (string?)m.Attribute("Name") == "Step" + num)?.Remove();
                }
                foreach (var num in removedTransNumbers)
                {
                    staticSection.Elements(InterfaceNs + "Member")
                        .FirstOrDefault(m => (string?)m.Attribute("Name") == "Trans" + num)?.Remove();
                }
            }

            ApplyBranchesAndConnections(seq, seqEdit, removedStepNumbers, removedTransNumbers, ilStepNumbers, ilTransNumbers, seqIdx);
        }

        // A block name change is honored (create_block/write_block both rename via the caller's
        // target blockName, matching the existing .s7dcl-route convention) - everything else about
        // AttributeList (Number, etc.) is left untouched so this is a true in-place edit.
        return doc.ToString();
    }

    // ============================================================================================
    // Brand-new STEP/TRANSITION creation. Two things are required, both reproduced exactly below:
    //  - The <Step>/<Transition> element itself in the Graph/Sequence (BuildNewStepElement/
    //    BuildNewTransitionElement), with a default "open" (always-true) Supervision/Interlock/
    //    condition FlgNet - the same OpenCon/NameCon shape ApplyConditionEdit's RenderFlgNet
    //    already renders as "TRUE", so an edit that doesn't mention SUPERVISION/INTERLOCK/the
    //    transition condition leaves it untouched exactly as intended.
    //  - A mirror <Member> in the block's own Interface, VAR (Static) section, named "Step{N}"/
    //    "Trans{N}" with a fixed GRAPH-internal UDT (G7_StepPlus_V2/G7_TransitionPlus_V2) -
    //    without this, TIA Portal's GUI itself always creates one alongside the graph element, so
    //    presumably import/compile depends on it existing too (BuildStepInterfaceMember/
    //    BuildTransitionInterfaceMember). Its field values (SNO/TNO, T_MAX/T_WARN) are cosmetic/
    //    informative - TIA recomputes them - but are filled in accurately anyway since the real
    //    values are already at hand.
    // Ordering of the new element within its container, and of the new Member within Static,
    // doesn't matter for import - the reader code resolves everything by Number/Name attribute,
    // never document position - both are simply appended at the end.
    // ============================================================================================

    // Finds an existing Static-section member whose Datatype starts with the given UDT prefix
    // (e.g. "G7_StepPlus") and returns its exact Datatype/Version, so a newly created member
    // matches this project's actual library version instead of a hardcoded guess. Falls back to
    // the given defaults only if no such member exists yet (e.g. a sequence with zero steps).
    private static (string Datatype, string Version) FindMemberTypeTemplate(
        XElement staticSection, string typePrefix, string fallbackDatatype, string fallbackVersion)
    {
        var existing = staticSection.Elements(InterfaceNs + "Member")
            .FirstOrDefault(m => ((string?)m.Attribute("Datatype"))?.StartsWith(typePrefix, StringComparison.OrdinalIgnoreCase) == true);
        if (existing == null) return (fallbackDatatype, fallbackVersion);
        return ((string?)existing.Attribute("Datatype") ?? fallbackDatatype, (string?)existing.Attribute("Version") ?? fallbackVersion);
    }

    private static XElement GetStaticInterfaceSection(XElement? interfaceEl)
    {
        var section = interfaceEl?.Element(InterfaceNs + "Sections")
            ?.Elements(InterfaceNs + "Section")
            .FirstOrDefault(s => (string?)s.Attribute("Name") == "Static");
        if (section == null)
        {
            throw new InvalidOperationException(
                "Cannot add a new STEP/TRANSITION: this block's Interface has no VAR (Static) section to add the required Step{N}/Trans{N} member to. No changes were made.");
        }
        return section;
    }

    private static XElement BuildNewStepElement(string number)
    {
        return new XElement(GraphNs + "Step",
            new XAttribute("Number", number),
            new XAttribute("Init", "false"),
            new XAttribute("Name", "Step" + number),
            new XAttribute("MaximumStepTime", "T#10S"),
            new XAttribute("WarningTime", "T#7S"),
            new XElement(GraphNs + "Actions", new XElement(GraphNs + "Action")),
            new XElement(GraphNs + "Supervisions", BuildOpenConditionElement("Supervision", "SvCoil")),
            new XElement(GraphNs + "Interlocks", BuildOpenConditionElement("Interlock", "IlCoil")));
    }

    private static XElement BuildNewTransitionElement(string number)
    {
        return new XElement(GraphNs + "Transition",
            new XAttribute("IsMissing", "false"),
            new XAttribute("Name", "Trans" + number),
            new XAttribute("Number", number),
            new XAttribute("ProgrammingLanguage", "FBD"),
            BuildOpenFlgNet("TrCoil"));
    }

    // An unconnected (always-true) condition network - OpenCon wired straight to the sink -
    // exactly what TIA Portal's own GUI generates for a fresh Supervision/Interlock/Transition
    // with no condition configured yet.
    private static XElement BuildOpenConditionElement(string elementName, string sinkPartName)
    {
        return new XElement(GraphNs + elementName,
            new XAttribute("ProgrammingLanguage", "FBD"),
            BuildOpenFlgNet(sinkPartName));
    }

    private static XElement BuildOpenFlgNet(string sinkPartName)
    {
        return new XElement(GraphNs + "FlgNet",
            new XElement(GraphNs + "Parts",
                new XElement(GraphNs + "Part", new XAttribute("Name", sinkPartName), new XAttribute("UId", "21"))),
            new XElement(GraphNs + "Wires",
                new XElement(GraphNs + "Wire", new XAttribute("UId", "23"),
                    new XElement(GraphNs + "OpenCon", new XAttribute("UId", "22")),
                    new XElement(GraphNs + "NameCon", new XAttribute("UId", "21"), new XAttribute("Name", "in")))));
    }

    private static XElement BuildBooleanAttributeList()
    {
        return new XElement(InterfaceNs + "AttributeList",
            new XElement(InterfaceNs + "BooleanAttribute", new XAttribute("Name", "ExternalAccessible"), new XAttribute("SystemDefined", "true"), "false"),
            new XElement(InterfaceNs + "BooleanAttribute", new XAttribute("Name", "ExternalVisible"), new XAttribute("SystemDefined", "true"), "false"),
            new XElement(InterfaceNs + "BooleanAttribute", new XAttribute("Name", "ExternalWritable"), new XAttribute("SystemDefined", "true"), "false"),
            new XElement(InterfaceNs + "BooleanAttribute", new XAttribute("Name", "SetPoint"), new XAttribute("Informative", "true"), new XAttribute("SystemDefined", "true"), "true"));
    }

    private static XElement BuildStepInterfaceMember(string number, string maxTime, string warnTime, string datatype, string version)
    {
        XElement BoolMember(string n) => new(InterfaceNs + "Member", new XAttribute("Name", n), new XAttribute("Datatype", "Bool"));
        XElement ValueMember(string n, string dt, string startValue) =>
            new(InterfaceNs + "Member", new XAttribute("Name", n), new XAttribute("Datatype", dt),
                new XElement(InterfaceNs + "StartValue", new XAttribute("Informative", "true"), startValue));

        return new XElement(InterfaceNs + "Member",
            new XAttribute("Name", "Step" + number),
            new XAttribute("Datatype", datatype),
            new XAttribute("Version", version),
            new XAttribute("Remanence", "NonRetain"),
            new XAttribute("Accessibility", "Public"),
            BuildBooleanAttributeList(),
            new XElement(InterfaceNs + "Comment", new XAttribute("Informative", "true"),
                new XElement(InterfaceNs + "MultiLanguageText", new XAttribute("Lang", "en-US"), "Step structure")),
            new XElement(InterfaceNs + "Sections",
                new XElement(InterfaceNs + "Section", new XAttribute("Name", "None"),
                    BoolMember("S1"), BoolMember("L1"), BoolMember("V1"), BoolMember("R1"), BoolMember("A1"),
                    BoolMember("S0"), BoolMember("L0"), BoolMember("V0"), BoolMember("X"),
                    BoolMember("LA"), BoolMember("VA"), BoolMember("RA"), BoolMember("AA"),
                    BoolMember("SS"), BoolMember("LS"), BoolMember("VS"),
                    ValueMember("SNO", "Int", number),
                    new XElement(InterfaceNs + "Member", new XAttribute("Name", "T"), new XAttribute("Datatype", "Time")),
                    new XElement(InterfaceNs + "Member", new XAttribute("Name", "U"), new XAttribute("Datatype", "Time")),
                    ValueMember("T_MAX", "Time", maxTime),
                    ValueMember("T_WARN", "Time", warnTime),
                    BoolMember("SM"),
                    new XElement(InterfaceNs + "Member", new XAttribute("Name", "H_IL_ERR"), new XAttribute("Datatype", "Byte")),
                    ValueMember("H_SV_FLT", "Byte", "16#04"))));
    }

    private static XElement BuildTransitionInterfaceMember(string number, string datatype, string version)
    {
        return new XElement(InterfaceNs + "Member",
            new XAttribute("Name", "Trans" + number),
            new XAttribute("Datatype", datatype),
            new XAttribute("Version", version),
            new XAttribute("Remanence", "NonRetain"),
            new XAttribute("Accessibility", "Public"),
            BuildBooleanAttributeList(),
            new XElement(InterfaceNs + "Comment", new XAttribute("Informative", "true"),
                new XElement(InterfaceNs + "MultiLanguageText", new XAttribute("Lang", "en-US"), "Transition structure")),
            new XElement(InterfaceNs + "Sections",
                new XElement(InterfaceNs + "Section", new XAttribute("Name", "None"),
                    new XElement(InterfaceNs + "Member", new XAttribute("Name", "TV"), new XAttribute("Datatype", "Bool")),
                    new XElement(InterfaceNs + "Member", new XAttribute("Name", "TT"), new XAttribute("Datatype", "Bool")),
                    new XElement(InterfaceNs + "Member", new XAttribute("Name", "TS"), new XAttribute("Datatype", "Bool")),
                    new XElement(InterfaceNs + "Member", new XAttribute("Name", "TNO"), new XAttribute("Datatype", "Int"),
                        new XElement(InterfaceNs + "StartValue", new XAttribute("Informative", "true"), number)))));
    }

    private static void ApplyStepEdit(XElement stepEl, StepEdit edit, HashSet<string> knownConstants)
    {
        if (edit.Name != null) stepEl.SetAttributeValue("Name", edit.Name);
        stepEl.SetAttributeValue("Init", edit.Init ? "true" : "false");
        if (edit.MaxTime != null) stepEl.SetAttributeValue("MaximumStepTime", edit.MaxTime);
        if (edit.WarnTime != null) stepEl.SetAttributeValue("WarningTime", edit.WarnTime);

        if (edit.Actions != null)
        {
            var actionsEl = stepEl.Element(GraphNs + "Actions");
            if (actionsEl != null)
            {
                var existingActionEls = actionsEl.Elements(GraphNs + "Action").ToList();
                foreach (var a in existingActionEls) a.Remove();
                foreach (var actionEdit in edit.Actions)
                {
                    var actionEl = new XElement(GraphNs + "Action");
                    if (actionEdit.Event != null) actionEl.SetAttributeValue("Event", actionEdit.Event);
                    if (actionEdit.Qualifier != null) actionEl.SetAttributeValue("Qualifier", actionEdit.Qualifier);
                    actionEl.Add(new XElement(GraphNs + "Token", new XAttribute("Text", actionEdit.Text)));
                    actionEl.Add(new XElement(GraphNs + "Token", new XAttribute("Text", "\n")));
                    actionsEl.Add(actionEl);
                }
                // Every real Actions element - regardless of how many real actions it holds, zero
                // included - ends with exactly one trailing blank <Action/> (every Actions element
                // in a real GRAPH export ends with one, whether it also had real actions or not -
                // it's a GUI "new action" placeholder slot, not an empty-case fallback). Always
                // restore it after rebuilding the real ones.
                actionsEl.Add(new XElement(GraphNs + "Action"));
            }
        }

        if (edit.Supervision != null)
        {
            var flgNet = stepEl.Element(GraphNs + "Supervisions")?.Element(GraphNs + "Supervision")?.Element(GraphNs + "FlgNet");
            ApplyConditionEdit(flgNet, edit.Supervision, knownConstants, "SvCoil", $"step {edit.Number} SUPERVISION");
        }
        if (edit.Interlock != null)
        {
            var flgNet = stepEl.Element(GraphNs + "Interlocks")?.Element(GraphNs + "Interlock")?.Element(GraphNs + "FlgNet");
            ApplyConditionEdit(flgNet, edit.Interlock, knownConstants, "IlCoil", $"step {edit.Number} INTERLOCK");
        }
    }

    private static void ApplyTransitionEdit(XElement transEl, TransitionEdit edit, HashSet<string> knownConstants)
    {
        if (edit.Name != null) transEl.SetAttributeValue("Name", edit.Name);
        if (edit.Condition != null)
        {
            ApplyConditionEdit(transEl.Element(GraphNs + "FlgNet"), edit.Condition, knownConstants, "TrCoil", $"transition {edit.Number}");
        }
    }

    private static void ApplyBranchesAndConnections(
        XElement seq, SequenceEdit seqEdit, List<string> removedStepNumbers, List<string> removedTransNumbers,
        HashSet<string> finalStepNumbers, HashSet<string> finalTransNumbers, int seqIdx)
    {
        var branchesEl = seq.Element(GraphNs + "Branches");
        var originalBranchNumbers = branchesEl?.Elements(GraphNs + "Branch").Select(b => b.Attribute("Number")?.Value ?? "").ToHashSet() ?? new HashSet<string>();

        if (seqEdit.SawBranches)
        {
            var newBranchesEl = new XElement(GraphNs + "Branches",
                seqEdit.Branches.Select(b => new XElement(GraphNs + "Branch",
                    new XAttribute("Number", b.Number),
                    new XAttribute("Type", b.Type),
                    new XAttribute("Cardinality", b.Cardinality))));
            if (branchesEl != null) branchesEl.ReplaceWith(newBranchesEl);
            else seq.Add(newBranchesEl);
        }

        // The branch numbers a Connection's BranchRef is allowed to point at, after this
        // sequence's own edits are applied (regenerated set if BRANCHES was touched, else the
        // original XML's set, unaffected by anything here).
        var finalBranchNumbers = seqEdit.SawBranches
            ? seqEdit.Branches.Select(b => b.Number).ToHashSet()
            : originalBranchNumbers;

        var connectionsEl = seq.Element(GraphNs + "Connections");
        if (seqEdit.SawConnections)
        {
            foreach (var conn in seqEdit.Connections)
            {
                ValidateNodeRef(conn.From, finalStepNumbers, finalTransNumbers, finalBranchNumbers, seqIdx);
                ValidateNodeRef(conn.To, finalStepNumbers, finalTransNumbers, finalBranchNumbers, seqIdx);
            }

            var newConnectionsEl = new XElement(GraphNs + "Connections",
                seqEdit.Connections.Select(BuildConnectionElement));
            if (connectionsEl != null) connectionsEl.ReplaceWith(newConnectionsEl);
            else seq.Add(newConnectionsEl);
        }
        else if (connectionsEl != null && (removedStepNumbers.Count > 0 || removedTransNumbers.Count > 0))
        {
            // Topology wasn't touched in the IL, but a step/transition it may reference was
            // deleted - strip any now-dangling Connection rather than leave a broken StepRef/
            // TransitionRef behind.
            connectionsEl.Elements(GraphNs + "Connection")
                .Where(c => ReferencesRemovedNode(c, removedStepNumbers, removedTransNumbers))
                .ToList()
                .ForEach(c => c.Remove());
        }
    }

    private static void ValidateNodeRef(NodeRefEdit r, HashSet<string> validStepNumbers, HashSet<string> validTransNumbers, HashSet<string> validBranchNumbers, int seqIdx)
    {
        if (r.StepNumber != null && !validStepNumbers.Contains(r.StepNumber))
        {
            throw new InvalidOperationException(
                $"A CONNECTIONS line in SEQUENCE #{seqIdx + 1} references STEP {r.StepNumber}, which no longer exists (it was removed, or never existed) - remove or fix that connection. No changes were made.");
        }
        if (r.TransitionNumber != null && !validTransNumbers.Contains(r.TransitionNumber))
        {
            throw new InvalidOperationException(
                $"A CONNECTIONS line in SEQUENCE #{seqIdx + 1} references TRANSITION {r.TransitionNumber}, which no longer exists (it was removed, or never existed) - remove or fix that connection. No changes were made.");
        }
        if (r.BranchNumber != null && !validBranchNumbers.Contains(r.BranchNumber))
        {
            throw new InvalidOperationException(
                $"A CONNECTIONS line in SEQUENCE #{seqIdx + 1} references BRANCH {r.BranchNumber}, which isn't in this sequence's BRANCHES list - remove or fix that connection, or add the branch. No changes were made.");
        }
    }

    private static bool ReferencesRemovedNode(XElement connection, List<string> removedStepNumbers, List<string> removedTransNumbers)
    {
        foreach (var dir in new[] { "NodeFrom", "NodeTo" })
        {
            var container = connection.Element(GraphNs + dir);
            var stepNum = container?.Element(GraphNs + "StepRef")?.Attribute("Number")?.Value;
            if (stepNum != null && removedStepNumbers.Contains(stepNum)) return true;
            var transNum = container?.Element(GraphNs + "TransitionRef")?.Attribute("Number")?.Value;
            if (transNum != null && removedTransNumbers.Contains(transNum)) return true;
        }
        return false;
    }

    private static XElement BuildConnectionElement(ConnectionEdit conn)
    {
        // <LinkType> is always present in a real export - even for "Direct", the default
        // DescribeNodeRef's reader falls back to when the element is absent (every Connection in a
        // real GRAPH export, Direct and Jump alike, carries an explicit LinkType). Emit it
        // explicitly rather than relying on that fallback.
        return new XElement(GraphNs + "Connection",
            BuildNodeRefContainer("NodeFrom", conn.From),
            BuildNodeRefContainer("NodeTo", conn.To),
            new XElement(GraphNs + "LinkType", conn.Jump ? "Jump" : "Direct"));
    }

    private static XElement BuildNodeRefContainer(string containerName, NodeRefEdit r)
    {
        XElement inner;
        if (r.StepNumber != null)
        {
            inner = new XElement(GraphNs + "StepRef", new XAttribute("Number", r.StepNumber));
        }
        else if (r.TransitionNumber != null)
        {
            inner = new XElement(GraphNs + "TransitionRef", new XAttribute("Number", r.TransitionNumber));
        }
        else if (r.BranchNumber != null)
        {
            inner = new XElement(GraphNs + "BranchRef", new XAttribute("Number", r.BranchNumber));
            if (r.BranchIn != null) inner.SetAttributeValue("In", r.BranchIn);
            if (r.BranchOut != null) inner.SetAttributeValue("Out", r.BranchOut);
        }
        else
        {
            throw new InvalidOperationException("Internal error: an empty node reference reached XML generation.");
        }
        return new XElement(GraphNs + containerName, inner);
    }

    private static void ApplyConditionEdit(XElement? flgNet, string newConditionText, HashSet<string> knownConstants, string sinkPartName, string location)
    {
        if (flgNet == null) return; // nothing to compare/replace against - leave as-is.

        var originalText = RenderFlgNet(flgNet).Trim();
        var newText = newConditionText.Trim();
        if (originalText == newText) return; // unchanged - do not touch the original XML at all.

        ExprNode tree;
        try
        {
            tree = ParseExpression(newText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not parse the new condition for {location}: {ex.Message}. No changes were made.");
        }

        if (ContainsOpaque(tree, out var opaqueName))
        {
            throw new InvalidOperationException(
                $"{location}'s condition uses '{opaqueName}(...)', which this server can't safely regenerate yet (only AND/OR/NOT and tag/constant references can be written) - refusing to guess. Edit this condition in the TIA Portal GUI instead. No changes were made.");
        }

        var newFlgNet = BuildFlgNet(tree, knownConstants, sinkPartName);
        flgNet.ReplaceWith(newFlgNet);
    }

    // --- IL text parser ---

    private sealed class ActionEdit
    {
        public string? Event;
        public string? Qualifier;
        public string Text = "";
    }

    private sealed class StepEdit
    {
        public string Number = "";
        public string? Name;
        public bool Init;
        public string? MaxTime;
        public string? WarnTime;
        public List<ActionEdit>? Actions;
        public string? Supervision;
        public string? Interlock;
        public int SequenceIndex;
    }

    private sealed class TransitionEdit
    {
        public string Number = "";
        public string? Name;
        public string? Condition;
        public int SequenceIndex;
    }

    // A node reference as it appears in a Connection's NodeFrom/NodeTo (see DescribeNodeRef):
    // exactly one of StepNumber/TransitionNumber/BranchNumber is set.
    private sealed class NodeRefEdit
    {
        public string? StepNumber;
        public string? TransitionNumber;
        public string? BranchNumber;
        public string? BranchIn;
        public string? BranchOut;
    }

    private sealed class ConnectionEdit
    {
        public NodeRefEdit From = new();
        public NodeRefEdit To = new();
        public bool Jump;
    }

    private sealed class BranchEdit
    {
        public string Number = "";
        public string Type = "";
        public string Cardinality = "";
    }

    private sealed class SequenceEdit
    {
        public List<BranchEdit> Branches = new();
        public List<ConnectionEdit> Connections = new();
        // True only if the IL text actually contained a BRANCHES/CONNECTIONS...END_ block for
        // this sequence - distinguishes "no topology in this text" (leave the XML alone) from
        // "an explicit, possibly-empty block" (regenerate from it, even to zero entries).
        public bool SawBranches;
        public bool SawConnections;
    }

    private sealed class GraphEdits
    {
        public List<StepEdit> Steps = new();
        public List<TransitionEdit> Transitions = new();
        public List<SequenceEdit> Sequences = new();
    }

    private static readonly System.Text.RegularExpressions.Regex StepHeaderRegex = new(
        "^STEP\\s+(?<num>\\d+)\\s+\"(?<name>[^\"]*)\"(?<init>\\s+INIT)?(?:\\s+MAX_TIME=(?<max>\\S+))?(?:\\s+WARN_TIME=(?<warn>\\S+))?\\s*$");

    private static readonly System.Text.RegularExpressions.Regex TransitionRegex = new(
        "^TRANSITION\\s+(?<num>\\d+)\\s+\"(?<name>[^\"]*)\"\\s*:\\s*(?<expr>.*)$");

    private static readonly System.Text.RegularExpressions.Regex ActionRegex = new(
        "^ACTION\\s+(?<label>.*?):\\s*(?<text>.*)$");

    private static readonly System.Text.RegularExpressions.Regex BranchLineRegex = new(
        "^BRANCH\\s+(?<num>\\d+)\\s+(?<type>\\S+)\\s+CARDINALITY\\s+(?<card>\\d+)\\s*$");

    // Mirrors DescribeNodeRef's output exactly: "STEP n", "TRANSITION n", "BRANCH n IN i"/"BRANCH n OUT i".
    private static readonly System.Text.RegularExpressions.Regex ConnectionLineRegex = new(
        "^(?<from>.+?)\\s*->\\s*(?<to>.+?)(?<jump>\\s*\\[JUMP\\])?\\s*$");

    private static readonly System.Text.RegularExpressions.Regex NodeRefStepRegex = new("^STEP\\s+(?<num>\\d+)$");
    private static readonly System.Text.RegularExpressions.Regex NodeRefTransRegex = new("^TRANSITION\\s+(?<num>\\d+)$");
    private static readonly System.Text.RegularExpressions.Regex NodeRefBranchInRegex = new("^BRANCH\\s+(?<num>\\d+)\\s+IN\\s+(?<idx>\\d+)$");
    private static readonly System.Text.RegularExpressions.Regex NodeRefBranchOutRegex = new("^BRANCH\\s+(?<num>\\d+)\\s+OUT\\s+(?<idx>\\d+)$");

    private static GraphEdits ParseIL(string il)
    {
        var edits = new GraphEdits();
        var lines = il.Replace("\r\n", "\n").Split('\n');

        StepEdit? currentStep = null;
        var seqIndex = -1;
        var i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var line = raw.Trim();
            i++;

            if (line.Length == 0 || line.StartsWith(";")) continue;
            if (line == "INTERFACE") { SkipUntil(lines, ref i, "END_INTERFACE"); continue; }
            if (line == "PREOPERATIONS") { SkipUntil(lines, ref i, "END_PREOPERATIONS"); continue; }

            if (line == "BRANCHES")
            {
                if (seqIndex < 0) throw new InvalidOperationException("Found a BRANCHES block before any SEQUENCE header.");
                ParseBranches(lines, ref i, edits.Sequences[seqIndex]);
                continue;
            }
            if (line == "CONNECTIONS")
            {
                if (seqIndex < 0) throw new InvalidOperationException("Found a CONNECTIONS block before any SEQUENCE header.");
                ParseConnections(lines, ref i, edits.Sequences[seqIndex]);
                continue;
            }

            if (line.StartsWith("SEQUENCE"))
            {
                seqIndex++;
                edits.Sequences.Add(new SequenceEdit());
                continue;
            }
            if (line == "END_SEQUENCE" || line.StartsWith("GRAPH_BLOCK")) continue;

            if (line.StartsWith("STEP "))
            {
                var m = StepHeaderRegex.Match(line);
                if (!m.Success) throw new InvalidOperationException($"Could not parse STEP header: '{line}'");
                currentStep = new StepEdit
                {
                    Number = m.Groups["num"].Value,
                    Name = m.Groups["name"].Value,
                    Init = m.Groups["init"].Success,
                    MaxTime = m.Groups["max"].Success ? m.Groups["max"].Value : null,
                    WarnTime = m.Groups["warn"].Success ? m.Groups["warn"].Value : null,
                    Actions = new List<ActionEdit>(),
                    SequenceIndex = seqIndex,
                };
                edits.Steps.Add(currentStep);
                continue;
            }

            if (line == "END_STEP") { currentStep = null; continue; }

            if (currentStep != null && line.StartsWith("ACTION "))
            {
                var m = ActionRegex.Match(line);
                if (!m.Success) continue;
                var label = m.Groups["label"].Value.Trim();
                var parts = label.Split(' ');
                var actionEdit = new ActionEdit { Text = m.Groups["text"].Value };
                if (parts.Length == 2) { actionEdit.Event = parts[0]; actionEdit.Qualifier = parts[1]; }
                else if (parts.Length == 1 && parts[0].Length > 0) { actionEdit.Qualifier = parts[0]; }
                currentStep.Actions!.Add(actionEdit);
                continue;
            }

            if (currentStep != null && line.StartsWith("SUPERVISION:"))
            {
                currentStep.Supervision = line.Substring("SUPERVISION:".Length).Trim();
                continue;
            }

            if (currentStep != null && line.StartsWith("INTERLOCK:"))
            {
                currentStep.Interlock = line.Substring("INTERLOCK:".Length).Trim();
                continue;
            }

            if (line.StartsWith("TRANSITION "))
            {
                var m = TransitionRegex.Match(line);
                if (!m.Success) throw new InvalidOperationException($"Could not parse TRANSITION line: '{line}'");
                edits.Transitions.Add(new TransitionEdit
                {
                    Number = m.Groups["num"].Value,
                    Name = m.Groups["name"].Value,
                    Condition = m.Groups["expr"].Value,
                    SequenceIndex = seqIndex,
                });
                continue;
            }
        }

        return edits;
    }

    private static void ParseBranches(string[] lines, ref int i, SequenceEdit seqEdit)
    {
        seqEdit.SawBranches = true;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            i++;
            if (line == "END_BRANCHES") return;
            if (line.Length == 0 || line.StartsWith(";")) continue;

            var m = BranchLineRegex.Match(line);
            if (!m.Success) throw new InvalidOperationException($"Could not parse BRANCH line: '{line}'");
            seqEdit.Branches.Add(new BranchEdit
            {
                Number = m.Groups["num"].Value,
                Type = m.Groups["type"].Value,
                Cardinality = m.Groups["card"].Value,
            });
        }
        throw new InvalidOperationException("BRANCHES block is missing its END_BRANCHES.");
    }

    private static void ParseConnections(string[] lines, ref int i, SequenceEdit seqEdit)
    {
        seqEdit.SawConnections = true;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            i++;
            if (line == "END_CONNECTIONS") return;
            if (line.Length == 0 || line.StartsWith(";")) continue;

            var m = ConnectionLineRegex.Match(line);
            if (!m.Success) throw new InvalidOperationException($"Could not parse CONNECTIONS line: '{line}'");
            seqEdit.Connections.Add(new ConnectionEdit
            {
                From = ParseNodeRef(m.Groups["from"].Value.Trim()),
                To = ParseNodeRef(m.Groups["to"].Value.Trim()),
                Jump = m.Groups["jump"].Success,
            });
        }
        throw new InvalidOperationException("CONNECTIONS block is missing its END_CONNECTIONS.");
    }

    private static NodeRefEdit ParseNodeRef(string text)
    {
        var m = NodeRefStepRegex.Match(text);
        if (m.Success) return new NodeRefEdit { StepNumber = m.Groups["num"].Value };

        m = NodeRefTransRegex.Match(text);
        if (m.Success) return new NodeRefEdit { TransitionNumber = m.Groups["num"].Value };

        m = NodeRefBranchInRegex.Match(text);
        if (m.Success) return new NodeRefEdit { BranchNumber = m.Groups["num"].Value, BranchIn = m.Groups["idx"].Value };

        m = NodeRefBranchOutRegex.Match(text);
        if (m.Success) return new NodeRefEdit { BranchNumber = m.Groups["num"].Value, BranchOut = m.Groups["idx"].Value };

        throw new InvalidOperationException($"Could not parse connection endpoint: '{text}'");
    }

    private static void SkipUntil(string[] lines, ref int i, string endMarker)
    {
        while (i < lines.Length && lines[i].Trim() != endMarker) i++;
        if (i < lines.Length) i++; // consume the end marker itself
    }

    // --- Boolean/comparison expression parser (write direction) ---

    private enum ExprKind { And, Or, Not, LocalAccess, GlobalAccess, Literal, OpaqueCall }

    private sealed class ExprNode
    {
        public ExprKind Kind;
        public string Text = ""; // dotted path (Local/Global), literal text, or function name (OpaqueCall)
        public List<ExprNode> Children = new();
    }

    private static bool ContainsOpaque(ExprNode node, out string? name)
    {
        if (node.Kind == ExprKind.OpaqueCall) { name = node.Text; return true; }
        foreach (var c in node.Children)
        {
            if (ContainsOpaque(c, out name)) return true;
        }
        name = null;
        return false;
    }

    private static ExprNode ParseExpression(string text)
    {
        var tokens = Tokenize(text);
        var pos = 0;
        var node = ParseOr(tokens, ref pos);
        if (pos != tokens.Count)
        {
            throw new InvalidOperationException($"Unexpected trailing content near '{tokens[pos]}'.");
        }
        return node;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '(' or ')' or ',') { tokens.Add(c.ToString()); i++; continue; }
            if (c == '"')
            {
                var start = i;
                i++;
                while (i < text.Length && text[i] != '"') i++;
                if (i >= text.Length) throw new InvalidOperationException("Unterminated quoted name.");
                i++; // consume closing quote
                // Greedily continue through a following .Component.Component chain, if any.
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.')) i++;
                tokens.Add(text.Substring(start, i - start));
                continue;
            }
            // Word: run of characters that aren't whitespace/paren/comma/quote.
            var wstart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or ',' or '"')) i++;
            tokens.Add(text.Substring(wstart, i - wstart));
        }
        return tokens;
    }

    private static ExprNode ParseOr(List<string> tokens, ref int pos)
    {
        var left = ParseAnd(tokens, ref pos);
        if (pos >= tokens.Count || tokens[pos] != "OR") return left;

        var node = new ExprNode { Kind = ExprKind.Or };
        node.Children.Add(left);
        while (pos < tokens.Count && tokens[pos] == "OR")
        {
            pos++;
            node.Children.Add(ParseAnd(tokens, ref pos));
        }
        return node;
    }

    private static ExprNode ParseAnd(List<string> tokens, ref int pos)
    {
        var left = ParseUnary(tokens, ref pos);
        if (pos >= tokens.Count || tokens[pos] != "AND") return left;

        var node = new ExprNode { Kind = ExprKind.And };
        node.Children.Add(left);
        while (pos < tokens.Count && tokens[pos] == "AND")
        {
            pos++;
            node.Children.Add(ParseUnary(tokens, ref pos));
        }
        return node;
    }

    private static ExprNode ParseUnary(List<string> tokens, ref int pos)
    {
        if (pos < tokens.Count && tokens[pos] == "NOT")
        {
            pos++;
            return new ExprNode { Kind = ExprKind.Not, Children = { ParseUnary(tokens, ref pos) } };
        }
        return ParsePrimary(tokens, ref pos);
    }

    private static ExprNode ParsePrimary(List<string> tokens, ref int pos)
    {
        if (pos >= tokens.Count) throw new InvalidOperationException("Unexpected end of expression.");
        var tok = tokens[pos];

        if (tok == "(")
        {
            pos++;
            var inner = ParseOr(tokens, ref pos);
            if (pos >= tokens.Count || tokens[pos] != ")") throw new InvalidOperationException("Expected ')'.");
            pos++;
            return inner;
        }

        if (tok.StartsWith("#"))
        {
            pos++;
            return new ExprNode { Kind = ExprKind.LocalAccess, Text = tok.Substring(1) };
        }

        if (tok.StartsWith("\""))
        {
            pos++;
            return new ExprNode { Kind = ExprKind.GlobalAccess, Text = tok };
        }

        // A bare word immediately followed by '(' is a function call (opaque - comparisons, timers, etc.).
        if (pos + 1 < tokens.Count && tokens[pos + 1] == "(")
        {
            var funcName = tok;
            pos += 2; // consume name and '('
            var args = new List<ExprNode>();
            if (pos < tokens.Count && tokens[pos] != ")")
            {
                args.Add(ParseOr(tokens, ref pos));
                while (pos < tokens.Count && tokens[pos] == ",")
                {
                    pos++;
                    args.Add(ParseOr(tokens, ref pos));
                }
            }
            if (pos >= tokens.Count || tokens[pos] != ")") throw new InvalidOperationException($"Expected ')' after arguments to '{funcName}'.");
            pos++;
            return new ExprNode { Kind = ExprKind.OpaqueCall, Text = funcName, Children = args };
        }

        // Anything else (true/false, t#10S, 16#0100, plain numbers, quoted string constants like
        // "SwitchToPressurePID" without a following dotted path) is a verbatim literal.
        pos++;
        return new ExprNode { Kind = ExprKind.Literal, Text = tok };
    }

    // --- Expression tree -> fresh FlgNet XML ---

    private static XElement BuildFlgNet(ExprNode root, HashSet<string> knownConstants, string sinkPartName)
    {
        var parts = new List<XElement>();
        var wires = new List<XElement>();
        // Real samples never start at 1 (they're always well into double digits) and UId=1
        // specifically triggers a Siemens import error ("IdentCon does not exist at the object
        // with UID '1'") - start from a safe base instead. Arbitrary large UId values (offset
        // +100000) import fine, so self-consistency (not matching any original numbering) is
        // genuinely all that matters here - 1 just happens to be special-cased/reserved by
        // Siemens' importer.
        var nextUId = 21;
        int NewUId() => nextUId++;

        // Wraps any node so the same "wire operand into a gate pin, honoring NOT via a Negated
        // marker" logic in BuildGateOrLeaf handles every shape uniformly, including a bare
        // single-condition network (no top-level AND/OR in the original text).
        var effectiveRoot = root.Kind is ExprKind.And or ExprKind.Or
            ? root
            : new ExprNode { Kind = ExprKind.And, Children = { root } };

        var (gateUId, _) = BuildGate(effectiveRoot, knownConstants, parts, wires, NewUId);

        var sinkUId = NewUId();
        parts.Add(new XElement(GraphNs + "Part", new XAttribute("Name", sinkPartName), new XAttribute("UId", sinkUId)));
        // The gate is always a Part (never a bare Access), so its output is always referenced
        // via a named "out" pin, not IdentCon - see the isPart note on BuildOperand below.
        wires.Add(new XElement(GraphNs + "Wire", new XAttribute("UId", NewUId()),
            new XElement(GraphNs + "NameCon", new XAttribute("UId", gateUId), new XAttribute("Name", "out")),
            new XElement(GraphNs + "NameCon", new XAttribute("UId", sinkUId), new XAttribute("Name", "in"))));

        return new XElement(GraphNs + "FlgNet",
            new XElement(GraphNs + "Parts", parts),
            new XElement(GraphNs + "Wires", wires));
    }

    private static (int uid, bool isPart) BuildGate(ExprNode gateNode, HashSet<string> knownConstants, List<XElement> parts, List<XElement> wires, Func<int> newUId)
    {
        var partName = gateNode.Kind == ExprKind.And ? "A" : "O";

        // Post-order: build/number every child (leaf or nested gate) BEFORE the gate that
        // consumes them. Real samples always have leaf UIds < gate UId < sink UId, in both
        // numeric value and document order - numbering the gate first (as an earlier version of
        // this code did) produced a gate whose own UId was lower than, and positioned before,
        // parts that reference it, which TIA Portal's importer rejected outright.
        var childInfos = new List<(int uid, bool isPart, string pin, bool negated)>();
        for (var idx = 0; idx < gateNode.Children.Count; idx++)
        {
            var pin = "in" + (idx + 1);
            var operand = gateNode.Children[idx];
            var negated = false;
            if (operand.Kind == ExprKind.Not)
            {
                negated = true;
                operand = operand.Children[0];
            }
            var (sourceUId, sourceIsPart) = BuildOperand(operand, knownConstants, parts, wires, newUId);
            childInfos.Add((sourceUId, sourceIsPart, pin, negated));
        }

        var gateUId = newUId();
        var partEl = new XElement(GraphNs + "Part", new XAttribute("Name", partName), new XAttribute("UId", gateUId));
        // Real samples always carry this, even for a 2-input gate - it's not just for >2 inputs.
        partEl.Add(new XElement(GraphNs + "TemplateValue", new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), gateNode.Children.Count));
        foreach (var (_, _, pin, negated) in childInfos)
        {
            if (negated) partEl.Add(new XElement(GraphNs + "Negated", new XAttribute("Name", pin)));
        }
        parts.Add(partEl);

        foreach (var (sourceUId, sourceIsPart, pin, _) in childInfos)
        {
            // A leaf Access has one implicit value, referenced via IdentCon. A nested gate (Part)
            // has an explicit named "out" pin and must be referenced via NameCon instead
            // (PreOperations' Part "O" feeding a Coil uses NameCon Name="out", while Access
            // leaves feeding gates use IdentCon). Using IdentCon for a Part source causes
            // "IdentCon does not exist at the object with UID ...".
            var sourceEl = sourceIsPart
                ? new XElement(GraphNs + "NameCon", new XAttribute("UId", sourceUId), new XAttribute("Name", "out"))
                : new XElement(GraphNs + "IdentCon", new XAttribute("UId", sourceUId));
            wires.Add(new XElement(GraphNs + "Wire", new XAttribute("UId", newUId()),
                sourceEl,
                new XElement(GraphNs + "NameCon", new XAttribute("UId", gateUId), new XAttribute("Name", pin))));
        }

        return (gateUId, true);
    }

    private static (int uid, bool isPart) BuildOperand(ExprNode node, HashSet<string> knownConstants, List<XElement> parts, List<XElement> wires, Func<int> newUId)
    {
        if (node.Kind is ExprKind.And or ExprKind.Or)
        {
            return BuildGate(node, knownConstants, parts, wires, newUId);
        }

        var uid = newUId();
        switch (node.Kind)
        {
            case ExprKind.LocalAccess:
                if (!node.Text.Contains('.') && knownConstants.Contains(node.Text))
                {
                    parts.Add(new XElement(GraphNs + "Access", new XAttribute("Scope", "LocalConstant"), new XAttribute("UId", uid),
                        new XElement(GraphNs + "Constant", new XAttribute("Name", node.Text))));
                }
                else
                {
                    var components = node.Text.Split('.').Select(c => new XElement(GraphNs + "Component", new XAttribute("Name", c)));
                    parts.Add(new XElement(GraphNs + "Access", new XAttribute("Scope", "LocalVariable"), new XAttribute("UId", uid),
                        new XElement(GraphNs + "Symbol", components)));
                }
                break;

            case ExprKind.GlobalAccess:
            {
                // node.Text looks like: "DbName".Component.Component...
                var closeQuote = node.Text.IndexOf('"', 1);
                var dbName = node.Text.Substring(1, closeQuote - 1);
                var rest = node.Text.Length > closeQuote + 1 ? node.Text.Substring(closeQuote + 1).TrimStart('.') : "";
                var comps = new List<XElement> { new(GraphNs + "Component", new XAttribute("Name", dbName)) };
                if (rest.Length > 0) comps.AddRange(rest.Split('.').Select(c => new XElement(GraphNs + "Component", new XAttribute("Name", c))));
                parts.Add(new XElement(GraphNs + "Access", new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", uid),
                    new XElement(GraphNs + "Symbol", comps)));
                break;
            }

            case ExprKind.Literal:
                parts.Add(new XElement(GraphNs + "Access", new XAttribute("Scope", "TypedConstant"), new XAttribute("UId", uid),
                    new XElement(GraphNs + "Constant", new XElement(GraphNs + "ConstantValue", node.Text))));
                break;

            default:
                throw new InvalidOperationException($"Unexpected node kind '{node.Kind}' during regeneration.");
        }

        return (uid, false);
    }
}
