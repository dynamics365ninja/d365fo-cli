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
/// <param name="CocEligible">
/// False where a Chain of Command wrapper compiles, with only a warning, and then never runs.
/// </param>
public sealed record TableDataMethod(
    string Name,
    string Signature,
    string DeclaredOn,
    string Purpose,
    IReadOnlyList<string> Contract,
    bool CocEligible = true);

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

    private static readonly string[] AosValidateRefusal =
    [
        "A CoC wrapper here COMPILES AND NEVER RUNS. xppc answers with a warning (\"Chain of command is not " +
        "supported on method '…' because it is internal or marked as InternalUseOnly\"), builds the model, " +
        "and the validation silently does nothing.",
        "These run on the AOS tier as a last check before the physical access, and the platform reserves " +
        "them. Put the rule in `validateWrite` or `validateDelete`, where a wrapper is honoured.",
    ];

    private static TableDataMethod AosValidate(string name, string when) => new(
        name, $"public boolean {name}()", "xRecord",
        $"Server-tier check {when}; reserved by the platform.", AosValidateRefusal, CocEligible: false);

    private static readonly Dictionary<string, TableDataMethod> Catalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["validateWrite"] = new(
                "validateWrite", "public boolean validateWrite()", "xRecord",
                "Gate an insert or an update of the whole record. Forms and data entities call it; plain X++ " +
                "`insert()`/`update()` do not, so code calls it itself: `if (rec.validateWrite()) { rec.insert(); }`.",
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
                    "Validation belongs in validateWrite, which the caller runs before the write — insert() is for side effects.",
                    NextOnce,
                ]),
            ["update"] = new(
                "update", "public void update()", "xRecord",
                "Run logic around the physical update of this buffer.",
                [.. PreImage, "Validation belongs in validateWrite, which the caller runs before the write — update() is for side effects.", NextOnce]),
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
            ["modifiedFieldValue"] = new(
                "modifiedFieldValue", "public void modifiedFieldValue(FieldName _fieldName, int _value)", "xRecord",
                "React to a field changing, addressed by name; the form-driven twin of modifiedField.",
                [
                    "The signature is not modifiedField's: the first parameter is a FieldName (a str) and the second an int. "
                    + "Writing `(FieldId, anytype)` by analogy does not compile.",
                    "Compare by name: `if (_fieldName == fieldStr(MyTable, MyField))`, not fieldNum.",
                    .. PreImage, NextOnce,
                ]),
            ["postLoad"] = new(
                "postLoad", "public void postLoad()", "xRecord",
                "Run after a record is read from the database; the place to derive unstored state.",
                [
                    "Runs on every row fetched, including in a `while select` over thousands: anything expensive here is paid per row.",
                    NextOnce,
                ]),
            ["defaultRow"] = new(
                "defaultRow", "public void defaultRow()", "xRecord",
                "Seed a new row from the current context; called by the form framework, unlike initValue.",
                ["For a general default wrap initValue instead; neither method runs by itself on a plain X++ `insert()`.", NextOnce]),
            ["defaultField"] = new(
                "defaultField", "public void defaultField(FieldId _fieldId)", "xRecord",
                "Seed one field from context.",
                ["Test which field you were called for: `if (_fieldId == fieldNum(MyTable, MyField))`.", NextOnce]),
            ["toolTipField"] = new(
                "toolTipField", "public str toolTipField(FieldId _fieldId)", "xRecord",
                "The tooltip a form shows for one field of this record.",
                ["The string is shown to a user: use a label. It is called on hover, so keep it free of database access.", NextOnce]),
            ["helpField"] = new(
                "helpField", "public str helpField(FieldId _fieldId)", "xRecord",
                "The help text for one field of this record.",
                ["The string is shown to a user: use a label, and no database access on a per-hover path.", NextOnce]),
            ["caption"] = new(
                "caption", "public str caption()", "xRecord",
                "The record's caption, which a form title or a lookup shows.",
                ["The default comes from TitleField1 and TitleField2; setting those is usually better than wrapping this.", NextOnce]),
            ["aosValidateInsert"] = AosValidate("aosValidateInsert", "before a physical insert"),
            ["aosValidateUpdate"] = AosValidate("aosValidateUpdate", "before a physical update"),
            ["aosValidateDelete"] = AosValidate("aosValidateDelete", "before a physical delete"),
            ["aosValidateRead"] = AosValidate("aosValidateRead", "on a read"),
        };

    /// <summary>The inherited data method by that name, or null. Case-insensitive, as X++ is.</summary>
    public static TableDataMethod? Lookup(string methodName)
        => Catalog.TryGetValue(methodName.Trim(), out var m) ? m : null;

    /// <summary>
    /// The inherited data methods a wrapper can take effect on, in the order a test author meets them: the three validation
    /// gates first (the ones a rule is written into), then the write path, then the seeding and
    /// field-level hooks. <c>prepare test</c> lists these for a table because the index cannot —
    /// it stores declared members only, so a table that has never overridden <c>validateWrite</c>
    /// has no row for it, and that is exactly the method the caller wants to pin down.
    /// </summary>
    public static IReadOnlyList<TableDataMethod> All { get; } =
    [
        Catalog["validateWrite"], Catalog["validateField"], Catalog["validateDelete"],
        Catalog["insert"], Catalog["update"], Catalog["delete"],
        Catalog["initValue"], Catalog["modifiedField"], Catalog["modifiedFieldValue"],
        Catalog["postLoad"], Catalog["defaultRow"], Catalog["defaultField"],
        Catalog["toolTipField"], Catalog["helpField"], Catalog["caption"],
    ];

    /// <summary>
    /// Does this method write to the database? A test for one needs a transaction and a re-read;
    /// a test for a validation gate only needs the buffer and the verdict.
    /// </summary>
    public static bool IsWritePath(string methodName)
        => methodName.Trim().ToLowerInvariant() is "insert" or "update" or "delete";

    /// <summary>
    /// Does this method report its verdict as a boolean the caller must assert on? Those are the
    /// gates — and a test that asserts only the rejecting case passes even when the rule refuses
    /// every row, which is why <c>prepare test</c> insists on the accepting case beside it.
    /// </summary>
    public static bool IsVerdictMethod(string methodName)
        => methodName.Trim().ToLowerInvariant() is "validatewrite" or "validatefield" or "validatedelete";

    /// <summary>
    /// True for the object types this fallback speaks for. Tables only, deliberately — a
    /// fallback that guessed for views/maps/entities would be inventing a signature, which is
    /// the failure it exists to prevent.
    /// </summary>
    public static bool AppliesTo(string? objectType)
        => string.Equals(objectType?.Trim(), "table", StringComparison.OrdinalIgnoreCase);
}
