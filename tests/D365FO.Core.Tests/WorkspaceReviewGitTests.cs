using System.Diagnostics;
using System.Text.Json;
using D365FO.Core.Analysis;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// <see cref="WorkspaceReview.Diff"/> against real git repositories. The review used to look
/// for a <c>.git</c> directory at the path it was given, so it refused a linked worktree (where
/// <c>.git</c> is a file) and any subfolder of a repository with NOT_A_GIT_REPO (issue #255).
/// </summary>
public sealed class WorkspaceReviewGitTests : IDisposable
{
    private const string TablePath = "Metadata/ContosoExt/ContosoExt/AxTable/ContosoRentEquipmentTable.xml";

    private const string TypedTable = """
        <?xml version="1.0" encoding="utf-8"?>
        <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>ContosoRentEquipmentTable</Name>
          <Fields>
            <AxTableFieldString>
              <Name>EquipmentId</Name>
              <ExtendedDataType>Name</ExtendedDataType>
            </AxTableFieldString>
          </Fields>
        </AxTable>
        """;

    private const string UntypedTable = """
        <?xml version="1.0" encoding="utf-8"?>
        <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>ContosoRentEquipmentTable</Name>
          <Fields>
            <AxTableFieldString>
              <Name>EquipmentId</Name>
              <ExtendedDataType>Name</ExtendedDataType>
            </AxTableFieldString>
            <AxTableFieldString>
              <Name>Note</Name>
            </AxTableFieldString>
          </Fields>
        </AxTable>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-review-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;

    public WorkspaceReviewGitTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
    }

    [GitFact]
    public void Review_in_a_linked_worktree_is_not_refused_as_not_a_git_repo()
    {
        CommitTable();
        var worktree = Path.Combine(_root, "wt");
        Git(_repo, "worktree", "add", "-q", "-b", "side", worktree);
        Assert.True(File.Exists(Path.Combine(worktree, ".git")), "a linked worktree has a .git file");
        File.WriteAllText(Path.Combine(worktree, TablePath), UntypedTable);

        var r = WorkspaceReview.Diff(worktree);

        Assert.True(r.Ok, r.Error?.Code + " " + r.Error?.Message);
        var data = Data(r);
        Assert.Equal(1, data.GetProperty("changedFiles").GetInt32());
        Assert.Contains(Rules(data), rule => rule == "FIELD_WITHOUT_EDT");
        Assert.Equal(Path.GetFullPath(worktree).TrimEnd('\\', '/'),
            data.GetProperty("repo").GetString()!.TrimEnd('\\', '/'), ignoreCase: OperatingSystem.IsWindows());
    }

    [GitFact]
    public void Review_from_a_subfolder_reads_changed_files_from_the_repository_root()
    {
        CommitTable();
        File.WriteAllText(Path.Combine(_repo, TablePath), UntypedTable);

        var r = WorkspaceReview.Diff(Path.Combine(_repo, "Metadata", "ContosoExt"));

        Assert.True(r.Ok, r.Error?.Code + " " + r.Error?.Message);
        var data = Data(r);
        Assert.Equal(1, data.GetProperty("changedFiles").GetInt32());
        // The rule pass only runs when the git-relative path resolves against the top level.
        Assert.Contains(Rules(data), rule => rule == "FIELD_WITHOUT_EDT");
    }

    [GitFact]
    public void Review_of_a_folder_outside_any_repository_is_still_refused()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        var r = WorkspaceReview.Diff(plain);

        Assert.False(r.Ok);
        Assert.Equal("NOT_A_GIT_REPO", r.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(r.Error.Hint));
    }

    [Fact]
    public void Review_of_a_missing_folder_is_refused_as_not_a_git_repo()
    {
        var r = WorkspaceReview.Diff(Path.Combine(_root, "does-not-exist"));

        Assert.False(r.Ok);
        Assert.Equal("NOT_A_GIT_REPO", r.Error!.Code);
    }

    private void CommitTable()
    {
        Git(_repo, "init", "-q");
        var file = Path.Combine(_repo, TablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, TypedTable);
        Git(_repo, "add", "-A");
        Git(_repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "init");
    }

    private static JsonElement Data(ToolResult<object> r) =>
        JsonSerializer.SerializeToElement(r.Data);

    private static IEnumerable<string?> Rules(JsonElement data) =>
        data.GetProperty("violations").EnumerateArray().Select(v => v.GetProperty("rule").GetString()).ToList();

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {se}{so.Result}");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        try
        {
            // git writes its object files read-only, which Directory.Delete refuses on Windows.
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A fact that runs only when <c>git</c> can be started, and reports as skipped otherwise.
/// </summary>
internal sealed class GitFactAttribute : FactAttribute
{
    private static readonly bool Available = Probe();

    public GitFactAttribute()
    {
        if (!Available) Skip = "git is not on PATH.";
    }

    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
