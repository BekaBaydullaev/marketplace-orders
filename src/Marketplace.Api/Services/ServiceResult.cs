using System.Diagnostics.CodeAnalysis;

namespace Marketplace.Api.Services;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized
}

public record ServiceError(ErrorType Type, string Message)
{
    public static ServiceError Validation(string message) => new(ErrorType.Validation, message);
    public static ServiceError NotFound(string message) => new(ErrorType.NotFound, message);
    public static ServiceError Conflict(string message) => new(ErrorType.Conflict, message);
    public static ServiceError Unauthorized(string message) => new(ErrorType.Unauthorized, message);
}

public class ServiceResult<T>
{
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    public T? Value { get; }
    public ServiceError? Error { get; }

    private ServiceResult(T? value, ServiceError? error)
    {
        Value = value;
        Error = error;
        IsSuccess = error is null;
    }

    public static implicit operator ServiceResult<T>(T value) => new(value, null);
    public static implicit operator ServiceResult<T>(ServiceError error) => new(default, error);
}