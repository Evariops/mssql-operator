using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlUserValidatorTests
{
    private readonly SqlUserValidator _validator = new();

    [Fact]
    public void Create_ValidUser_ReturnsSuccess()
    {
        var entity = new SqlUser
        {
            Spec = new SqlUserSpec
            {
                InstanceRef = new ResourceReference { Name = "my-instance" },
                Login = new LoginSpec
                {
                    Name = "app_user",
                    PasswordSecretRef = new SecretReference { Name = "secret", Key = "password" }
                }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_NoInstanceRef_ReturnsFail()
    {
        var entity = new SqlUser
        {
            Spec = new SqlUserSpec
            {
                Login = new LoginSpec { Name = "app_user" }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_NoLogin_ReturnsFail()
    {
        var entity = new SqlUser
        {
            Spec = new SqlUserSpec
            {
                InstanceRef = new ResourceReference { Name = "my-instance" }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_EmptyLoginName_ReturnsFail()
    {
        var entity = new SqlUser
        {
            Spec = new SqlUserSpec
            {
                InstanceRef = new ResourceReference { Name = "my-instance" },
                Login = new LoginSpec { Name = "" }
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }
}
