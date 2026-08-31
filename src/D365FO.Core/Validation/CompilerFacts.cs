// <copyright file="CompilerFacts.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Reflection;
using System.Text.Json;

namespace D365FO.Core.Validation;

/// <summary>Accepted argument counts of a run-time function. <see cref="Max"/> is null when variadic.</summary>
/// <param name="Min">Fewest arguments the compiler accepts.</param>
/// <param name="Max">Most arguments the compiler accepts, or null when the function is variadic.</param>
public sealed record RuntimeArity(int Min, int? Max)
{
    /// <summary>True when the compiler accepts <paramref name="count"/> arguments.</summary>
    public bool Accepts(int count) => Max is null ? count >= Min : count >= Min && count <= Max;

    /// <summary>How the compiler describes the accepted argument counts, for a fix message.</summary>
    public string Describe()
    {
        if (Max is null)
        {
            return "a variable number of arguments";
        }

        if (Min == Max)
        {
            return $"{Max} argument(s)";
        }

        return $"{Min}–{Max} argument(s) (the last {Max - Min} optional)";
    }
}

/// <summary>
/// The compiler's own answers about X++, as a queryable surface.
///
/// Everything here comes from <c>compiler-facts.json</c> (embedded), a copy of the upstream
/// MCP server's <c>eval/compiler-facts.snapshot.json</c>, captured on a VM from the shipped
/// compiler:
/// <list type="bullet">
/// <item>reserved words → <c>XppParser.Keywords.KeywordHashSet</c> (reflection)</item>
/// <item>intrinsics → <c>XppCompiler.Intrinsics.IntrinsicFunctionInfo</c> (reflection)</item>
/// <item>run-time arities → xppc probe builds ("expects N argument(s)" / "is missing argument
/// K"), so optional trailing parameters are visible as a min/max range rather than guessed</item>
/// </list>
///
/// Rules and knowledge read from here instead of carrying their own copy of a list. The
/// reason is a measured one: upstream's hand-maintained arity table said <c>date2Str</c> took
/// 8 arguments and the shipped platform calls it with 7 (161 times). A table that is not the
/// compiler's own drifts from it.
/// </summary>
public static class CompilerFacts
{
    private const string ResourceName = "D365FO.Core.Validation.compiler-facts.json";

    private static readonly Lazy<Snapshot> _snapshot = new(Load, isThreadSafe: true);

    /// <summary>Version of the xppc compiler the facts were captured from.</summary>
    public static string CompilerVersion => _snapshot.Value.CompilerVersion;

    /// <summary>When the snapshot was captured.</summary>
    public static string CapturedAt => _snapshot.Value.CapturedAt;

    /// <summary>The parser's reserved words (115), lower-cased. Includes the exempted ones.</summary>
    public static IReadOnlyCollection<string> Keywords => _snapshot.Value.Keywords;

    /// <summary>
    /// True when <paramref name="word"/> is a reserved word the parser will not accept as an
    /// identifier. X++ is case-insensitive. <c>in</c> is reserved but exempted, so it is NOT
    /// reported.
    /// </summary>
    public static bool IsReservedKeyword(string word)
    {
        var w = word.ToLowerInvariant();
        return _snapshot.Value.Keywords.Contains(w) && !_snapshot.Value.Exempted.Contains(w);
    }

    /// <summary>Canonical spelling + argument count of a compile-time intrinsic, or null when it is not one.</summary>
    public static (string Name, int Args)? IntrinsicInfo(string name)
        => _snapshot.Value.Intrinsics.TryGetValue(name.ToLowerInvariant(), out var info) ? info : null;

    /// <summary>Canonical spelling + accepted argument counts of a run-time function, or null.</summary>
    public static (string Name, RuntimeArity Arity)? RuntimeFunctionInfo(string name)
        => _snapshot.Value.RuntimeFunctions.TryGetValue(name.ToLowerInvariant(), out var info) ? info : null;

    /// <summary>A name that looks predefined but does not exist on this platform version.</summary>
    public static bool IsUnknownFunction(string name)
        => _snapshot.Value.Unknown.Contains(name.ToLowerInvariant());

    /// <summary>A predefined function the compiler reports as obsolete.</summary>
    public static bool IsObsoleteFunction(string name)
        => _snapshot.Value.Obsolete.Contains(name.ToLowerInvariant());

    private sealed record Snapshot(
        string CapturedAt,
        string CompilerVersion,
        HashSet<string> Keywords,
        HashSet<string> Exempted,
        Dictionary<string, (string Name, int Args)> Intrinsics,
        Dictionary<string, (string Name, RuntimeArity Arity)> RuntimeFunctions,
        HashSet<string> Unknown,
        HashSet<string> Obsolete);

    private static Snapshot Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var keywords = root.GetProperty("keywords").EnumerateArray()
            .Select(e => e.GetString()!.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var exempted = root.GetProperty("exemptedKeywords").EnumerateArray()
            .Select(e => e.GetString()!.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        var intrinsics = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        foreach (var p in root.GetProperty("intrinsics").EnumerateObject())
        {
            intrinsics[p.Name.ToLowerInvariant()] = (p.Name, p.Value.GetInt32());
        }

        var runtime = new Dictionary<string, (string, RuntimeArity)>(StringComparer.Ordinal);
        foreach (var p in root.GetProperty("runtimeFunctions").EnumerateObject())
        {
            var min = p.Value.GetProperty("min").GetInt32();
            var maxEl = p.Value.GetProperty("max");
            int? max = maxEl.ValueKind == JsonValueKind.String ? null : maxEl.GetInt32();
            runtime[p.Name.ToLowerInvariant()] = (p.Name, new RuntimeArity(min, max));
        }

        var unknown = root.GetProperty("unknownFunctions").EnumerateArray()
            .Select(e => e.GetString()!.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var obsolete = root.GetProperty("obsoleteFunctions").EnumerateArray()
            .Select(e => e.GetString()!.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        return new Snapshot(
            root.GetProperty("capturedAt").GetString()!,
            root.GetProperty("compilerVersion").GetString()!,
            keywords,
            exempted,
            intrinsics,
            runtime,
            unknown,
            obsolete);
    }
}
