// <copyright file="TableDataMethods.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core.Knowledge;

/// <summary>One data method every table inherits from a kernel type.</summary>
/// <param name="Name">Canonical AOT spelling.</param>
/// <param name="Signature">The declaration a CoC wrapper has to match exactly.</param>
/// <param name="DeclaredOn">Kernel type that declares it (<c>xRecord</c> / <c>Common</c>).</param>
/// <param name="Purpose">What wrapping it is for, in one line.</param>
/// <param name="Contract">Non-negotiables a green build will not teach.</param>
public sealed record TableDataMethod(
    string Name,
    string Signature,
    string DeclaredOn,
    string Purpose,
    IReadOnlyList<string> Contract);

/// <summary>
/// The data methods every table inherits from <c>xRecord</c> / <c>Common</c>. Port of the
/// upstream MCP server's <c>src/knowledge/tableDataMethods.ts</c>.
///
/// Those are kernel types with no AOT metadata, and the symbol index stores declared members
/// only, so a table's <c>validateWrite</c> has no row anywhere. <c>prepare change</c> reported
/// that as "not found", which reads as "the method does not exist" for the most common CoC
/// target there is and leaves the caller to invent the wrapper unaided.
///
/// The contract below is the part a green build cannot teach — above all that the pre-image is
/// <c>this.orig()</c>, already in memory, so re-reading the row by its own RecId is a database
/// round trip per write AND a different value: the current stored state rather than what this
/// buffer was fetched with.
///
/// A FALLBACK only: consulted when neither index nor XML declares the method, so a table that
/// overrides <c>insert()</c> still reports its own signature. Tables only, deliberately —
/// views, maps and data entities descend from <c>Common</c> too, but they do not all wrap
/// through <c>tableStr</c> and not every one of these methods fires on them.
/// </summary>
public static class TableDataMethods
{
    private const string NextOnce =
        "`next <method>()` must be reached exactly once and unconditionally — not inside an `if`, not after a " +
        "`return`. The compiler rejects the alternative with SYS10028 (rule COC004).";

    private const string ValidationReturn =
        "Report a failure by RETURNING false, not by throwing: `ret = checkFailed(\"@MyModel:MyLabel\");` " +
        "— checkFailed writes the message to the infolog and returns false, so every failed validation " +
        "is presented at once. `checkFailed` is a Global function, never `this.checkFailed(…)` (rule COC005).";

    private static readonly string[] PreImage =
    [
        "`this.orig()` IS the pre-image — a buffer already in memory, filled when the record was fetched. " +
        "Read the old value from it: `this.orig().MyField`.",
        "Do NOT re-read the row (`select … where x.RecId == this.RecId`, `MyTable::find(this.RecId)`). It costs a " +
        "database round trip on every single write of the table, and it returns the CURRENT stored state, which " +
        "inside a transaction is not the same thing as the values this buffer was fetched with.",
        "On an insert the pre-image is empty, so `this.orig().RecId == 0` is the test for \"new record\" — the same " +
        "test the re-select spells as \"the select found nothing\".",
    ];

    private static readonly Dictionary<string, TableDataMethod> Catalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["validateWrite"] = new(
                "validateWrite", "public boolean validateWrite()", "xRecord",
                "Gate an insert or an update of the whole record; runs for UI and X++ writes alike.",
                [.. PreImage, NextOnce, ValidationReturn]),
            ["validateField"] = new(
                "validateField", "public boolean validateField(FieldId _fieldIdToCheck)", "xRecord",
                "Gate one field as it is modified, before validateWrite runs.",
                ["Test which field you were called for: `if (_fieldIdToCheck == fieldNum(MyTable, MyField))`.", .. PreImage, NextOnce, ValidationReturn]),
            ["validateDelete"] = new(
                "validateDelete", "public boolean validateDelete()", "xRecord",
                "Gate a delete.",
                ["The buffer holds the record being deleted — no lookup is needed to see what is about to go.", NextOnce, ValidationReturn]),
            ["insert"] = new(
                "insert", "public void insert()", "xRecord",
                "Run logic around the physical insert of this buffer.",
                [
                    "There is no pre-image: `this.orig()` is an empty buffer and `this.RecId` is still 0 until next insert() returns.",
                    "Validation belongs in validateWrite, which the framework calls first — insert() is for side effects.",
                    NextOnce,
                ]),
            ["update"] = new(
                "update", "public void update()", "xRecord",
                "Run logic around the physical update of this buffer.",
                [.. PreImage, "Validation belongs in validateWrite, which the framework calls first — update() is for side effects.", NextOnce]),
            ["delete"] = new(
                "delete", "public void delete()", "xRecord",
                "Run logic around the physical delete of this buffer.",
                ["The buffer still holds the record while the wrapper runs — read what you need before next delete().", NextOnce]),
            ["initValue"] = new(
                "initValue", "public void initValue()", "xRecord",
                "Seed defaults on a new, not yet inserted record.",
                ["Runs on a record that does not exist yet — there is nothing stored to read, and `this.orig()` is empty.", NextOnce]),
            ["modifiedField"] = new(
                "modifiedField", "public void modifiedField(FieldId _fieldId)", "xRecord",
                "React to one field changing, typically to derive others.",
                ["Test which field you were called for: `if (_fieldId == fieldNum(MyTable, MyField))`.", .. PreImage, NextOnce]),
        };

    /// <summary>The inherited data method by that name, or null. Case-insensitive, as X++ is.</summary>
    public static TableDataMethod? Lookup(string methodName)
        => Catalog.TryGetValue(methodName.Trim(), out var m) ? m : null;

    /// <summary>
    /// True for the object types this fallback speaks for. Tables only, deliberately — a
    /// fallback that guessed for views/maps/entities would be inventing a signature, which is
    /// the failure it exists to prevent.
    /// </summary>
    public static bool AppliesTo(string? objectType)
        => string.Equals(objectType?.Trim(), "table", StringComparison.OrdinalIgnoreCase);
}
