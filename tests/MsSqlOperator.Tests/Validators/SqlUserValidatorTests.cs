using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlUserValidatorTests
{
    private readonly SqlUserValidator _validator = new();

    private static SqlUser MakeUser(Action<SqlUserSpec>? configure = null)
    {
        var spec = new SqlUserSpec
        {
            Target = new TargetSelector { Instances = ["my-instance"] },
            Login = new LoginSpec
            {
                Name = "app_user",
                PasswordSecretRef = new SecretReference { Name = "secret", Key = "password" }
            }
        };
        configure?.Invoke(spec);
        return new SqlUser { Spec = spec };
    }

    // ── Target ─────────────────────────────────────────────

    [Fact]
    public void Create_ValidUser_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_EmptyTarget_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector()), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_TargetWithInstances_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector { Instances = ["db1", "db2"] }), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_TargetWithLabels_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector
        {
            MatchLabels = new Dictionary<string, string> { ["env"] = "prod" }
        }), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_TargetWithAgs_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector { AvailabilityGroups = ["ag1"] }), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_TargetWithMatchLabels_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector { MatchLabels = new() { ["app"] = "sql" } }), false);
        Assert.True(result.Valid);
    }

    // ── Overrides ──────────────────────────────────────────

    [Fact]
    public void Create_OverrideBothInstanceAndAg_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Overrides =
        [
            new TargetOverride { Instance = "a", AvailabilityGroup = "b" }
        ]), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_OverrideNeitherInstanceNorAg_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Overrides =
        [
            new TargetOverride()
        ]), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_ValidOverride_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Overrides =
        [
            new TargetOverride { Instance = "db1", ServerRoles = ["sysadmin"] }
        ]), false);
        Assert.True(result.Valid);
    }

    // ── Login ──────────────────────────────────────────────

    [Fact]
    public void Create_NoLogin_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Login = null), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_EmptyLoginName_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Login = new LoginSpec { Name = "" }), false);
        Assert.False(result.Valid);
    }

    // ── Expiration ─────────────────────────────────────────

    [Fact]
    public void Create_ValidExpiresAt_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            ExpiresAt = DateTime.UtcNow.AddHours(1).ToString("O"),
            Action = "Disable"
        }), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_ExpiresAtInPast_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            ExpiresAt = "2020-01-01T00:00:00Z",
            Action = "Disable"
        }), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Update_ExpiresAtInPast_Allowed()
    {
        var entity = MakeUser(s => s.Expiration = new ExpirationSpec
        {
            ExpiresAt = "2020-01-01T00:00:00Z",
            Action = "Disable"
        });
        var result = _validator.Update(entity, entity, false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_ValidTtl_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Ttl = "72h",
            Action = "Delete"
        }), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_InvalidTtl_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Ttl = "abc",
            Action = "Disable"
        }), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_InvalidAction_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Ttl = "1h",
            Action = "Noop"
        }), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_ExpirationNoDateOrTtl_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Action = "Disable"
        }), false);
        Assert.False(result.Valid);
    }

    // ── Identifier rules ─────────────────────────

    [Theory]
    [InlineData("a]b")]
    [InlineData("a'b")]
    [InlineData("a\nb")]
    public void Create_ExoticLoginName_ReturnsFail(string name)
    {
        var result = _validator.Create(MakeUser(s => s.Login!.Name = name), false);
        Assert.False(result.Valid);
        Assert.Contains("login.name", result.Message);
    }

    [Fact]
    public void Create_LoginName129Chars_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Login!.Name = new string('a', 129)), false);
        Assert.False(result.Valid);
        Assert.Contains("128", result.Message);
    }

    [Fact]
    public void Create_ExoticDatabaseName_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.DatabasePermissions =
            [new DatabasePermissionSpec { Databases = ["db]x"], Roles = ["db_datareader"] }]), false);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Create_ExoticRoleName_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.DatabasePermissions =
            [new DatabasePermissionSpec { Databases = ["appdb"], Roles = ["r'x"] }]), false);
        Assert.False(result.Valid);
    }

    // ── Server roles allowlist ───────────────────

    [Fact]
    public void Create_UnknownServerRole_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.ServerRoles = ["mycustomrole"]), false);
        Assert.False(result.Valid);
        Assert.Contains("fixed SQL Server role", result.Message);
    }

    [Fact]
    public void Create_NonPrivilegedFixedRole_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.ServerRoles = ["dbcreator"]), false);
        Assert.True(result.Valid);
    }

    [Fact]
    public void Create_PrivilegedRole_SucceedsWithWarning()
    {
        var result = _validator.Create(MakeUser(s => s.ServerRoles = ["sysadmin"]), false);
        Assert.True(result.Valid);
        Assert.True(result.Warnings is { Length: > 0 });
        Assert.Contains("sysadmin", result.Warnings![0]);
    }
}
