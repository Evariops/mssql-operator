using FluentAssertions;
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
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_EmptyTarget_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector()), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_TargetWithInstances_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector { Instances = ["db1", "db2"] }), false);
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_TargetWithLabels_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector
        {
            MatchLabels = new Dictionary<string, string> { ["env"] = "prod" }
        }), false);
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_TargetWithAgs_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector { AvailabilityGroups = ["ag1"] }), false);
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_TargetWithNamespaces_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector { Namespaces = ["ns1"], MatchLabels = new() { ["app"] = "sql" } }), false);
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_TargetWithNamespacesOnly_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Target = new TargetSelector { Namespaces = ["ns1"] }), false);
        result.Valid.Should().BeFalse();
    }

    // ── Overrides ──────────────────────────────────────────

    [Fact]
    public void Create_OverrideBothInstanceAndAg_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Overrides =
        [
            new TargetOverride { Instance = "a", AvailabilityGroup = "b" }
        ]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_OverrideNeitherInstanceNorAg_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Overrides =
        [
            new TargetOverride()
        ]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_ValidOverride_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Overrides =
        [
            new TargetOverride { Instance = "db1", ServerRoles = ["sysadmin"] }
        ]), false);
        result.Valid.Should().BeTrue();
    }

    // ── Login ──────────────────────────────────────────────

    [Fact]
    public void Create_NoLogin_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Login = null), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_EmptyLoginName_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Login = new LoginSpec { Name = "" }), false);
        result.Valid.Should().BeFalse();
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
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_ExpiresAtInPast_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            ExpiresAt = "2020-01-01T00:00:00Z",
            Action = "Disable"
        }), false);
        result.Valid.Should().BeFalse();
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
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_ValidTtl_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Ttl = "72h",
            Action = "Delete"
        }), false);
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_InvalidTtl_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Ttl = "abc",
            Action = "Disable"
        }), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidAction_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Ttl = "1h",
            Action = "Noop"
        }), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_ExpirationNoDateOrTtl_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Expiration = new ExpirationSpec
        {
            Action = "Disable"
        }), false);
        result.Valid.Should().BeFalse();
    }

    // ── Identifier rules ─────────────────────────

    [Theory]
    [InlineData("a]b")]
    [InlineData("a'b")]
    [InlineData("a\nb")]
    public void Create_ExoticLoginName_ReturnsFail(string name)
    {
        var result = _validator.Create(MakeUser(s => s.Login!.Name = name), false);
        result.Valid.Should().BeFalse();
        result.Message.Should().Contain("login.name");
    }

    [Fact]
    public void Create_LoginName129Chars_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Login!.Name = new string('a', 129)), false);
        result.Valid.Should().BeFalse();
        result.Message.Should().Contain("128");
    }

    [Fact]
    public void Create_ExoticDatabaseName_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.DatabasePermissions =
            [new DatabasePermissionSpec { Databases = ["db]x"], Roles = ["db_datareader"] }]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_ExoticRoleName_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.DatabasePermissions =
            [new DatabasePermissionSpec { Databases = ["appdb"], Roles = ["r'x"] }]), false);
        result.Valid.Should().BeFalse();
    }

    // ── Server roles allowlist ───────────────────

    [Fact]
    public void Create_UnknownServerRole_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.ServerRoles = ["mycustomrole"]), false);
        result.Valid.Should().BeFalse();
        result.Message.Should().Contain("fixed SQL Server role");
    }

    [Fact]
    public void Create_NonPrivilegedFixedRole_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.ServerRoles = ["dbcreator"]), false);
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_PrivilegedRole_SucceedsWithWarning()
    {
        var result = _validator.Create(MakeUser(s => s.ServerRoles = ["sysadmin"]), false);
        result.Valid.Should().BeTrue();
        result.Warnings.Should().NotBeNullOrEmpty();
        result.Warnings![0].Should().Contain("sysadmin");
    }
}
