using System.Text.RegularExpressions;

namespace AgentStudio.Domain;

/// <summary>Validates submitted form values against their FormField definitions, and decides
/// which fields are currently visible (conditional visibility). Pure/static — usable from both
/// the runtime form page and the studio's live preview without any DI wiring, same spirit as
/// ConditionEvaluator.</summary>
public static class FormFieldValidator
{
    /// <summary>True if this field should be shown to the user right now, given the current
    /// values of all fields. A field with no VisibleWhenField is always visible. A hidden
    /// field's own value/requiredness is ignored by Validate below — hidden means inert, not
    /// just visually hidden.</summary>
    public static bool IsVisible(FormField field, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(field.VisibleWhenField)) return true;
        var actual = values.GetValueOrDefault(field.VisibleWhenField, "");
        return actual == (field.VisibleWhenEquals ?? "");
    }

    /// <summary>Returns one error message per invalid *visible* field (missing-but-required,
    /// pattern/length/range violations) — hidden fields are never validated, since a value the
    /// user was never shown can't reasonably be required. Fields are validated in list order.</summary>
    public static List<string> Validate(IReadOnlyList<FormField> fields, IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        foreach (var field in fields)
        {
            if (!IsVisible(field, values)) continue;

            var value = values.GetValueOrDefault(field.Name, "");
            // A checkbox's value is always "true"/"false" once touched — "false" isn't empty,
            // so the generic empty-string check below would let a required-but-unchecked box
            // through. "Required" on a checkbox means "must be checked", not "must have a value".
            if (field.Required && field.Type == "checkbox" && value != "true")
            {
                errors.Add(field.ErrorMessage ?? $"{field.Label} is required.");
                continue;
            }
            if (field.Required && field.Type != "checkbox" && string.IsNullOrWhiteSpace(value))
            {
                errors.Add(field.ErrorMessage ?? $"{field.Label} is required.");
                continue;
            }
            if (string.IsNullOrEmpty(value)) continue; // optional and empty — nothing else to check

            if (field.MinLength is > 0 && value.Length < field.MinLength)
            {
                errors.Add(field.ErrorMessage ?? $"{field.Label} must be at least {field.MinLength} characters.");
                continue;
            }
            if (field.MaxLength is > 0 && value.Length > field.MaxLength)
            {
                errors.Add(field.ErrorMessage ?? $"{field.Label} must be at most {field.MaxLength} characters.");
                continue;
            }
            if (field.Type == "number" && double.TryParse(value, out var num))
            {
                if (field.Min.HasValue && num < field.Min)
                {
                    errors.Add(field.ErrorMessage ?? $"{field.Label} must be at least {field.Min}.");
                    continue;
                }
                if (field.Max.HasValue && num > field.Max)
                {
                    errors.Add(field.ErrorMessage ?? $"{field.Label} must be at most {field.Max}.");
                    continue;
                }
            }
            if (!string.IsNullOrEmpty(field.Pattern))
            {
                // ponytail: pattern compiled per call, not cached — form validation isn't a hot
                // path (one submit per human), caching would be premature.
                var matches = SafeIsMatch(field.Pattern, value);
                if (matches == false)
                    errors.Add(field.ErrorMessage ?? $"{field.Label} is not in the expected format.");
            }
        }
        return errors;
    }

    /// <summary>Field-level pattern is operator-authored config, not attacker input, but a typo'd
    /// regex shouldn't crash every submission — treat an invalid pattern as "doesn't match"
    /// rather than throwing.</summary>
    private static bool? SafeIsMatch(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
