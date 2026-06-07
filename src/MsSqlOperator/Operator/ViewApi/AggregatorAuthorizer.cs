using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// Authorizes aggregated-API requests. The kube-aggregator terminates the client connection, validates
/// its own front-proxy client certificate against <c>requestheader-client-ca-file</c>, and forwards the
/// authenticated identity in <c>X-Remote-User</c>/<c>X-Remote-Group</c> headers. We trust those headers
/// (the connection arrives over the aggregator's mTLS) and delegate authorization to the API server via
/// a SubjectAccessReview so that normal RBAC on the <c>view.mssql.operator</c> resources applies.
///
/// NOTE: full hardening also validates the front-proxy client certificate against the CA published in
/// the <c>extension-apiserver-authentication</c> ConfigMap (kube-system). That requires Kestrel client-cert
/// negotiation and is verified on-cluster; the SubjectAccessReview below is the authorization gate.
/// </summary>
public sealed class AggregatorAuthorizer(IKubernetes client, ILogger<AggregatorAuthorizer> logger)
{
    public const string Group = "view.mssql.operator";

    public async Task<(bool Allowed, int Status, string? Reason)> AuthorizeAsync(
        HttpContext ctx, string resource, string verb, string? @namespace, CancellationToken ct)
    {
        var user = ctx.Request.Headers["X-Remote-User"].FirstOrDefault();
        if (string.IsNullOrEmpty(user))
            return (false, StatusCodes.Status401Unauthorized, "missing X-Remote-User (request did not come through the aggregator)");

        var groups = ctx.Request.Headers["X-Remote-Group"].Where(g => g != null).Cast<string>().ToList();

        var sar = new V1SubjectAccessReview
        {
            Spec = new V1SubjectAccessReviewSpec
            {
                User = user,
                Groups = groups.Count > 0 ? groups : null,
                ResourceAttributes = new V1ResourceAttributes
                {
                    Group = Group,
                    Resource = resource,
                    Verb = verb,
                    NamespaceProperty = @namespace,
                },
            },
        };

        try
        {
            var result = await client.AuthorizationV1.CreateSubjectAccessReviewAsync(sar, cancellationToken: ct);
            if (result.Status?.Allowed == true)
                return (true, StatusCodes.Status200OK, null);
            return (false, StatusCodes.Status403Forbidden, result.Status?.Reason ?? "not allowed");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "view: SubjectAccessReview failed for {User} {Verb} {Resource}", user, verb, resource);
            return (false, StatusCodes.Status403Forbidden, "authorization check failed");
        }
    }
}
