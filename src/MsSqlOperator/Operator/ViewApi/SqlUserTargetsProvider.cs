using Microsoft.Extensions.Logging;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Operator.Client;
using MsSqlOperator.Services.ConnectionFactory;
using MsSqlOperator.Services.Filters;
using MsSqlOperator.Services.Kubernetes;
using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Operator.ViewApi;

/// <summary>
/// <c>sqlusertargets</c>: live login / database-user / role state, computed from
/// <c>sys.server_principals</c>, <c>sys.database_principals</c> and the role-membership catalogs for
/// every target each SqlUser selects. Replaces the removed per-target detail that used to live in
/// <c>SqlUser.status</c>. Two row shapes: one <c>Login</c> row per (user, target) carrying the server
/// login state + server roles, then one <c>DatabaseUser</c> row per selected database.
/// </summary>
public sealed class SqlUserTargetsProvider(
    ILogger<SqlUserTargetsProvider> logger,
    IKubernetesClient kubernetesClient,
    ITargetResolver targetResolver,
    ISqlConnectionFactory connectionFactory,
    ISqlServerManager sqlServerManager,
    IDatabaseFilter databaseFilter,
    ISqlUserManager userManager) : IViewResourceProvider
{
    public ApiResource Resource { get; } = new()
    {
        Name = "sqlusertargets",
        SingularName = "sqlusertarget",
        Kind = "SqlUserTarget",
        ShortNames = ["sut"],
    };

    public IReadOnlyList<TableColumnDefinition> Columns { get; } =
    [
        new() { Name = "Name", Type = "string" },
        new() { Name = "User", Type = "string" },
        new() { Name = "Target", Type = "string" },
        new() { Name = "Scope", Type = "string" },
        new() { Name = "Database", Type = "string" },
        new() { Name = "Exists", Type = "boolean" },
        new() { Name = "Enabled", Type = "boolean" },
        new() { Name = "Roles", Type = "string" },
    ];

    public async Task<List<Dictionary<string, object?>>> ListAsync(string? @namespace, string? name, CancellationToken ct)
    {
        var items = new List<Dictionary<string, object?>>();
        var users = await kubernetesClient.ListAsync<SqlUser>(@namespace, null, ct);

        foreach (var user in users)
        {
            var login = user.Spec.Login;
            if (login is null || string.IsNullOrWhiteSpace(login.Name)) continue;
            var userNs = user.Metadata.NamespaceProperty;

            List<ResolvedTarget> targets;
            try { targets = await targetResolver.ResolveAsync(user.Spec.Target, userNs, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "view: resolve targets for {User}", user.Metadata.Name); continue; }

            foreach (var target in targets)
            {
                try
                {
                    await using var conn = await ViewTargets.OpenAsync(target, connectionFactory, targetResolver, ct);
                    if (conn is null)
                    {
                        logger.LogWarning("view: no reachable primary for {User}/{Target}", user.Metadata.Name, target.Name);
                        continue;
                    }

                    var loginEnabled = await userManager.IsLoginEnabledAsync(conn, login.Name, ct);
                    var loginExists = loginEnabled is not null;
                    var serverRoles = loginExists ? await userManager.GetServerRoleMembershipsAsync(conn, login.Name, ct) : [];

                    items.Add(Item(user.Metadata.Name, userNs, login.Name, target.Name,
                        scope: "Login", database: null, exists: loginExists, enabled: loginEnabled,
                        roles: serverRoles, key: $"{user.Metadata.Name}.{target.Name}.login"));

                    // Per-database user detail, honoring per-target overrides like the controller does.
                    var ov = MatchOverride(user.Spec.Overrides, target);
                    var dbPermissions = ov?.DatabasePermissions ?? user.Spec.DatabasePermissions;
                    if (dbPermissions is { Count: > 0 })
                    {
                        var allDbs = await sqlServerManager.GetDatabaseNamesAsync(conn, ct);
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var perm in dbPermissions)
                        foreach (var db in databaseFilter.Apply(allDbs, perm.Databases, perm.DatabaseFilter))
                        {
                            if (!seen.Add(db)) continue;
                            var dbUserExists = await userManager.DatabaseUserExistsAsync(conn, db, login.Name, ct);
                            var dbRoles = dbUserExists ? await userManager.GetDatabaseRoleMembershipsAsync(conn, db, login.Name, ct) : [];
                            items.Add(Item(user.Metadata.Name, userNs, login.Name, target.Name,
                                scope: "DatabaseUser", database: db, exists: dbUserExists, enabled: null,
                                roles: dbRoles, key: $"{user.Metadata.Name}.{target.Name}.{db}"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "view: user state for {User}/{Target}", user.Metadata.Name, target.Name);
                }
            }
        }

        return name is null ? items : items.Where(i => ItemName(i) == name).ToList();
    }

    private static TargetOverride? MatchOverride(List<TargetOverride>? overrides, ResolvedTarget target)
    {
        if (overrides is null) return null;
        return target.Kind == "SqlAvailabilityGroup"
            ? overrides.FirstOrDefault(o => o.AvailabilityGroup == target.Name)
            : overrides.FirstOrDefault(o => o.Instance == target.Name);
    }

    private static Dictionary<string, object?> Item(string user, string ns, string login, string target,
        string scope, string? database, bool exists, bool? enabled, List<string> roles, string key) => new()
    {
        ["apiVersion"] = "view.mssql.operator/v1alpha1",
        ["kind"] = "SqlUserTarget",
        ["metadata"] = new Dictionary<string, object?> { ["name"] = Sanitize(key), ["namespace"] = ns },
        ["user"] = user,
        ["login"] = login,
        ["target"] = target,
        ["scope"] = scope,
        ["database"] = database,
        ["exists"] = exists,
        ["enabled"] = enabled,
        ["roles"] = roles,
    };

    public TableRow ToRow(Dictionary<string, object?> item) => new()
    {
        Cells =
        [
            ItemName(item), item.GetValueOrDefault("login"), item.GetValueOrDefault("target"),
            item.GetValueOrDefault("scope"), item.GetValueOrDefault("database"),
            item.GetValueOrDefault("exists"), item.GetValueOrDefault("enabled"),
            item.GetValueOrDefault("roles") is List<string> r ? string.Join(",", r) : "",
        ]
    };

    private static string ItemName(Dictionary<string, object?> item) =>
        item["metadata"] is Dictionary<string, object?> m && m.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";

    private static string Sanitize(string s) =>
        new(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray());
}
