using System.Text.Json.Serialization;

namespace NxtUI.Filtering;

// ── Node hierarchy ─────────────────────────────────────────────────────────────
//
// The AST is produced by the C# parser (server-side string input) or deserialized
// from JSON sent by the JS parser (client-side string input). Both paths share the
// same node types. Global (field-less) terms are expanded to an OrNode over the
// configured searchable fields before the AST leaves either parser.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AndNode),       "and")]
[JsonDerivedType(typeof(OrNode),        "or")]
[JsonDerivedType(typeof(NotNode),       "not")]
[JsonDerivedType(typeof(FieldTermNode), "field")]
public abstract record FilterNode;

public record AndNode(FilterNode Left, FilterNode Right) : FilterNode;
public record OrNode(FilterNode Left, FilterNode Right) : FilterNode;
public record NotNode(FilterNode Operand) : FilterNode;

/// <summary>
/// A filter scoped to a single document field. All global-term expansion has
/// already been done before this node is produced, so Field is always a real
/// document field name (alias resolution also done at parse time).
/// </summary>
public record FieldTermNode(
    string      Field,
    MatchType   MatchType,
    bool        CaseSensitive,
    FilterValue Value
) : FilterNode;

// ── Value hierarchy ────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StringValue),     "string")]
[JsonDerivedType(typeof(NumberValue),     "number")]
[JsonDerivedType(typeof(BoolValue),       "bool")]
[JsonDerivedType(typeof(DateValue),       "date")]
[JsonDerivedType(typeof(TimeOfDayValue),  "time")]
[JsonDerivedType(typeof(NullValue),       "null")]
[JsonDerivedType(typeof(RangeValue),      "range")]
public abstract record FilterValue;

public record StringValue(string Value) : FilterValue;
public record NumberValue(double Value) : FilterValue;

/// <summary>A bare `true`/`false` literal — always resolves to MatchType.Exact
/// (equality); comparison operators and ranges are rejected for it at parse time
/// since "greater than true" etc. has no meaning.</summary>
public record BoolValue(bool Value) : FilterValue;

/// <summary>UTC instant. Both the JS and C# parsers convert local literals to UTC before
/// storing them here so the MongoDB builder never has to think about time zones.</summary>
public record DateValue(DateTime Value) : FilterValue;

/// <summary>Time-of-day comparison (hh:mm or hh:mm:ss). Compares only the time portion
/// of a DateTime field, ignoring the date — mirrors JS filter.js { type:'time', seconds }.</summary>
public record TimeOfDayValue(int Seconds) : FilterValue;

public record NullValue : FilterValue;

/// <summary>Inclusive range produced by the <c>..</c> operator.</summary>
public record RangeValue(FilterValue Low, FilterValue High) : FilterValue;

// ── Match type ─────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter<MatchType>))]
public enum MatchType
{
    /// <summary>Substring match (case-controlled by <c>CaseSensitive</c>).</summary>
    Contains,

    /// <summary>Whole-value equality (<c>:=</c> prefix).</summary>
    Exact,

    /// <summary>Glob pattern containing <c>*</c> or <c>?</c> wildcards.</summary>
    Glob,

    /// <summary>Field is null or missing (<c>field:null</c>).</summary>
    IsNull,

    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,

    /// <summary>Inclusive range (<c>..</c> operator). Value must be a <see cref="RangeValue"/>.</summary>
    Between,
}
