using System.Text.RegularExpressions;

namespace D365FO.Core.Validation;

/// <summary>
/// What a name BINDS to: the member an intrinsic names, the method a Chain of Command wrapper or
/// an event handler targets, and calls that reach an obsolete method. The rest of the resolver
/// proves that names exist; these read the declaration a name resolves to, which is what breaks
/// when a dependency is updated underneath code that compiled yesterday.
/// </summary>
/// <remarks>
/// Every error is a case xppc rejects. Whatever the compiler accepts is at most a warning, and
/// nothing is said where the index cannot answer: an owner it does not hold, a base class outside
/// it, a declaration that cannot be read, a member an extension contributes.
/// </remarks>
public static partial class ReferenceResolver
{
    private sealed record MemberSpec(string Spelling, DeclaringKind[] Owners, bool IsDelegate, bool IsStatic, string Twin);

    /// <summary>The intrinsics whose second argument names a method or delegate.</summary>
    private static readonly Dictionary<string, MemberSpec> MemberIntrinsics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["methodstr"] = new("methodStr", [DeclaringKind.Class], false, false, "staticMethodStr"),
        ["staticmethodstr"] = new("staticMethodStr", [DeclaringKind.Class], false, true, "methodStr"),
        ["tablemethodstr"] = new("tableMethodStr", [DeclaringKind.Table], false, false, "tableStaticMethodStr"),
        ["tablestaticmethodstr"] = new("tableStaticMethodStr", [DeclaringKind.Table], false, true, "tableMethodStr"),
        ["delegatestr"] = new("delegateStr", [DeclaringKind.Class, DeclaringKind.Table], true, false, "staticDelegateStr"),
        ["staticdelegatestr"] = new("staticDelegateStr", [DeclaringKind.Class, DeclaringKind.Table], true, true, "delegateStr"),
    };

    private static MemberResolution SafeLookupMember(
        IReferenceIndex index, string owner, string member, DeclaringKind kind, string? ignoreExtensionClass = null)
    {
        try { return index.LookupMember(owner, member, kind, ignoreExtensionClass); }
        catch { return MemberResolution.Unknown; }
    }

    private static bool IsKernelMember(DeclaringKind kind, string member)
        => kind == DeclaringKind.Table
            ? TableBuiltinMethods.Contains(member) || Knowledge.TableDataMethods.Lookup(member) is not null
            : ObjectBuiltinMethods.Contains(member);

    /// <summary>
    /// The member argument of <c>methodStr</c> and its twins. xppc is stricter than a name lookup:
    /// the member must be declared on the named type itself (extension classes count; inherited
    /// members and kernel table methods the table does not override do not), be of the matching
    /// static/instance and method/delegate kind, and <c>methodStr</c> takes a class only.
    /// Returns null when the member is proven or the index cannot say.
    /// </summary>
    private static ReferenceViolation? CheckIntrinsicMember(
        IReferenceIndex index, MemberSpec spec, string target, string member, int line, IReadOnlyList<string> kinds)
    {
        var identifier = $"{target}.{member}";
        bool Is(string kind) => kinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

        // delegateStr(X, …) names the FORM's delegate when a form shares its name with a table or
        // class, and form delegates are not indexed.
        if (spec.IsDelegate && Is("form")) return null;

        DeclaringKind? owner = null;
        foreach (var kind in spec.Owners)
        {
            if (Is(kind == DeclaringKind.Class ? "class" : "table")) { owner = kind; break; }
        }

        if (owner is not { } declaring)
        {
            if (spec.Spelling == "methodStr" && (Is("table") || Is("form")))
            {
                var what = Is("table") ? "table" : "form";
                return new ReferenceViolation("wrong-member-kind", "error", line, identifier,
                    $"methodStr() takes a class; \"{target}\" is a {what}. Use " +
                    $"{(what == "table" ? "tableMethodStr" : "formMethodStr")}({target}, {member}).");
            }

            return null; // a view, map, entity or form owner: not answerable here
        }

        var found = SafeLookupMember(index, target, member, declaring);
        if (found.Chain.Count == 0) return null; // the type itself is not indexed

        var onTarget = found.State is MemberState.Found or MemberState.Contributed && found.Chain.Count == 1;
        if (!onTarget)
        {
            if (declaring == DeclaringKind.Class && ObjectBuiltinMethods.Contains(member)) return null;
            var noun = spec.IsDelegate ? "delegate" : "method";
            if (found.State == MemberState.Found)
            {
                return new ReferenceViolation("unknown-method", "error", line, identifier,
                    $"\"{member}\" is declared on {found.Owner}, a base of {target}, and {spec.Spelling}() does not " +
                    $"see inherited members. Name the declaring type: {spec.Spelling}({found.Owner}, {member}).");
            }

            return new ReferenceViolation("unknown-method", "error", line, identifier,
                $"{target} declares no {noun} \"{member}\"" +
                (IsKernelMember(declaring, member) ? " (a kernel method it does not override does not count)" : "") +
                $", so the compiler rejects {spec.Spelling}(). Check with `d365fo get " +
                $"{(declaring == DeclaringKind.Table ? "table" : "class")} {target}`; if it was just added, refresh the index.");
        }

        if (found.State != MemberState.Found || found.Source is null) return null;
        if (XppDeclaration.Parse(found.Source, member) is not { } decl) return null;

        if (spec.IsDelegate && !decl.IsDelegate)
        {
            return new ReferenceViolation("wrong-member-kind", "error", line, identifier,
                $"\"{decl.Name}\" on {target} is an ordinary method, not a delegate. Subscribe to it with " +
                "[PreHandlerFor]/[PostHandlerFor] and methodStr(), or wrap it with Chain of Command.");
        }

        if (!spec.IsDelegate && decl.IsDelegate) return null;
        if (decl.IsStatic != spec.IsStatic)
        {
            return new ReferenceViolation("wrong-member-kind", "error", line, identifier,
                $"\"{decl.Name}\" on {target} is {(spec.IsStatic ? "an instance" : "a static")} " +
                $"{(spec.IsDelegate ? "delegate" : "method")}, which {spec.Spelling}() rejects. " +
                $"Use {spec.Twin}({target}, {decl.Name}).");
        }

        return null;
    }

    /// <summary>The classes a snippet declares, and the types it extends with <c>[ExtensionOf]</c>.</summary>
    private static HashSet<string> TypesDefinedOrExtended(string cleaned)
        => Regex.Matches(cleaned, @"\bclass\s+(\w+)|\bExtensionOf\s*\(\s*\w+Str\s*\(\s*(\w+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The finding for a call that reaches a <c>[SysObsolete]</c> method, or null.</summary>
    private static ReferenceViolation? ObsoleteCall(
        IReferenceIndex index, string owner, string member, DeclaringKind kind, int line, bool callerObsolete)
    {
        var found = SafeLookupMember(index, owner, member, kind);
        if (found.State != MemberState.Found || found.Source is null) return null;
        if (XppDeclaration.Parse(found.Source, member)?.ObsoleteError is not { } isError) return null;

        var identifier = $"{owner}.{member}";
        // The compiler does not report obsolete calls made from obsolete code.
        return new ReferenceViolation("obsolete-method", isError && !callerObsolete ? "error" : "warning", line, identifier,
            isError
                ? $"{identifier} is marked [SysObsolete] with isError = true, so the compiler rejects every call to it. " +
                  "Read the declaration for the replacement the attribute names."
                : $"{identifier} is marked [SysObsolete]; the build warns on this call. Prefer the replacement its attribute names.");
    }

    // ── Signatures ───────────────────────────────────────────────────────────

    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "int64", "real", "str", "boolean", "date", "utcdatetime", "timeofday", "guid", "container",
    };

    /// <summary>
    /// Severity of a type a wrapper or handler spells differently from its target, or null when
    /// the two agree or nothing can be said. xppc errors only where the types cannot convert
    /// (primitive against another primitive or an enum); another EDT compiles with a warning.
    /// </summary>
    private static string? TypeMismatch(IReferenceIndex index, string mine, string theirs)
    {
        if (mine.Equals(theirs, StringComparison.OrdinalIgnoreCase)) return null;
        string[] open = ["anytype", "var", "void"];
        if (open.Contains(mine, StringComparer.OrdinalIgnoreCase) || open.Contains(theirs, StringComparer.OrdinalIgnoreCase)) return null;

        string KindOf(string type)
        {
            if (PrimitiveTypes.Contains(type)) return "primitive";
            if (Knowledge.KernelEdts.Contains(type)) return "edt";
            IReadOnlyList<string> kinds;
            try { kinds = index.SymbolKinds(type); }
            catch { return "unknown"; }
            if (kinds.Contains("edt")) return "edt";
            if (kinds.Contains("enum")) return "enum";
            return kinds.Count > 0 ? "other" : "unknown";
        }

        var a = KindOf(mine);
        var b = KindOf(theirs);
        if (a == "unknown" || b == "unknown") return null;
        if (a == "primitive" && b == "primitive")
        {
            return mine.StartsWith("int", StringComparison.OrdinalIgnoreCase) && theirs.StartsWith("int", StringComparison.OrdinalIgnoreCase)
                ? "warning"
                : "error";
        }

        if ((a == "primitive" && b == "enum") || (a == "enum" && b == "primitive")) return "error";
        return "warning";
    }

    private static IEnumerable<(string Severity, string Reason)> CompareParameters(
        IReferenceIndex index, XppDeclaration mine, XppDeclaration theirs)
    {
        if (mine.Parameters.Count != theirs.Parameters.Count)
        {
            yield return ("error", $"it declares {mine.Parameters.Count} parameter(s), the target {theirs.Parameters.Count}");
            yield break;
        }

        for (int i = 0; i < mine.Parameters.Count; i++)
        {
            if (TypeMismatch(index, mine.Parameters[i].Type, theirs.Parameters[i].Type) is not { } severity) continue;
            yield return (severity,
                $"parameter {i + 1} is {mine.Parameters[i].Type}, the target declares {theirs.Parameters[i].Type}" +
                (severity == "warning" ? " (the compiler accepts a compatible type with a warning)" : ""));
        }
    }

    // ── Chain of Command and event handlers ──────────────────────────────────

    private static DeclaringKind? DeclaringKindOf(string intrinsic) => intrinsic.ToLowerInvariant() switch
    {
        "classstr" => DeclaringKind.Class,
        "tablestr" => DeclaringKind.Table,
        _ => null, // formStr and the rest: their members are not in the index
    };

    /// <summary>Chain of Command wrappers in an <c>[ExtensionOf(classStr|tableStr(X))]</c> class.</summary>
    private static IEnumerable<ReferenceViolation> CheckWrappers(string cleaned, IReferenceIndex index)
    {
        var extension = Regex.Match(cleaned, @"\bExtensionOf\s*\(\s*(class|table)Str\s*\(\s*(\w+)\s*\)\s*\)", RegexOptions.IgnoreCase);
        if (!extension.Success) yield break;
        var target = extension.Groups[2].Value;
        var kind = DeclaringKindOf(extension.Groups[1].Value + "Str")!.Value;
        // The extension being validated may already be indexed; its own wrappers prove nothing.
        var own = Regex.Match(cleaned, @"\bclass\s+(\w+)", RegexOptions.IgnoreCase);
        var ownClass = own.Success ? own.Groups[1].Value : null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match next in Regex.Matches(cleaned, @"\bnext\s+(\w+)\s*\(", RegexOptions.IgnoreCase))
        {
            var name = next.Groups[1].Value;
            if (!seen.Add(name)) continue;

            var mine = XppDeclaration.Parse(cleaned, name);
            var line = LineOf(cleaned, next.Index);
            var identifier = $"{target}.{name}";
            var found = SafeLookupMember(index, target, name, kind, ownClass);

            if (found.State == MemberState.Missing)
            {
                if (IsKernelMember(kind, name)) continue;
                yield return new ReferenceViolation("coc-target", "error", line, identifier,
                    $"Chain of Command wrapper of a method {target} does not have: \"{name}\" is declared on none of " +
                    $"{string.Join(" → ", found.Chain)} and is not a kernel method, so \"next {name}()\" cannot compile. " +
                    "If an update renamed or removed it, find the replacement; if it was just added, refresh the index.");
                continue;
            }

            if (found.State != MemberState.Found || found.Source is null) continue;
            if (XppDeclaration.Parse(found.Source, name) is not { } target_) continue;

            var reasons = new List<(string Severity, string Reason)>();
            if (target_.Hookable == false)
                reasons.Add(("error", "the target is marked [Hookable(false)], which also forbids wrapping"));
            if (target_.Has("private") && target_.Hookable != true)
                reasons.Add(("error", "the target is private; only public and protected methods can be wrapped"));
            if (target_.Has("final") && target_.Wrappable != true)
                reasons.Add(("error", "the target is final and has no [Wrappable(true)]"));
            if (target_.Wrappable == false && target_.Has("public"))
                reasons.Add(("error", "the target is a public method marked [Wrappable(false)]"));

            if (mine is not null)
            {
                if (target_.IsStatic != mine.IsStatic)
                {
                    reasons.Add(target_.IsStatic
                        ? ("error", "the target is static, the wrapper is not")
                        : ("warning", "the wrapper is static, the target is not"));
                }

                reasons.AddRange(CompareParameters(index, mine, target_));
                if (TypeMismatch(index, mine.ReturnType, target_.ReturnType) is { } returns)
                    reasons.Add((returns, $"the wrapper returns {mine.ReturnType}, the target {target_.ReturnType}"));
            }

            if (reasons.Count == 0) continue;
            var where = string.Equals(found.Owner, target, StringComparison.OrdinalIgnoreCase)
                ? target
                : $"{found.Owner} (inherited by {target})";
            yield return new ReferenceViolation("coc-target",
                reasons.Any(r => r.Severity == "error") ? "error" : "warning", line, identifier,
                $"Chain of Command wrapper does not match its target on {where}: " +
                $"{string.Join("; ", reasons.Select(r => r.Reason))}. The target is declared as " +
                $"\"{target_.Render()}\"; a wrapper repeats it without default values.");
        }
    }

    /// <summary>The handler method declared right after the attribute that ends at <paramref name="from"/>.</summary>
    private static XppDeclaration? HandlerAfter(string cleaned, int from)
    {
        var rest = cleaned[from..];
        var m = Regex.Match(rest,
            @"^[^{};]*?\b(?:public|private|protected|internal|static|final)\b[^({};]*?\b(\w+)\s*\(",
            RegexOptions.IgnoreCase);
        return m.Success ? XppDeclaration.Parse(rest, m.Groups[1].Value) : null;
    }

    private static IEnumerable<ReferenceViolation> CheckHandlers(string cleaned, IReferenceIndex index)
    {
        // Whether the delegate exists and is named with the right intrinsic is the member check's
        // finding, reported once there. Here: the handler's parameters against the delegate's.
        foreach (Match m in Regex.Matches(cleaned,
            @"\bSubscribesTo\s*\(\s*(\w+Str)\s*\(\s*\w+\s*\)\s*,\s*(delegateStr|staticDelegateStr)\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)\s*\)",
            RegexOptions.IgnoreCase))
        {
            if (DeclaringKindOf(m.Groups[1].Value) is not { } kind) continue;
            var isStatic = m.Groups[2].Value.Equals("staticDelegateStr", StringComparison.OrdinalIgnoreCase);
            var owner = m.Groups[3].Value;
            var name = m.Groups[4].Value;
            var found = SafeLookupMember(index, owner, name, kind);
            if (found.State != MemberState.Found || found.Source is null || found.Chain.Count != 1) continue;
            if (XppDeclaration.Parse(found.Source, name) is not { IsDelegate: true } target || target.IsStatic != isStatic) continue;
            if (HandlerAfter(cleaned, m.Index + m.Length) is not { } handler) continue;

            var reasons = CompareParameters(index, handler, target).ToList();
            if (reasons.Count == 0) continue;
            yield return new ReferenceViolation("handler-target",
                reasons.Any(r => r.Severity == "error") ? "error" : "warning", LineOf(cleaned, m.Index), $"{owner}.{name}",
                $"Event handler does not match its delegate: {string.Join("; ", reasons.Select(r => r.Reason))}. " +
                $"The delegate is declared as \"{target.Render()}\"; the handler repeats its parameters.");
        }

        foreach (Match m in Regex.Matches(cleaned,
            @"\b(Pre|Post)HandlerFor\s*\(\s*(\w+Str)\s*\(\s*\w+\s*\)\s*,\s*\w*MethodStr\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)\s*\)",
            RegexOptions.IgnoreCase))
        {
            if (DeclaringKindOf(m.Groups[2].Value) is not { } kind) continue;
            var owner = m.Groups[3].Value;
            var name = m.Groups[4].Value;
            // A missing method is the member check's finding.
            var found = SafeLookupMember(index, owner, name, kind);
            if (found.State != MemberState.Found || found.Source is null) continue;
            if (XppDeclaration.Parse(found.Source, name) is not { } target) continue;

            var reasons = new List<string>();
            if (target.Hookable == false) reasons.Add("the target is marked [Hookable(false)]");
            if (target.Has("private") && target.Hookable != true) reasons.Add("the target is private and not marked [Hookable(true)]");
            if (reasons.Count == 0) continue;
            yield return new ReferenceViolation("handler-target", "error", LineOf(cleaned, m.Index), $"{owner}.{name}",
                $"[{m.Groups[1].Value}HandlerFor] cannot subscribe to {owner}.{name}: {string.Join(" and ", reasons)}. " +
                "Subscribe to a delegate or a hookable public or protected method instead.");
        }
    }
}
