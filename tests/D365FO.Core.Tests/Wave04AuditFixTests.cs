using D365FO.Core.Index;
using D365FO.Core.Labels;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The defects the wave-04 audit found, each locked by the case that exposed it.
/// </summary>
/// <remarks>
/// Three of these are the upstream 1.16.0 fixes the gap analysis lists under this wave; one is a
/// defect in the wave's own new code, found by running it against two languages instead of one.
/// </remarks>
public class Wave04AuditFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-w4a-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public Wave04AuditFixTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "index.sqlite");
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ── a degraded zero is not "unused" ────────────────────────────────────

    /// <summary>
    /// An empty index and no readable source is not a codebase where the symbol is unused, and
    /// the difference decides whether someone deletes it.
    /// </summary>
    [Fact]
    public void A_search_that_read_nothing_says_so_instead_of_returning_a_clean_zero()
    {
        var result = MethodSourceSearch.Find(_repo, "NumberSeq", kind: null, model: null, limit: 50);

        Assert.Empty(result.Hits);
        Assert.Equal(0, result.FilesScanned);
        Assert.False(result.Searched);
        Assert.NotNull(result.Caveat);
        Assert.Contains("not evidence", result.Caveat!, StringComparison.OrdinalIgnoreCase);
    }

    // ── the extension suffix the platform actually ships ───────────────────

    /// <summary>
    /// Every one of the 1 093 AxTableExtension objects in a stock installation is named
    /// <c>Base.Suffix</c>; none uses the underscore form. The validator used to call that dot an
    /// illegal character while the same run recommended the shape it appears in.
    /// </summary>
    [Theory]
    [InlineData("VendInvoiceInfoTable.Extension")]
    [InlineData("BOM.AdvancedQualityManagement")]
    [InlineData("CustTable_Extension")]
    public void Both_element_extension_suffix_forms_are_legal(string name)
    {
        var errors = ObjectNamingRules.Validate("tableextension", name)
            .Where(v => v.Severity == "error")
            .ToList();

        Assert.Empty(errors);
    }

    [Theory]
    // Two dots is not a suffix, it is a second one.
    [InlineData("tableextension", "Cust.Table.Bad")]
    [InlineData("tableextension", "Cust Table")]
    // And the dot stays illegal for a kind that is not an extension.
    [InlineData("table", "Cust.Table")]
    [InlineData("class", "Cust.Class")]
    public void A_dot_is_still_refused_where_the_platform_does_not_use_one(string kind, string name)
    {
        var errors = ObjectNamingRules.Validate(kind, name)
            .Where(v => v.Severity == "error")
            .ToList();

        Assert.Contains(errors, e => e.Code == "INVALID_CHARS");
    }

    // ── a label file nothing can reference ─────────────────────────────────

    [Theory]
    [InlineData("ConFleet", true)]
    [InlineData("Con_Fleet2", true)]
    [InlineData("Con Fleet", false)]   // the @File:Id grammar has no space
    [InlineData("Con.Fleet", false)]   // nor a dot — that separates file from id
    [InlineData("Con-Fleet", false)]
    [InlineData("2ConFleet", false)]   // an identifier does not start with a digit
    [InlineData("", false)]
    public void A_label_file_id_must_be_one_an_at_token_can_name(string id, bool referenceable)
        => Assert.Equal(referenceable, LabelFileWriter.IsReferenceableLabelFileId(id));

    [Theory]
    [InlineData("ConFleet.en-us.label.txt", "ConFleet")]
    [InlineData("/a/b/Con Fleet.cs.label.txt", "Con Fleet")]
    public void The_label_file_id_is_the_part_before_the_first_dot(string path, string expected)
        => Assert.Equal(expected, LabelFileWriter.LabelFileIdOf(path));

    // ── one text is not a translation ──────────────────────────────────────

    /// <summary>
    /// <c>Update</c> writes exactly the value it is given, so the guard against writing one
    /// language's text into every file belongs to the caller — this locks the writer's half:
    /// each file keeps whatever was written to it.
    /// </summary>
    [Fact]
    public void Each_language_file_keeps_its_own_text()
    {
        var en = Path.Combine(_dir, "ConFleet.en-us.label.txt");
        var cs = Path.Combine(_dir, "ConFleet.cs.label.txt");
        LabelFileWriter.CreateOrUpdate(en, "ConVehicle", "Vehicel");
        LabelFileWriter.CreateOrUpdate(cs, "ConVehicle", "Vozidlo");

        LabelFileWriter.Update(en, "ConVehicle", "Vehicle");
        LabelFileWriter.Update(cs, "ConVehicle", "Vozidlo");

        Assert.Contains("ConVehicle=Vehicle", File.ReadAllText(en), StringComparison.Ordinal);
        Assert.Contains("ConVehicle=Vozidlo", File.ReadAllText(cs), StringComparison.Ordinal);
    }

    // ── a multi-word query ─────────────────────────────────────────────────

    /// <summary>
    /// An object name has no spaces, so a multi-word query matched as one literal matches
    /// nothing: "agent feed" found none of the three AgentFeed* tables in a real index.
    /// </summary>
    [Fact]
    public void A_multi_word_query_matches_names_carrying_every_token()
    {
        Seed("AgentFeed", "AgentFeedItemSourceRecord", "AgentFeedMenuItem", "SalesTable");

        var both = _repo.FindUsages("agent feed").Select(r => r.Name).ToList();
        Assert.Equal(3, both.Count);
        Assert.DoesNotContain("SalesTable", both);

        // Narrower still — and "item" is carried by AgentFeedMenuItem too, which is the point:
        // a token matches anywhere in the name, not only at a word boundary the name does not have.
        var items = _repo.FindUsages("agent feed item").Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(["AgentFeedItemSourceRecord", "AgentFeedMenuItem"], items);

        // Order does not matter.
        Assert.Equal(items, _repo.FindUsages("item feed").Select(r => r.Name).OrderBy(n => n).ToList());

        // A token only one name carries narrows to that one.
        Assert.Equal(["AgentFeedItemSourceRecord"], _repo.FindUsages("feed source").Select(r => r.Name).ToList());

        // A token nothing carries takes the result to nothing.
        Assert.Empty(_repo.FindUsages("agent nosuchtoken"));

        // The single-token behaviour is untouched.
        Assert.Equal(3, _repo.FindUsages("AgentFeed").Count);
    }

    private void Seed(params string[] tableNames)
    {
        var batch = ExtractBatch.Empty("ConFleet") with
        {
            Tables = tableNames.Select(n => new ExtractedTable(n, null, null, [])).ToList(),
        };
        _repo.ApplyExtract(batch);
    }
}
