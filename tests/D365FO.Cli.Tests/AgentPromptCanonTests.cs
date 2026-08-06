using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// The agent system prompt composes its rule sections from the canon in
/// <c>skills/_source</c> instead of restating them. These tests fail if a section stops being
/// canon-backed — which is how the prompt used to drift away from the skill files.
/// </summary>
public class AgentPromptCanonTests
{
    private static readonly string Prompt = InvokeBuild();

    private static string InvokeBuild()
    {
        // PromptGenerator is internal to D365FO.Cli; reach it the same way the command does.
        var type = typeof(D365FO.Cli.Commands.Agent.AgentPromptCommand).Assembly
            .GetType("D365FO.Cli.Commands.Agent.PromptGenerator")
            ?? throw new InvalidOperationException("PromptGenerator not found");
        return (string)type.GetMethod("Build")!.Invoke(null, null)!;
    }

    [Theory]
    [InlineData("never-auto")]
    [InlineData("core")]
    [InlineData("queries")]
    [InlineData("coc")]
    [InlineData("aot-xml-safety")]
    [InlineData("classes")]
    [InlineData("statements")]
    [InlineData("bp")]
    public void Prompt_carries_each_canon_block_verbatim(string canonId)
    {
        Assert.Contains(RuleCanon.Require(canonId), Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_still_carries_its_hand_written_half()
    {
        // The narrative half is not canon-backed and must survive the composition.
        Assert.Contains("## 🏁 Mandatory first steps", Prompt, StringComparison.Ordinal);
        Assert.Contains("## 🔁 Workflow templates", Prompt, StringComparison.Ordinal);
        Assert.Contains("## 📦 Output contract", Prompt, StringComparison.Ordinal);
    }
}
