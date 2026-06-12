using System.Net;

namespace MsSqlOperator.Services.GitHub;

/// <summary>
/// Typed failure of a GitHub API interaction. Transport errors, 5xx, auth and
/// rate-limit problems must surface as exceptions so the runtime backs off — they were
/// previously swallowed into null/[] and a partial deployment reported as success.
/// </summary>
public class GitHubFetchException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>403 with exhausted rate limit; <see cref="ResetAt"/> comes from X-RateLimit-Reset/Retry-After.</summary>
public class GitHubRateLimitException(string message, DateTimeOffset? resetAt)
    : GitHubFetchException(message, HttpStatusCode.Forbidden)
{
    public DateTimeOffset? ResetAt { get; } = resetAt;
}

/// <summary>Downloaded content does not match the git blob SHA announced by the Trees API.</summary>
public class GitHubIntegrityException(string message) : GitHubFetchException(message);
