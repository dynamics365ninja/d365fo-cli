using D365FO.Cli.Commands.Label;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Batch (`--entry KEY=VALUE`) behaviour of `d365fo label create`. The point of the
/// batch path is per-key error isolation: one key that cannot be written must not
/// cost the caller the keys that could.
/// </summary>
// Shares a collection with ScaffoldingSnapshotTests: both override the process-wide
// D365FO_INDEX_DB env var (here to keep the modification journal out of the real
// index), so running them in parallel would race on that global state.
[Collection("EnvIndexDb")]
public class LabelBatchCreateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-labels-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly string? _oldIndexDb;

    public LabelBatchCreateTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Fleet.en-us.label.txt");
        _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", Path.Combine(_dir, "index.sqlite"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private int Run(LabelCreateCommand.Settings settings)
        => new LabelCreateCommand().Execute(null!, settings);

    private LabelCreateCommand.Settings Settings(params string[] entries) => new()
    {
        File = _file,
        Entries = entries,
        Output = "json",
    };

    [Fact]
    public void Batch_writes_every_key_in_one_pass()
    {
        var exit = Run(Settings("FmVehicle=Vehicle", "FmCustomer=Customer", "FmOrder=Order"));

        Assert.Equal(0, exit);
        var text = File.ReadAllText(_file);
        Assert.Contains("FmVehicle=Vehicle", text);
        Assert.Contains("FmCustomer=Customer", text);
        Assert.Contains("FmOrder=Order", text);
    }

    [Fact]
    public void Batch_isolates_a_failing_key_and_still_writes_the_others()
    {
        // FmVehicle already exists and --overwrite is not passed, so it must fail —
        // without taking the two writable keys down with it.
        Assert.Equal(0, Run(Settings("FmVehicle=Vehicle")));

        var exit = Run(Settings("FmVehicle=Changed", "FmCustomer=Customer", "FmOrder=Order"));

        Assert.Equal(0, exit); // partial success is still a success envelope
        var text = File.ReadAllText(_file);
        Assert.Contains("FmVehicle=Vehicle", text);   // untouched, not overwritten
        Assert.DoesNotContain("FmVehicle=Changed", text);
        Assert.Contains("FmCustomer=Customer", text); // the isolation guarantee
        Assert.Contains("FmOrder=Order", text);
    }

    [Fact]
    public void Batch_fails_when_no_key_could_be_written()
    {
        Assert.Equal(0, Run(Settings("FmVehicle=Vehicle", "FmCustomer=Customer")));

        // Every key already exists → nothing lands → non-zero exit, not a success
        // envelope the caller has to dig through.
        var exit = Run(Settings("FmVehicle=Other", "FmCustomer=Other"));
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Batch_splits_on_the_first_equals_so_values_may_contain_one()
    {
        Assert.Equal(0, Run(Settings("FmFormula=a=b+c")));
        Assert.Contains("FmFormula=a=b+c", File.ReadAllText(_file));
    }

    [Fact]
    public void Batch_rejects_a_malformed_entry_before_writing_anything()
    {
        var exit = Run(Settings("FmVehicle=Vehicle", "NoEqualsSign"));

        Assert.Equal(1, exit);
        Assert.False(File.Exists(_file), "a malformed --entry must be caught before any write");
    }

    [Fact]
    public void Batch_rejects_a_duplicate_key_rather_than_letting_the_last_value_win()
    {
        var exit = Run(Settings("FmVehicle=First", "FmVehicle=Second"));

        Assert.Equal(1, exit);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void Overwrite_applies_to_every_key_in_the_batch()
    {
        Assert.Equal(0, Run(Settings("FmVehicle=Vehicle", "FmCustomer=Customer")));

        var settings = Settings("FmVehicle=Changed", "FmCustomer=AlsoChanged");
        var exit = Run(new LabelCreateCommand.Settings
        {
            File = settings.File,
            Entries = settings.Entries,
            Output = settings.Output,
            Overwrite = true,
        });

        Assert.Equal(0, exit);
        var text = File.ReadAllText(_file);
        Assert.Contains("FmVehicle=Changed", text);
        Assert.Contains("FmCustomer=AlsoChanged", text);
    }

    [Fact]
    public void Single_positional_key_keeps_its_hard_KEY_EXISTS_failure()
    {
        // The one-shot form predates the batch path and scripts depend on it failing
        // loudly rather than reporting a per-item result.
        var first = new LabelCreateCommand.Settings { File = _file, Key = "FmVehicle", Value = "Vehicle", Output = "json" };
        Assert.Equal(0, Run(first));

        var again = new LabelCreateCommand.Settings { File = _file, Key = "FmVehicle", Value = "Other", Output = "json" };
        Assert.Equal(1, Run(again));
        Assert.Contains("FmVehicle=Vehicle", File.ReadAllText(_file));
    }

    [Fact]
    public void Missing_key_and_entries_is_rejected()
    {
        var exit = Run(new LabelCreateCommand.Settings { File = _file, Output = "json" });
        Assert.Equal(1, exit);
    }
}
