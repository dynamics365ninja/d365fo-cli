namespace D365FO.Core.Scaffolding;

/// <summary>
/// One overridable framework method: the X++ signature needed to emit a
/// compile-safe <c>super()</c> stub. <see cref="ReturnType"/> drives whether
/// the stub captures and returns a <c>ret</c> value; <see cref="Parameters"/>
/// is the verbatim parameter list (X++ syntax) and <see cref="SuperArgs"/> the
/// argument list forwarded to <c>super(...)</c>.
/// </summary>
public sealed record FormMethodSignature(
    string Name,
    string ReturnType,
    string Parameters = "",
    string SuperArgs = "");

/// <summary>
/// Curated catalog of the commonly-overridden D365FO <c>FormDataSource</c> and
/// <c>FormControl</c> framework methods, with their exact X++ signatures.
///
/// Why a catalog and not a free-form name: an X++ override must match the base
/// method's signature (return type AND parameters), otherwise it does not
/// compile / does not actually override. A naive <c>public void {name}()</c>
/// stub (as the CoC scaffolder emits) produces broken code for methods like
/// <c>active()</c> (returns int) or <c>validateWrite()</c> (returns boolean).
/// Keeping the canonical signatures here lets <see cref="FormMethodScaffolder"/>
/// generate stubs that are correct against the framework — consistent with the
/// project's "grounded, no hallucinated signatures" guarantee.
///
/// The set is intentionally a high-confidence subset. For a method outside the
/// catalog the caller can pass an explicit <c>--return-type</c> escape hatch;
/// the scaffolder then emits a parameterless stub and warns that the signature
/// must be verified against the framework.
/// </summary>
public static class FormMethodCatalog
{
    public enum Target { DataSource, Control }

    // FormDataSource — verified canonical signatures.
    private static readonly FormMethodSignature[] DataSourceMethods =
    {
        new("active",         "int"),
        new("init",           "void"),
        new("executeQuery",   "void"),
        new("delete",         "void"),
        new("write",          "void"),
        new("create",         "void",    "boolean _append = false", "_append"),
        new("validateWrite",  "boolean"),
        new("validateDelete", "boolean"),
        new("initValue",      "void"),
        new("linkActive",     "void"),
        new("markChanged",    "void"),
        new("refresh",        "void"),
        new("reread",         "void"),
        new("first",          "void"),
        new("last",           "void"),
    };

    // FormControl — verified canonical signatures.
    private static readonly FormMethodSignature[] ControlMethods =
    {
        new("modified",   "boolean"),
        new("validate",   "boolean"),
        new("leave",      "boolean"),
        new("clicked",    "void"),
        new("enter",      "void"),
        new("gotFocus",   "void"),
        new("lostFocus",  "void"),
        new("jumpRef",    "void"),
        new("lookup",     "void"),
        new("textChange", "void"),
    };

    private static FormMethodSignature[] For(Target target) =>
        target == Target.DataSource ? DataSourceMethods : ControlMethods;

    /// <summary>All catalogued method names for the given target (ordinal-sorted by definition order).</summary>
    public static IReadOnlyList<FormMethodSignature> List(Target target) => For(target);

    /// <summary>
    /// Look up a catalogued signature by method name (case-insensitive).
    /// Returns null when the method is not in the curated set.
    /// </summary>
    public static FormMethodSignature? TryGet(Target target, string methodName) =>
        For(target).FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
}
