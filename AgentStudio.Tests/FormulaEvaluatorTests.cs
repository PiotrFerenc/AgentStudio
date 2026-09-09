using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class FormulaEvaluatorTests
{
    private static readonly Dictionary<string, string> NoVars = new();

    [Theory]
    [InlineData("1 + 2", "3")]
    [InlineData("2 * 3 + 1", "7")]      // precedence: * before +
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(2 + 3) * 4", "20")]   // parens override precedence
    [InlineData("10 / 4", "2.5")]
    [InlineData("-5 + 3", "-2")]
    [InlineData("7 - 2 - 1", "4")]      // left-associative
    public void Arithmetic(string formula, string expected) =>
        Assert.Equal(expected, FormulaEvaluator.Evaluate(formula, NoVars));

    [Fact]
    public void Division_by_zero_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FormulaEvaluator.Evaluate("1 / 0", NoVars));
        Assert.Contains("division by zero", ex.Message);
    }

    [Theory]
    [InlineData("1 < 2", "true")]
    [InlineData("2 < 1", "false")]
    [InlineData("2 >= 2", "true")]
    [InlineData("3 == 3", "true")]
    [InlineData("3 != 3", "false")]
    [InlineData("\"a\" == \"a\"", "true")]
    [InlineData("\"a\" == \"b\"", "false")]
    public void Comparisons(string formula, string expected) =>
        Assert.Equal(expected, FormulaEvaluator.Evaluate(formula, NoVars));

    [Theory]
    [InlineData("true && false", "false")]
    [InlineData("true || false", "true")]
    [InlineData("!true", "false")]
    [InlineData("1 < 2 && 3 < 4", "true")]
    public void Boolean_logic(string formula, string expected) =>
        Assert.Equal(expected, FormulaEvaluator.Evaluate(formula, NoVars));

    [Fact]
    public void If_returns_the_matching_branch_unevaluated_type()
    {
        Assert.Equal("adult", FormulaEvaluator.Evaluate("IF(20 >= 18, \"adult\", \"minor\")", NoVars));
        Assert.Equal("minor", FormulaEvaluator.Evaluate("IF(10 >= 18, \"adult\", \"minor\")", NoVars));
    }

    [Fact]
    public void Concat_joins_mixed_types_as_text() =>
        Assert.Equal("Age: 18!", FormulaEvaluator.Evaluate("CONCAT(\"Age: \", 18, \"!\")", NoVars));

    [Theory]
    [InlineData("LEN(\"hello\")", "5")]
    [InlineData("UPPER(\"abc\")", "ABC")]
    [InlineData("LOWER(\"ABC\")", "abc")]
    [InlineData("TRIM(\"  x  \")", "x")]
    [InlineData("ROUND(3.14159, 2)", "3.14")]
    [InlineData("ABS(-5)", "5")]
    public void Functions(string formula, string expected) =>
        Assert.Equal(expected, FormulaEvaluator.Evaluate(formula, NoVars));

    [Fact]
    public void Placeholders_resolve_from_variables_and_input()
    {
        var vars = new Dictionary<string, string> { ["age"] = "20", ["name"] = "Anna", ["input"] = "5" };
        Assert.Equal("30", FormulaEvaluator.Evaluate("{variables.age} + 10", vars));
        Assert.Equal("ANNA", FormulaEvaluator.Evaluate("UPPER({variables.name})", vars));
        Assert.Equal("10", FormulaEvaluator.Evaluate("{input} * 2", vars));
    }

    [Fact]
    public void Missing_variable_resolves_to_empty_string_not_a_crash()
    {
        Assert.Equal("0", FormulaEvaluator.Evaluate("LEN({variables.doesNotExist})", NoVars));
    }

    [Fact]
    public void A_variable_value_can_never_be_reparsed_as_formula_syntax()
    {
        // The value contains characters that would break the grammar if it were text-substituted
        // instead of resolved as an atomic token — CONCAT must still just treat it as a string.
        var vars = new Dictionary<string, string> { ["x"] = "2 + 2) * evil(" };
        Assert.Equal("value: 2 + 2) * evil(", FormulaEvaluator.Evaluate("CONCAT(\"value: \", {variables.x})", vars));
    }

    [Fact]
    public void Unknown_function_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FormulaEvaluator.Evaluate("NOPE(1)", NoVars));
        Assert.Contains("unknown function 'NOPE'", ex.Message);
    }

    [Fact]
    public void Wrong_argument_count_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FormulaEvaluator.Evaluate("LEN(\"a\", \"b\")", NoVars));
        Assert.Contains("expects 1 argument", ex.Message);
    }

    [Fact]
    public void Non_numeric_string_in_arithmetic_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FormulaEvaluator.Evaluate("\"abc\" + 1", NoVars));
        Assert.Contains("expected a number", ex.Message);
    }

    [Fact]
    public void Unterminated_string_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FormulaEvaluator.Evaluate("\"abc", NoVars));
        Assert.Contains("unterminated string", ex.Message);
    }

    [Fact]
    public void Unbalanced_paren_fails_clearly()
    {
        Assert.Throws<InvalidOperationException>(() => FormulaEvaluator.Evaluate("(1 + 2", NoVars));
    }
}
