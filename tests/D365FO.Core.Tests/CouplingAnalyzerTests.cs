using D365FO.Core.Analysis;

namespace D365FO.Core.Tests;

public class CouplingAnalyzerTests
{
    [Fact]
    public void Analyse_reports_fan_in_fan_out_and_instability()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Contoso"] = new[] { "ApplicationPlatform", "ApplicationSuite" },
            ["ApplicationSuite"] = new[] { "ApplicationPlatform" },
            ["ApplicationPlatform"] = Array.Empty<string>(),
        };

        var report = CouplingAnalyzer.Analyse(graph);

        var platform = report.Nodes.Single(n => n.Name == "ApplicationPlatform");
        Assert.Equal(0, platform.FanOut);
        Assert.Equal(2, platform.FanIn);
        Assert.Equal(0.0, platform.Instability);

        var contoso = report.Nodes.Single(n => n.Name == "Contoso");
        Assert.Equal(2, contoso.FanOut);
        Assert.Equal(0, contoso.FanIn);
        Assert.Equal(1.0, contoso.Instability);

        Assert.Empty(report.Cycles);
    }

    [Fact]
    public void Analyse_detects_cycles()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = new[] { "B" },
            ["B"] = new[] { "C" },
            ["C"] = new[] { "A" },
            ["D"] = new[] { "D" }, // self-loop
        };

        var report = CouplingAnalyzer.Analyse(graph);
        Assert.Equal(2, report.Cycles.Count);
        var triangle = report.Cycles.Single(c => c.Count == 3);
        Assert.Contains("A", triangle);
        Assert.Contains("B", triangle);
        Assert.Contains("C", triangle);
        var selfLoop = report.Cycles.Single(c => c.Count == 1);
        Assert.Equal("D", selfLoop[0]);
    }

    [Fact]
    public void Analyse_handles_missing_target_nodes_as_sinks()
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = new[] { "B", "C" }, // C never appears as a key
        };
        var report = CouplingAnalyzer.Analyse(graph);
        Assert.Equal(3, report.Nodes.Count); // A, B, C all present
        Assert.Contains(report.Nodes, n => n.Name == "C" && n.FanIn == 1 && n.FanOut == 0);
    }
}
