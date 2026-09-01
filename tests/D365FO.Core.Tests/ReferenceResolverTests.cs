using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Anti-hallucination gate tests: every reference in generated X++ is either
/// proven by the seeded index or reported with the right severity.
/// </summary>
public class ReferenceResolverTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"refres-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public ReferenceResolverTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        Seed();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    private void Seed()
    {
        var batch = new ExtractBatch(
            Model: "ApplicationSuite",
            Publisher: "Microsoft",
            Layer: "app",
            IsCustom: false,
            Tables: new[]
            {
                new ExtractedTable("CustTable", "@SYS1234", "x", new[]
                {
                    new ExtractedTableField("AccountNum", "ExtendedDataType", "CustAccount", null, true),
                    new ExtractedTableField("CustGroup", "ExtendedDataType", "CustGroupId", null, false),
                })
                {
                    Methods = new[]
                    {
                        new ExtractedMethod("checkCreditLimit", "boolean checkCreditLimit(real _amount)", "boolean", false),
                    },
                },
            },
            Classes: new[]
            {
                new ExtractedClass("CustBalance", null, false, false, "x", new[]
                {
                    new ExtractedMethod("construct", "static CustBalance construct()", "CustBalance", true),
                    new ExtractedMethod("calc", "real calc(CustTable _custTable, boolean _includeOpen = true)", "real", false),
                }),
                new ExtractedClass("CustBalanceSub", "CustBalance", false, false, "x",
                    Array.Empty<ExtractedMethod>()),
            },
            Edts: new[] { new ExtractedEdt("CustAccount", null, "String", null, 20) },
            Enums: new[] { new ExtractedEnum("CustVendorBlocked", null, new[] { new ExtractedEnumValue("No", 0, null) }) },
            MenuItems: new[] { new ExtractedMenuItem("CustTableListPage", "Display", "CustTable", "Form", null) },
            CocExtensions: new[] { new ExtractedCoc("SalesTable", "insert", "SalesTable_MyExt_Extension") },
            Labels: new[]
            {
                new ExtractedLabel("MyModel", "en-us", "CustomerCreated", "Customer was created."),
                new ExtractedLabel("SYS", "en-us", "12345", "Some standard text"),
            });
        _repo.ApplyExtract(batch);

        // Extension-added field on CustTable (AxTableExtension path).
        var extBatch = new ExtractBatch(
            Model: "MyModel",
            Publisher: "Me",
            Layer: "usr",
            IsCustom: true,
            Tables: Array.Empty<ExtractedTable>(),
            Classes: Array.Empty<ExtractedClass>(),
            Edts: Array.Empty<ExtractedEdt>(),
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>())
        {
            Extensions = new[]
            {
                new ExtractedObjectExtension("Table", "CustTable", "CustTable.MyExt", "x")
                {
                    AddedFields = new[] { new ExtractedTableField("MyCustomField", "ExtendedDataType", "Name", null, false) },
                },
            },
        };
        _repo.ApplyExtract(extBatch);
    }

    private ResolveResult Resolve(string code) => ReferenceResolver.Resolve(code, _repo);

    // ── Intrinsics ───────────────────────────────────────────────────────────

    [Fact]
    public void Known_tableStr_verifies()
    {
        var r = Resolve("str s = tableStr(CustTable);");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-intrinsic-target");
        Assert.True(r.VerifiedCount > 0);
    }

    [Fact]
    public void Hallucinated_tableStr_is_error()
    {
        var r = Resolve("str s = tableStr(CustTableFake);");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-intrinsic-target" && v.Severity == "error");
    }

    [Fact]
    public void Hallucinated_field_in_fieldStr_is_error()
    {
        var r = Resolve("str s = fieldStr(CustTable, CustomerName);");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-field" && v.Severity == "error" && v.Identifier == "CustTable.CustomerName");
    }

    [Fact]
    public void Known_field_in_fieldStr_verifies()
    {
        var r = Resolve("str s = fieldStr(CustTable, AccountNum);");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-field");
    }

    [Fact]
    public void Extension_added_field_verifies()
    {
        var r = Resolve("str s = fieldStr(CustTable, MyCustomField);");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-field");
    }

    [Fact]
    public void Known_menu_item_intrinsic_verifies()
    {
        var r = Resolve("str s = menuItemDisplayStr(CustTableListPage);");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-intrinsic-target");
    }

    [Fact]
    public void Enum_intrinsic_verifies()
    {
        var r = Resolve("int n = enumNum(CustVendorBlocked);");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-intrinsic-target");
    }

    // ── Static access ────────────────────────────────────────────────────────

    [Fact]
    public void Known_static_method_verifies()
    {
        var r = Resolve("CustBalance b = CustBalance::construct();");
        Assert.DoesNotContain(r.Violations, v => v.Severity == "error");
    }

    [Fact]
    public void Hallucinated_static_member_is_error()
    {
        var r = Resolve("CustBalance::newFromCustomer();");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-static-member" && v.Severity == "error");
    }

    [Fact]
    public void Hallucinated_type_in_static_access_is_error()
    {
        var r = Resolve("CustBalanceHelper::construct();");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-type" && v.Severity == "error");
    }

    [Fact]
    public void Enum_value_access_verifies_enum_type()
    {
        var r = Resolve("if (blocked == CustVendorBlocked::No) return;");
        Assert.DoesNotContain(r.Violations, v => v.Identifier.StartsWith("CustVendorBlocked"));
    }

    [Fact]
    public void Kernel_static_is_not_flagged()
    {
        var r = Resolve("utcdatetime now = DateTimeUtil::utcNow();");
        Assert.DoesNotContain(r.Violations, v => v.Severity == "error");
    }

    [Fact]
    public void Arity_mismatch_is_error()
    {
        var r = Resolve("CustBalance::construct(1, 2, 3);");
        Assert.Contains(r.Violations, v => v.Kind == "arity-mismatch" && v.Severity == "error");
    }

    [Fact]
    public void Inherited_static_method_found_through_chain()
    {
        var r = Resolve("CustBalanceSub::construct();");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-static-member");
    }

    // ── Buffer field access ──────────────────────────────────────────────────

    [Fact]
    public void Bound_buffer_hallucinated_field_is_error()
    {
        var code = """
            CustTable custTable;
            select custTable;
            info(strFmt("%1", custTable.CustomerName));
            """;
        var r = Resolve(code);
        Assert.Contains(r.Violations, v => v.Kind == "unknown-field" && v.Identifier == "CustTable.CustomerName");
    }

    [Fact]
    public void Bound_buffer_real_field_and_system_field_verify()
    {
        var code = """
            CustTable custTable;
            select custTable;
            print custTable.AccountNum, custTable.RecId, custTable.MyCustomField;
            """;
        var r = Resolve(code);
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-field");
    }

    [Fact]
    public void Table_builtin_method_verifies()
    {
        var code = """
            CustTable custTable;
            custTable.insert();
            """;
        var r = Resolve(code);
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-method");
    }

    [Fact]
    public void Indexed_table_method_verifies()
    {
        var code = """
            CustTable custTable;
            custTable.checkCreditLimit(100.0);
            """;
        var r = Resolve(code);
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-method");
    }

    [Fact]
    public void Unknown_instance_method_is_warning_not_error()
    {
        var code = """
            CustTable custTable;
            custTable.frobnicate();
            """;
        var r = Resolve(code);
        var hit = r.Violations.Single(v => v.Kind == "unknown-method");
        Assert.Equal("warning", hit.Severity);
    }

    // ── Declared types ───────────────────────────────────────────────────────

    [Fact]
    public void Unknown_declared_type_is_warning()
    {
        var r = Resolve("MadeUpHelperClass helper;");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-type" && v.Severity == "warning");
    }

    [Fact]
    public void Kernel_declared_type_verifies()
    {
        var r = Resolve("Map m = new Map(Types::String, Types::String);");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-type");
    }

    // ── Labels ───────────────────────────────────────────────────────────────

    [Fact]
    public void Known_modern_label_verifies()
    {
        var r = Resolve("info(\"@MyModel:CustomerCreated\");");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-label");
    }

    [Fact]
    public void Missing_label_in_known_file_is_error()
    {
        var r = Resolve("info(\"@MyModel:DoesNotExist\");");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-label" && v.Severity == "error");
    }

    [Fact]
    public void Unknown_label_file_is_warning()
    {
        var r = Resolve("info(\"@BrandNewModel:SomeLabel\");");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-label" && v.Severity == "warning");
    }

    [Fact]
    public void Legacy_label_found_verifies()
    {
        var r = Resolve("info(\"@SYS12345\");");
        Assert.DoesNotContain(r.Violations, v => v.Kind == "unknown-label");
    }

    [Fact]
    public void Legacy_label_missing_is_warning()
    {
        var r = Resolve("info(\"@SYS99999\");");
        Assert.Contains(r.Violations, v => v.Kind == "unknown-label" && v.Severity == "warning");
    }

    // ── Comments / strings are inert ─────────────────────────────────────────

    [Fact]
    public void References_in_comments_are_ignored()
    {
        var code = """
            // CustTableFake::frobnicate() should not be resolved
            /* tableStr(NopeTable) neither */
            str s = "CustBalanceHelper::construct()";
            """;
        var r = Resolve(code);
        Assert.Empty(r.Violations);
    }

    // ── Clean generated CoC compiles against the gate ────────────────────────

    [Fact]
    public void Clean_coc_wrapper_passes()
    {
        var code = """
            [ExtensionOf(tableStr(CustTable))]
            final class CustTable_MyExt_Extension
            {
                public boolean checkCreditLimit(real _amount)
                {
                    boolean ret = next checkCreditLimit(_amount);
                    if (this.CustGroup == '')
                    {
                        ret = checkFailed("@MyModel:CustomerCreated");
                    }
                    return ret;
                }
            }
            """;
        var r = Resolve(code);
        Assert.DoesNotContain(r.Violations, v => v.Severity == "error");
    }
}
