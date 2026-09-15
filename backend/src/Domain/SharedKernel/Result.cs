using System.Diagnostics.CodeAnalysis;

namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>
/// Outcome of an operation whose failure is an expected state, not an exceptional one
/// (validation, protocol negative responses, timeouts). Exceptions remain reserved for
/// genuinely exceptional conditions.
/// </summary>
/// <remarks>
/// A <c>default</c> instance is a failure carrying <c>default(TError)</c>; construct
/// instances only through <see cref="Ok"/> and <see cref="Fail"/>.
/// </remarks>
public readonly struct Result<TValue, TError> : IEquatable<Result<TValue, TError>>
{
    private readonly TValue? _value;
    private readonly TError? _error;

    private Result(bool isSuccess, TValue? value, TError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>The success value. Throws when accessed on a failure.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Result is a failure; it has no value.");

    /// <summary>The error. Throws when accessed on a success.</summary>
    public TError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Result is a success; it has no error.");

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Ok/Fail factories on the result type are the call-site convention this codebase standardizes on.")]
    public static Result<TValue, TError> Ok(TValue value) => new(true, value, default);

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "Ok/Fail factories on the result type are the call-site convention this codebase standardizes on.")]
    public static Result<TValue, TError> Fail(TError error) => new(false, default, error);

    public static implicit operator Result<TValue, TError>(TValue value) => Ok(value);

    public static implicit operator Result<TValue, TError>(TError error) => Fail(error);

    public Result<TOut, TError> Map<TOut>(Func<TValue, TOut> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return IsSuccess
            ? Result<TOut, TError>.Ok(transform(_value!))
            : Result<TOut, TError>.Fail(_error!);
    }

    public Result<TOut, TError> Bind<TOut>(Func<TValue, Result<TOut, TError>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsSuccess ? next(_value!) : Result<TOut, TError>.Fail(_error!);
    }

    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<TError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(_error!);
    }

    public bool Equals(Result<TValue, TError> other) =>
        IsSuccess == other.IsSuccess
        && EqualityComparer<TValue?>.Default.Equals(_value, other._value)
        && EqualityComparer<TError?>.Default.Equals(_error, other._error);

    public override bool Equals(object? obj) => obj is Result<TValue, TError> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(IsSuccess, _value, _error);

    public static bool operator ==(Result<TValue, TError> left, Result<TValue, TError> right) => left.Equals(right);

    public static bool operator !=(Result<TValue, TError> left, Result<TValue, TError> right) => !left.Equals(right);

    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Fail({_error})";
}
