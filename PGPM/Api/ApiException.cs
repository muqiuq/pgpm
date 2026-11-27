namespace PGPM.Api;

/// <summary>
/// A v1 error with its HTTP status and the stable <c>code</c> of the ProblemDetails body (docs/03 §3.3, from PNMTD).
/// Thrown by the domain services, written by <see cref="ApiExceptionFilterAttribute"/>. <see cref="Extensions"/> become
/// members of the body next to <c>code</c>.
/// </summary>
public sealed class ApiException(int status, string code, string detail, IDictionary<string, string[]>? errors = null,
    IReadOnlyDictionary<string, object?>? extensions = null)
    : Exception(detail)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public IDictionary<string, string[]>? Errors { get; } = errors;

    public IReadOnlyDictionary<string, object?>? Extensions { get; } = extensions;

    public static ApiException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, $"{what} not found.");

    public static ApiException Conflict(string code, string detail) =>
        new(StatusCodes.Status409Conflict, code, detail);

    /// <summary>403 <c>forbidden</c>: a check an attribute can't express.</summary>
    public static ApiException Forbidden(string detail) => new(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, detail);

    /// <summary>
    /// 403 <c>missing_right</c> with the area and level (docs/03 §6.1), for a right only a service can tell. Phase 1 types
    /// the area and level with the rights catalogue's enums.
    /// </summary>
    public static ApiException MissingRight(string area, string level) =>
        new(StatusCodes.Status403Forbidden, ApiErrorCodes.MissingRight, $"You need {area}: {level}.",
            extensions: new Dictionary<string, object?> { ["area"] = area, ["level"] = level });

    /// <summary>400 <c>validation_failed</c> without a field.</summary>
    public static ApiException BadRequest(string detail) => new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, detail);

    public static ApiException Invalid(string field, string message) =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "One or more fields are invalid.",
            new Dictionary<string, string[]> { [field] = [message] });
}

public static class ApiErrorCodes
{
    public const string NotFound = "not_found";
    public const string ValidationFailed = "validation_failed";
    public const string DuplicateKey = "duplicate_key";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string MissingRight = "missing_right";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";
    public const string Error = "error";

    public static string ForStatus(int status) => status switch
    {
        400 => ValidationFailed,
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        405 => MethodNotAllowed,
        409 => DuplicateKey,
        415 => UnsupportedMediaType,
        429 => RateLimited,
        >= 500 => InternalError,
        _ => Error,
    };
}

/// <summary>
/// Collects field errors and throws one 400 <c>validation_failed</c> with all of them.
/// Field names are the JSON names (camelCase).
/// </summary>
public sealed class ApiValidator
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public ApiValidator Check(bool valid, string field, string message)
    {
        if (!valid)
        {
            Add(field, message);
        }
        return this;
    }

    public ApiValidator Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }
        list.Add(message);
        return this;
    }

    public bool HasErrors => _errors.Count > 0;

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "One or more fields are invalid.",
                _errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}
