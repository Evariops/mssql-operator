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

    // ── Memory validation tests ─────────────────────────────

    [Fact]
    public void Create_WithValidMemory_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "8Gi", SystemReserve = "2Gi" }
            }
        };

        _validator.Create(entity, false).Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_MemoryTotalTooSmall_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "2Gi", SystemReserve = "512Mi" }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_MemoryReserveTooSmall_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "8Gi", SystemReserve = "256Mi" }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_MemorySqlMaxTooSmall_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "4Gi", SystemReserve = "3Gi" }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_MemoryMinMemoryExceedsMax_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "8Gi", SystemReserve = "2Gi", MinMemory = "7Gi" }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_WithValidMinMemory_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Memory = new MemorySpec { Total = "8Gi", SystemReserve = "2Gi", MinMemory = "2Gi" }
            }
        };

        _validator.Create(entity, false).Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_NoMemory_ReturnsSuccess()
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

        _validator.Create(entity, false).Valid.Should().BeTrue();
    }

    // ── CpuPinning validation tests ─────────────────────────

    [Fact]
    public void Create_CpuPinning_ValidIntegerCpu_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Requests = new Entities.Common.ResourceQuantity { Cpu = "4" },
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "4" }
                }
            }
        };

        _validator.Create(entity, false).Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_CpuPinning_NoCpuLimit_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Requests = new Entities.Common.ResourceQuantity { Cpu = "4" }
                }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_CpuPinning_MillicoreCpu_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "4000m" }
                }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_CpuPinning_MismatchedCpu_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Requests = new Entities.Common.ResourceQuantity { Cpu = "2" },
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "4" }
                }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_CpuPinning_FractionalCpu_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "2.5" }
                }
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_CpuPinning_OnlyLimitSet_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Resources = new ResourceRequirements
                {
                    CpuPinning = true,
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "4" }
                }
            }
        };

        _validator.Create(entity, false).Valid.Should().BeTrue();
    }

    // ── RuntimeClassName validation tests ────────────────────

    [Fact]
    public void Create_KataWithoutCpuLimit_ReturnsFail()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                RuntimeClassName = "kata"
            }
        };

        var result = _validator.Create(entity, false);
        result.Valid.Should().BeFalse();
    }

    [Fact]
    public void Create_KataWithCpuLimit_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                RuntimeClassName = "kata",
                Resources = new ResourceRequirements
                {
                    Limits = new Entities.Common.ResourceQuantity { Cpu = "4" }
                }
            }
        };

        _validator.Create(entity, false).Valid.Should().BeTrue();
    }

    [Fact]
    public void Create_NonKataRuntime_NoCpuLimit_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                RuntimeClassName = "gvisor"
            }
        };

        _validator.Create(entity, false).Valid.Should().BeTrue();
    }
}
