namespace D365FO.Core.Analysis;

/// <summary>
/// Graph metrics over a model-dependency graph (ROADMAP §6.2). Pure, allocation-
/// conscious functions so both the CLI and the MCP server can invoke them
/// without wiring up extra infrastructure.
/// </summary>
public static class CouplingAnalyzer
{
    public sealed record NodeMetric(string Name, int FanOut, int FanIn, double Instability);

    public sealed record CouplingReport(
        IReadOnlyList<NodeMetric> Nodes,
        IReadOnlyList<IReadOnlyList<string>> Cycles);

    /// <summary>
    /// Compute fan-in / fan-out / instability (I = Ce / (Ca + Ce)) per node and
    /// detect strongly connected components of size &gt; 1 (= reachable cycles).
    /// The graph is expected to map <c>source → list of targets</c>; missing
    /// target nodes are treated as sink nodes with no outgoing edges.
    /// </summary>
    public static CouplingReport Analyse(IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // Canonicalise: ensure every edge target is a node key; keep comparer
        // case-insensitive so mixed-case model names don't fragment the graph.
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in graph)
        {
            if (!adjacency.TryGetValue(kv.Key, out var list))
            {
                list = new List<string>();
                adjacency[kv.Key] = list;
            }
            foreach (var t in kv.Value)
            {
                if (!list.Contains(t, StringComparer.OrdinalIgnoreCase))
                    list.Add(t);
                if (!adjacency.ContainsKey(t))
                    adjacency[t] = new List<string>();
            }
        }

        // Fan-in per node.
        var fanIn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in adjacency.Keys) fanIn[node] = 0;
        foreach (var kv in adjacency)
            foreach (var t in kv.Value)
                fanIn[t] = fanIn.TryGetValue(t, out var c) ? c + 1 : 1;

        var nodes = adjacency
            .Select(kv =>
            {
                var ce = kv.Value.Count;
                var ca = fanIn[kv.Key];
                var denom = ca + ce;
                var instability = denom == 0 ? 0.0 : (double)ce / denom;
                return new NodeMetric(kv.Key, FanOut: ce, FanIn: ca, Instability: instability);
            })
            .OrderByDescending(n => n.FanIn + n.FanOut)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cycles = TarjanSccs(adjacency);
        return new CouplingReport(nodes, cycles);
    }

    /// <summary>
    /// Tarjan's strongly-connected-components. Returns only components whose
    /// size &gt; 1 or components that contain a self-loop, which correspond to
    /// real cyclic dependencies in the model graph.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> TarjanSccs(IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowlinks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sccs = new List<IReadOnlyList<string>>();

        void StrongConnect(string v)
        {
            indices[v] = index;
            lowlinks[v] = index;
            index++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in adjacency[v])
            {
                if (!indices.ContainsKey(w))
                {
                    StrongConnect(w);
                    lowlinks[v] = Math.Min(lowlinks[v], lowlinks[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlinks[v] = Math.Min(lowlinks[v], indices[w]);
                }
            }

            if (lowlinks[v] == indices[v])
            {
                var component = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                } while (!string.Equals(w, v, StringComparison.OrdinalIgnoreCase));

                var hasSelfLoop = component.Count == 1
                    && adjacency[component[0]].Contains(component[0], StringComparer.OrdinalIgnoreCase);
                if (component.Count > 1 || hasSelfLoop)
                {
                    component.Sort(StringComparer.OrdinalIgnoreCase);
                    sccs.Add(component);
                }
            }
        }

        foreach (var node in adjacency.Keys)
        {
            if (!indices.ContainsKey(node))
                StrongConnect(node);
        }
        return sccs;
    }
}
