using FluentAssertions;
using k8s.Models;
using MsSqlOperator.Entities.Common;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Validators;

namespace MsSqlOperator.Tests.Validators;

public class SqlInstanceValidatorTests
{
    private readonly SqlInstanceValidator _validator = new();

    [Fact]
    public void Create_ValidSingleNode_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer"
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_InvalidMode_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "Invalid",
                Version = "2022",
                Edition = "Developer"
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_InvalidEdition_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "InvalidEdition"
            }
        };

        var result = _validator.Create(entity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Update_VersionUpgrade_ReturnsSuccess()
    {
        var oldEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2019", Edition = "Developer" }
        };
        var newEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2022", Edition = "Developer" }
        };

        var result = _validator.Update(oldEntity, newEntity, false);

        result.Valid.Should().BeTrue();
    }

    [Fact]
    public void Update_VersionDowngrade_ReturnsFail()
    {
        var oldEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2022", Edition = "Developer" }
        };
        var newEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2019", Edition = "Developer" }
        };

        var result = _validator.Update(oldEntity, newEntity, false);

        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Update_EditionChange_ReturnsFail()
    {
        var oldEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2022", Edition = "Developer" }
        };
        var newEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2022", Edition = "Standard" }
        };

        var result = _validator.Update(oldEntity, newEntity, false);

        result.Valid.Should().BeFalse();
    }
}
