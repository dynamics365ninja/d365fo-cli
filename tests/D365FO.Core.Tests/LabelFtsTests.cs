using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

public class LabelFtsTests
{
    [Fact]
    public void Fts_ranks_phrase_match_above_partial()
    {
        var db = Path.Combine(Path.GetTempPath(), $"d365fo-fts-{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new MetadataRepository(db);
            repo.EnsureSchema();
            var modelId = repo.UpsertModel("Fleet", "Contoso", "usr", true);

            Insert(db, 1, "Vehicle fleet operations", "en-us", "VehicleFleet", "Vehicles.en-us");
            Insert(db, 2, "Fleet management console", "en-us", "FleetMgmt", "Vehicles.en-us");
            Insert(db, 3, "Customer invoice register", "en-us", "CustInv", "Sales.en-us");
            Insert(db, 4, "Vehicle is mandatory", "en-us", "VehMandatory", "Vehicles.en-us");

            var hits = repo.SearchLabelsFts("Vehicle fleet", new[] { "en-us" }, 10);
            Assert.NotEmpty(hits);
            Assert.Contains(hits, h => h.Key == "VehicleFleet");
            Assert.DoesNotContain(hits, h => h.Key == "CustInv");
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    [Fact]
    public void Fts_search_is_language_filtered()
    {
        var db = Path.Combine(Path.GetTempPath(), $"d365fo-fts-{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new MetadataRepository(db);
            repo.EnsureSchema();
            repo.UpsertModel("Fleet", "Contoso", "usr", true);
            Insert(db, 1, "vehicle register", "en-us", "VehEn", "Vehicles.en-us");
            Insert(db, 2, "register vozidel", "cs", "VehCs", "Vehicles.cs");

            var en = repo.SearchLabelsFts("vehicle", new[] { "en-us" }, 10);
            Assert.Single(en);
            Assert.Equal("VehEn", en[0].Key);

            var cs = repo.SearchLabelsFts("vozidel", new[] { "cs" }, 10);
            Assert.Single(cs);
            Assert.Equal("VehCs", cs[0].Key);
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    private static void Insert(string db, long labelId, string value, string lang, string key, string file)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Labels(LabelId, LabelFile, Language, Key, Value) VALUES(@id,@f,@lg,@k,@v)";
        cmd.Parameters.AddWithValue("@id", labelId);
        cmd.Parameters.AddWithValue("@f", file);
        cmd.Parameters.AddWithValue("@lg", lang);
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}
