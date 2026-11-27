using D365FO.Core.Skills;
using Xunit;

namespace D365FO.Core.Tests;

public sealed class SkillInstallerTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("d365fo-skills-").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "d365fo-cli.slnx"))) return d.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    [Theory]
    [InlineData(SkillTarget.Claude, "skills/anthropic", "SKILL.md")]
    [InlineData(SkillTarget.Copilot, "skills/d365fo-cli", "*.md")]
    public void The_build_carries_every_skill_file_of_the_repository(SkillTarget target, string folder, string pattern)
    {
        var source = Path.Combine(RepositoryRoot(), folder);
        var expected = Directory.EnumerateFiles(source, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal);

        Assert.Equal(expected, SkillInstaller.Bundle(target).Keys);
    }

    [Theory]
    [InlineData(SkillTarget.Claude, ".claude/skills/d365fo-cli/SKILL.md")]
    [InlineData(SkillTarget.Copilot, ".github/skills/d365fo-cli/SKILL.md")]
    public void Installs_into_the_folder_the_agent_reads_and_is_idempotent(SkillTarget target, string expected)
    {
        var first = SkillInstaller.Install(target, _repo);
        var second = SkillInstaller.Install(target, _repo);

        Assert.True(File.Exists(Path.Combine(_repo, expected)));
        Assert.Equal(SkillInstaller.Bundle(target).Count, first.Written.Count);
        Assert.Empty(second.Written);
        Assert.Equal(first.Written.Count, second.Unchanged.Count);
    }

    [Fact]
    public void A_dry_run_writes_nothing()
    {
        var result = SkillInstaller.Install(SkillTarget.Claude, _repo, dryRun: true);

        Assert.NotEmpty(result.Written);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_repo));
    }

    [Fact]
    public void A_changed_line_ending_is_not_a_change()
    {
        SkillInstaller.Install(SkillTarget.Claude, _repo);
        var file = Path.Combine(_repo, ".claude", "skills", "d365fo-cli", "SKILL.md");
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        File.WriteAllText(file, text.Replace("\n", "\r\n"));
        Assert.Empty(SkillInstaller.Install(SkillTarget.Claude, _repo).Written);
        File.WriteAllText(file, text);
        Assert.Empty(SkillInstaller.Install(SkillTarget.Claude, _repo).Written);
    }

    [Fact]
    public void Removes_only_the_files_it_recorded()
    {
        SkillInstaller.Install(SkillTarget.Claude, _repo);
        var skills = Path.Combine(_repo, ".claude", "skills");
        // A topic an earlier version installed, a user's own skill that talks about the tool,
        // and a record entry that points outside the folder.
        var retired = Write(skills, "retired-topic/SKILL.md", "run d365fo search");
        var own = Write(skills, "my-notes/SKILL.md", "we use d365fo daily");
        var outside = Write(_repo, "keep.txt", "keep");
        var manifest = Path.Combine(skills, SkillInstaller.ManifestName);
        File.WriteAllText(manifest, File.ReadAllText(manifest)
            .Replace("\"Files\": [", "\"Files\": [ \"retired-topic/SKILL.md\", \"../../keep.txt\","));

        var result = SkillInstaller.Install(SkillTarget.Claude, _repo);

        Assert.Equal(["retired-topic/SKILL.md"], result.Removed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(retired)));
        Assert.True(File.Exists(own));
        Assert.True(File.Exists(outside));
        Assert.Equal(["my-notes/SKILL.md"], result.LeftInPlace);
    }

    [Fact]
    public void Refuses_to_overwrite_a_skill_somebody_else_wrote()
    {
        var topic = SkillInstaller.Bundle(SkillTarget.Claude).Keys.First();
        var skills = Path.Combine(_repo, ".claude", "skills");
        var theirs = Write(skills, topic, "our team's own guidance");

        var refused = Assert.Throws<SkillConflictException>(() => SkillInstaller.Install(SkillTarget.Claude, _repo));

        Assert.Equal([Path.GetFullPath(theirs)], refused.Files);
        Assert.Single(Directory.EnumerateFiles(skills, "*", SearchOption.AllDirectories));

        SkillInstaller.Install(SkillTarget.Claude, _repo, force: true);
        Assert.NotEqual("our team's own guidance", File.ReadAllText(theirs));
    }

    [Fact]
    public void Takes_over_what_the_install_scripts_wrote()
    {
        var topic = SkillInstaller.Bundle(SkillTarget.Claude).Keys.First();
        var old = Write(Path.Combine(_repo, ".claude", "skills"), topic, "an older d365fo skill");

        var result = SkillInstaller.Install(SkillTarget.Claude, _repo);

        Assert.Contains(topic, result.Written);
        Assert.NotEqual("an older d365fo skill", File.ReadAllText(old));
    }

    private static string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
