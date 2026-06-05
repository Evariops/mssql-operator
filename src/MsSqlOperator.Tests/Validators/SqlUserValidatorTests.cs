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
}
