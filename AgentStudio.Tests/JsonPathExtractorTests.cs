using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class JsonPathExtractorTests
{
    [Fact]
    public void Extracts_top_level_string_property()
    {
        Assert.Equal("Anna", JsonPathExtractor.Extract("""{"name":"Anna"}""", "name"));
    }

    [Fact]
    public void Extracts_nested_property()
    {
        Assert.Equal("c", JsonPathExtractor.Extract("""{"a":{"b":"c"}}""", "a.b"));
    }

    [Fact]
    public void Extracts_array_element_by_index_then_property()
    {
        var json = """{"items":[{"name":"x"},{"name":"y"}]}""";
        Assert.Equal("y", JsonPathExtractor.Extract(json, "items[1].name"));
    }

    [Fact]
    public void Extracts_from_a_root_level_array()
    {
        Assert.Equal("20", JsonPathExtractor.Extract("[10,20,30]", "[1]"));
    }

    [Fact]
    public void Number_returned_as_its_literal_text()
    {
        Assert.Equal("42", JsonPathExtractor.Extract("""{"n":42}""", "n"));
    }

    [Fact]
    public void Booleans_returned_as_lowercase_text()
    {
        Assert.Equal("true", JsonPathExtractor.Extract("""{"ok":true}""", "ok"));
        Assert.Equal("false", JsonPathExtractor.Extract("""{"ok":false}""", "ok"));
    }

    [Fact]
    public void Null_returned_as_empty_string()
    {
        Assert.Equal("", JsonPathExtractor.Extract("""{"x":null}""", "x"));
    }

    [Fact]
    public void Object_or_array_leaf_returned_as_raw_json_text()
    {
        Assert.Equal("""{"x":1}""", JsonPathExtractor.Extract("""{"obj":{"x":1}}""", "obj"));
    }

    [Fact]
    public void Empty_path_returns_the_whole_document()
    {
        Assert.Equal("hello", JsonPathExtractor.Extract("\"hello\"", ""));
    }

    [Fact]
    public void Invalid_json_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonPathExtractor.Extract("not json", "a"));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Missing_property_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonPathExtractor.Extract("""{"a":1}""", "b"));
        Assert.Contains("'b'", ex.Message);
    }

    [Fact]
    public void Out_of_range_index_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonPathExtractor.Extract("[1,2]", "[5]"));
        Assert.Contains("[5]", ex.Message);
    }

    [Fact]
    public void Indexing_into_a_non_array_fails_clearly()
    {
        Assert.Throws<InvalidOperationException>(() => JsonPathExtractor.Extract("""{"a":1}""", "[0]"));
    }

    [Fact]
    public void Property_access_into_a_non_object_fails_clearly()
    {
        Assert.Throws<InvalidOperationException>(() => JsonPathExtractor.Extract("[1,2]", "name"));
    }
}
