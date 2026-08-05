using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The knowledge topics are embedded from <c>skills/_source/*.md</c> at build time,
/// so these tests double as a guard that the corpus stays parseable — a topic that
/// loses its frontmatter would otherwise only fail at runtime.
/// </summary>
public class KnowledgeBaseTests
{
    [Fact]
    public void Embeds_the_whole_skills_source_corpus()
    {
        Assert.NotEmpty(KnowledgeBase.Topics);
        // Sentinel topics: if one of these disappears the corpus was renamed, not just edited.
        Assert.Contains(KnowledgeBase.Topics, t => t.Id == "table-scaffolding");
        Assert.Contains(KnowledgeBase.Topics, t => t.Id == "coc-extension-authoring");
        Assert.Contains(KnowledgeBase.Topics, t => t.Id == "xpp-best-practice-rules");
    }

    [Fact]
    public void Every_topic_carries_id_description_and_body()
    {
        Assert.All(KnowledgeBase.Topics, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id), "topic id is empty");
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Id} has no description");
            Assert.False(string.IsNullOrWhiteSpace(t.Body), $"{t.Id} has no body");
            Assert.DoesNotContain("---", t.Body[..Math.Min(4, t.Body.Length)]);
        });
    }

    [Fact]
    public void Get_resolves_exact_id_and_unique_substring()
    {
        Assert.Equal("table-scaffolding", KnowledgeBase.Get("table-scaffolding")?.Id);
        Assert.Equal("table-scaffolding", KnowledgeBase.Get("TABLE-SCAFFOLDING")?.Id);
        Assert.Null(KnowledgeBase.Get("no-such-topic"));
        Assert.Null(KnowledgeBase.Get(""));
    }

    [Fact]
    public void Ambiguous_substring_does_not_silently_pick_one()
    {
        // Several ids contain "xpp"; returning an arbitrary one would be worse than a miss.
        var candidates = KnowledgeBase.Topics.Count(t => t.Id.Contains("xpp", StringComparison.OrdinalIgnoreCase));
        Assert.True(candidates > 1, "test premise: more than one topic id contains 'xpp'");
        Assert.Null(KnowledgeBase.Get("xpp"));
    }

    [Fact]
    public void Suggest_offers_near_misses()
    {
        var suggestions = KnowledgeBase.Suggest("table");
        Assert.Contains("table-scaffolding", suggestions);
    }

    [Fact]
    public void Topics_split_into_named_sections()
    {
        var topic = KnowledgeBase.Get("table-scaffolding")!;
        Assert.True(topic.Sections.Count > 1, "expected several '##' sections");
        Assert.All(topic.Sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Text)));
    }

    [Fact]
    public void A_hash_inside_a_fenced_block_is_not_a_heading()
    {
        var sections = KnowledgeBase.SplitSections("""
            intro text

            ## Real heading

            ```sh
            ## not a heading, this is a shell comment
            d365fo doctor
            ```

            trailing text
            """);

        Assert.Equal(2, sections.Count);
        Assert.Equal("(intro)", sections[0].Heading);
        Assert.Equal("Real heading", sections[1].Heading);
        Assert.Contains("not a heading", sections[1].Text);
    }

    [Fact]
    public void Search_ranks_the_relevant_topic_first()
    {
        var hits = KnowledgeBase.Search("chain of command wrap a standard method", limit: 5);
        Assert.NotEmpty(hits);
        Assert.Equal("coc-extension-authoring", hits[0].TopicId);
        Assert.All(hits, h => Assert.False(string.IsNullOrWhiteSpace(h.Excerpt)));
    }

    [Fact]
    public void Search_can_be_scoped_to_one_topic()
    {
        var hits = KnowledgeBase.Search("field", limit: 20, topicId: "table-scaffolding");
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("table-scaffolding", h.TopicId));
    }

    [Fact]
    public void Search_with_only_stopwords_returns_nothing()
    {
        Assert.Empty(KnowledgeBase.Search("how can you use the"));
        Assert.Empty(KnowledgeBase.Search(""));
    }

    [Fact]
    public void Parse_reads_frontmatter_and_strips_it_from_the_body()
    {
        var topic = KnowledgeBase.Parse("""
            ---
            id: demo-topic
            description: A demo description.
            appliesWhen: The user asks for a demo.
            applyTo:
              - "**/AxTable/**"
            ---

            # Demo

            Body text.
            """, "fallback");

        Assert.Equal("demo-topic", topic.Id);
        Assert.Equal("A demo description.", topic.Description);
        Assert.Equal("The user asks for a demo.", topic.AppliesWhen);
        Assert.StartsWith("# Demo", topic.Body);
    }

    [Fact]
    public void Parse_falls_back_to_the_resource_name_when_frontmatter_is_absent()
    {
        var topic = KnowledgeBase.Parse("Just a body.", "fallback-id");
        Assert.Equal("fallback-id", topic.Id);
        Assert.Equal("Just a body.", topic.Body);
    }
}
