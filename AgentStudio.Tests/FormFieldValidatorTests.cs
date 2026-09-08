using AgentStudio.Domain;
using Xunit;

namespace AgentStudio.Tests;

public class FormFieldValidatorTests
{
    private static FormField Field(string name = "field", string label = "Field", string type = "text", bool required = false) =>
        new() { Name = name, Label = label, Type = type, Required = required };

    [Fact]
    public void Validate_required_field_missing_uses_own_error_message()
    {
        var field = Field(required: true);
        field.ErrorMessage = "Custom required message.";
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string>());

        Assert.Equal(new[] { "Custom required message." }, errors);
    }

    [Fact]
    public void Validate_required_field_missing_without_error_message_falls_back_to_generic_message_with_label()
    {
        var field = Field(label: "Email address", required: true);
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string>());

        Assert.Single(errors);
        Assert.Contains("Email address", errors[0]);
    }

    [Fact]
    public void Validate_optional_field_left_empty_passes()
    {
        var field = Field(required: false);
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_min_length_violation_produces_error()
    {
        var field = Field();
        field.MinLength = 5;
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "abc" });

        Assert.Single(errors);
    }

    [Fact]
    public void Validate_max_length_violation_produces_error()
    {
        var field = Field();
        field.MaxLength = 3;
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "abcdef" });

        Assert.Single(errors);
    }

    [Fact]
    public void Validate_length_within_bounds_passes()
    {
        var field = Field();
        field.MinLength = 2;
        field.MaxLength = 5;
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "abc" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_number_min_max_violation_produces_error()
    {
        var field = Field(type: "number");
        field.Min = 1;
        field.Max = 10;
        var tooLow = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "0" });
        var tooHigh = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "11" });

        Assert.Single(tooLow);
        Assert.Single(tooHigh);
    }

    [Fact]
    public void Validate_number_within_bounds_passes()
    {
        var field = Field(type: "number");
        field.Min = 1;
        field.Max = 10;
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "5" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_min_max_not_applied_to_non_number_types()
    {
        var field = Field(type: "text");
        field.Min = 100;
        field.Max = 200;
        // "5" would violate Min=100 if this were treated as a number check — it must not be.
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "5" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_pattern_mismatch_produces_error()
    {
        var field = Field();
        field.Pattern = "^[0-9]+$";
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "abc" });

        Assert.Single(errors);
    }

    [Fact]
    public void Validate_pattern_match_passes()
    {
        var field = Field();
        field.Pattern = "^[0-9]+$";
        var errors = FormFieldValidator.Validate(new[] { field }, new Dictionary<string, string> { ["field"] = "12345" });

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_hidden_required_field_is_never_validated()
    {
        var trigger = Field(name: "country", label: "Country");
        var conditional = Field(name: "state", label: "State", required: true);
        conditional.VisibleWhenField = "country";
        conditional.VisibleWhenEquals = "US";

        // country != "US" -> "state" is hidden, so even though it's Required and missing, no error.
        var values = new Dictionary<string, string> { ["country"] = "CA" };
        var errors = FormFieldValidator.Validate(new[] { trigger, conditional }, values);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_visible_required_field_is_validated_normally()
    {
        var trigger = Field(name: "country", label: "Country");
        var conditional = Field(name: "state", label: "State", required: true);
        conditional.VisibleWhenField = "country";
        conditional.VisibleWhenEquals = "US";

        var values = new Dictionary<string, string> { ["country"] = "US" };
        var errors = FormFieldValidator.Validate(new[] { trigger, conditional }, values);

        Assert.Single(errors);
    }

    [Fact]
    public void IsVisible_true_when_no_visibility_condition_set()
    {
        var field = Field();
        Assert.True(FormFieldValidator.IsVisible(field, new Dictionary<string, string>()));
    }

    [Fact]
    public void IsVisible_true_when_referenced_field_matches_expected_value()
    {
        var field = Field();
        field.VisibleWhenField = "trigger";
        field.VisibleWhenEquals = "yes";
        var values = new Dictionary<string, string> { ["trigger"] = "yes" };

        Assert.True(FormFieldValidator.IsVisible(field, values));
    }

    [Fact]
    public void IsVisible_false_when_referenced_field_does_not_match_expected_value()
    {
        var field = Field();
        field.VisibleWhenField = "trigger";
        field.VisibleWhenEquals = "yes";
        var values = new Dictionary<string, string> { ["trigger"] = "no" };

        Assert.False(FormFieldValidator.IsVisible(field, values));
    }

    [Fact]
    public void Required_checkbox_explicitly_unchecked_fails_validation()
    {
        // A checkbox's bound value is always "true"/"false" once touched — "false" isn't an
        // empty string, so a generic empty-string required check would wrongly let an
        // unchecked-but-touched required checkbox through.
        var field = Field(type: "checkbox", required: true);
        var values = new Dictionary<string, string> { ["field"] = "false" };

        var errors = FormFieldValidator.Validate(new List<FormField> { field }, values);

        Assert.Single(errors);
    }

    [Fact]
    public void Required_checkbox_checked_passes_validation()
    {
        var field = Field(type: "checkbox", required: true);
        var values = new Dictionary<string, string> { ["field"] = "true" };

        var errors = FormFieldValidator.Validate(new List<FormField> { field }, values);

        Assert.Empty(errors);
    }
}
