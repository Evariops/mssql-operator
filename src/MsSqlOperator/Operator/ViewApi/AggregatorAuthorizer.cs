using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// Authorizes aggregated-API requests in two gates:
///   1. <b>Authentication</b> — <see cref="FrontProxyAuthenticator"/> proves the request really came
///      through the kube-aggregator by validating its front-proxy client certificate against
///      <c>requestheader-client-ca-file</c> before trusting the forwarded <c>X-Remote-User</c>/
///      <c>X-Remote-Group</c> identity headers.
///   2. <b>Authorization</b> — a SubjectAccessReview delegates the access decision to the API server so
///      normal RBAC on the <c>view.mssql.operator</c> resources applies to that authenticated identity.
/// </summary>
public sealed class AggregatorAuthorizer(IKubernetes client, FrontProxyAuthenticator frontProxy, ILogger<AggregatorAuthorizer> logger)
{
    public const string Group = "view.mssql.operator";

    public async Task<(bool Allowed, int Status, string? Reason)> AuthorizeAsync(
        HttpContext ctx, string resource, string verb, string? @namespace, CancellationToken ct)
    {
        var auth = await frontProxy.AuthenticateAsync(ctx, ct);
        if (!auth.Ok)
            return (false, auth.Status, auth.Reason);

        var user = auth.User!;
        var groups = auth.Groups;

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
