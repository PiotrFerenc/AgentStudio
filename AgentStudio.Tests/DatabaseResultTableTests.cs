using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class DatabaseResultTableTests
{
    [Fact]
    public void Parses_rows_and_columns_from_a_databaseQuery_result()
    {
        var json = """{"rows":[{"id":1,"name":"Anna"},{"id":2,"name":"Bob"}],"rowCount":2,"truncated":false}""";

        var ok = DatabaseResultTable.TryParse(json, out var columns, out var rows, out var truncated);

        Assert.True(ok);
        Assert.Equal(new[] { "id", "name" }, columns);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("Anna", rows[0]["name"]);
        Assert.False(truncated);
    }

    [Fact]
    public void Reports_truncated_flag()
    {
        var json = """{"rows":[{"id":1}],"rowCount":1,"truncated":true}""";

        DatabaseResultTable.TryParse(json, out _, out _, out var truncated);

        Assert.True(truncated);
    }

    [Fact]
    public void Empty_rows_still_parses_successfully()
    {
        var json = """{"rows":[],"rowCount":0,"truncated":false}""";

        var ok = DatabaseResultTable.TryParse(json, out var columns, out var rows, out _);

        Assert.True(ok);
        Assert.Empty(rows);
    }

    [Fact]
    public void Null_cell_becomes_empty_string()
    {
        var json = """{"rows":[{"id":1,"name":null}],"rowCount":1,"truncated":false}""";

        DatabaseResultTable.TryParse(json, out _, out var rows, out _);

        Assert.Equal("", rows[0]["name"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"reply":"hi"}""")]            // no "rows"/"rowCount" — a normal chat/jsonParse result
    [InlineData("""{"rows":"not an array"}""")]
    [InlineData("[1,2,3]")]                        // valid JSON, but not an object at all
    public void Non_matching_shapes_are_rejected(string json)
    {
        var ok = DatabaseResultTable.TryParse(json, out _, out _, out _);

        Assert.False(ok);
    }
}
