using System.Diagnostics.CodeAnalysis;

namespace NxtUI.Core;

/// <summary>
/// Discriminated result: either a successful value or an error message.
/// Default (struct zero-value) represents the "not yet loaded" / loading state.
/// </summary>
public readonly struct Result<T>
{
    private readonly bool _loaded;
    private readonly T? _value;
    private readonly string? _error;

    private Result(T value) { _value = value; _loaded = true; _error = null; }
    private Result(string error) { _error = error; _loaded = true; _value = default; }

    public T? Value => _value;
    public string? Error => _error;

    [MemberNotNullWhen(true, nameof(Value))]
    public bool IsOk => _loaded && _error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailed => _error is not null;

    /// <summary>True when neither value nor error has been set (initial / loading state).</summary>
    public bool IsLoading => !_loaded;

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(string error) => new(error);
}
