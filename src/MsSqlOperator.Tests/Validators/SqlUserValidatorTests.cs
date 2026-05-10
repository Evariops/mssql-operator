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
            Targets = [new UserTarget { InstanceRef = new ResourceReference { Name = "my-instance" } }],
            Login = new LoginSpec
            {
                Name = "app_user",
                PasswordSecretRef = new SecretReference { Name = "secret", Key = "password" }
            }
        };
        configure?.Invoke(spec);
        return new SqlUser { Spec = spec };
    }

    // ── Targets ────────────────────────────────────────────

    [Fact]
    public void Create_ValidUser_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(), false);
        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_NoTargets_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Targets = null), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_EmptyTargets_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Targets = []), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_TargetWithBothRefs_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Targets =
        [
            new UserTarget
            {
                InstanceRef = new ResourceReference { Name = "a" },
                AvailabilityGroupRef = new ResourceReference { Name = "b" }
            }
        ]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_TargetWithNoRefs_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Targets = [new UserTarget()]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_DuplicateTargets_ReturnsFail()
    {
        var result = _validator.Create(MakeUser(s => s.Targets =
        [
            new UserTarget { InstanceRef = new ResourceReference { Name = "db1" } },
            new UserTarget { InstanceRef = new ResourceReference { Name = "db1" } }
        ]), false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_MultipleDistinctTargets_ReturnsSuccess()
    {
        var result = _validator.Create(MakeUser(s => s.Targets =
        [
            new UserTarget { InstanceRef = new ResourceReference { Name = "db1" } },
            new UserTarget { InstanceRef = new ResourceReference { Name = "db2" } },
            new UserTarget { AvailabilityGroupRef = new ResourceReference { Name = "ag1" } }
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
