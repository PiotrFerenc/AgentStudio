namespace AgentStudio.Domain;

/// <summary>Safe evaluator for simple condition comparisons. No arbitrary expression evaluation.</summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(ConditionNode node, Func<string, string?> resolveVariable)
    {
        var leftValue = Resolve(node.Left, resolveVariable) ?? "";
        var rightValue = Resolve(node.Right, resolveVariable) ?? "";

        return node.Operator switch
        {
            ConditionOperator.Equals => string.Equals(leftValue, rightValue, StringComparison.Ordinal),
            ConditionOperator.NotEquals => !string.Equals(leftValue, rightValue, StringComparison.Ordinal),
            ConditionOperator.Contains => leftValue.Contains(rightValue, StringComparison.Ordinal),
            ConditionOperator.StartsWith => leftValue.StartsWith(rightValue, StringComparison.Ordinal),
            ConditionOperator.EndsWith => leftValue.EndsWith(rightValue, StringComparison.Ordinal),
            ConditionOperator.GreaterThan => Compare(leftValue, rightValue) > 0,
            ConditionOperator.LessThan => Compare(leftValue, rightValue) < 0,
            ConditionOperator.GreaterOrEqual => Compare(leftValue, rightValue) >= 0,
            ConditionOperator.LessOrEqual => Compare(leftValue, rightValue) <= 0,
            _ => false
        };
    }

    private static int Compare(string left, string right)
    {
        if (double.TryParse(left, System.Globalization.CultureInfo.InvariantCulture, out var ln) &&
            double.TryParse(right, System.Globalization.CultureInfo.InvariantCulture, out var rn))
            return ln.CompareTo(rn);
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static string? Resolve(string expr, Func<string, string?> resolveVariable)
    {
        if (expr.StartsWith("variables.", StringComparison.Ordinal))
            return resolveVariable(expr["variables.".Length..]);
        if (string.Equals(expr, "input", StringComparison.Ordinal))
            return resolveVariable("input");
        return expr; // literal
    }
}
