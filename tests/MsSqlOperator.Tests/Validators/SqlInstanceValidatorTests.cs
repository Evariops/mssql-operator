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
                Edition = "Developer",
                Memory = new MemorySpec { Total = "4Gi" }
            }
        };

        var result = _validator.Create(entity, false);

        Assert.True(result.Valid);
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

        Assert.False(result.Valid);
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

        Assert.False(result.Valid);
    }

    [Fact]
    public void Update_VersionUpgrade_ReturnsSuccess()
    {
        var oldEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2019", Edition = "Developer", Memory = new MemorySpec { Total = "4Gi" } }
        };
        var newEntity = new SqlInstance
        {
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2022", Edition = "Developer", Memory = new MemorySpec { Total = "4Gi" } }
        };

        var result = _validator.Update(oldEntity, newEntity, false);

        Assert.True(result.Valid);
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
            Spec = new SqlInstanceSpec { Mode = "SingleNode", Version = "2019", Edition = "Developer", Memory = new MemorySpec { Total = "4Gi" } }
        };

        var result = _validator.Update(oldEntity, newEntity, false);

        Assert.False(result.Valid);
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

        Assert.False(result.Valid);
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

        Assert.True(_validator.Create(entity, false).Valid);
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
        Assert.False(result.Valid);
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
        Assert.False(result.Valid);
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
        Assert.False(result.Valid);
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
        Assert.False(result.Valid);
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

        Assert.True(_validator.Create(entity, false).Valid);
    }

    [Fact]
    public void Create_NoMemoryNoResources_IsRejected()
    {
        // BestEffort QoS refused explicitly.
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
        Assert.False(result.Valid);
        Assert.Contains("spec.memory or spec.resources", result.Message);
    }

    [Fact]
    public void Create_ResourcesOnly_ReturnsSuccess()
    {
        var entity = new SqlInstance
        {
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = "2022",
                Edition = "Developer",
                Resources = new MsSqlOperator.Entities.Common.ResourceRequirements { Limits = new MsSqlOperator.Entities.Common.ResourceQuantity { Memory = "4Gi" } }
            }
        };

        Assert.True(_validator.Create(entity, false).Valid);
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

        Assert.True(_validator.Create(entity, false).Valid);
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
        Assert.False(result.Valid);
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
        Assert.False(result.Valid);
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
        Assert.False(result.Valid);
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
        Assert.False(result.Valid);
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

        Assert.True(_validator.Create(entity, false).Valid);
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
        Assert.False(result.Valid);
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

        Assert.True(_validator.Create(entity, false).Valid);
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
                RuntimeClassName = "gvisor",
                Memory = new MemorySpec { Total = "4Gi" }
            }
        };

        Assert.True(_validator.Create(entity, false).Valid);
    }

    // ── Per-instance CU control ───────────────────────────

    private static SqlInstance Instance(string? cu = null, string? imageTag = null, string version = "2022",
        Dictionary<string, string>? annotations = null) => new()
        {
            Metadata = new V1ObjectMeta { Name = "sql", NamespaceProperty = "default", Annotations = annotations },
            Spec = new SqlInstanceSpec
            {
                Mode = "SingleNode",
                Version = version,
                Edition = "Developer",
                CumulativeUpdate = cu,
                ImageTag = imageTag,
                Memory = new MemorySpec { Total = "4Gi" },
            },
        };

    [Fact]
    public void Create_PinnedCumulativeUpdate_ReturnsSuccess()
    {
        Assert.True(_validator.Create(Instance(cu: "CU19"), false).Valid);
    }

    [Fact]
    public void Create_CumulativeUpdateAndImageTag_ReturnsFail()
    {
        Assert.False(_validator.Create(Instance(cu: "CU25", imageTag: "2022-CU24-ubuntu-22.04"), false).Valid);
    }

    [Fact]
    public void Create_FloatingImageTag_ReturnsFail()
    {
        // A floating tag lets the engine move under the databases with nothing in the spec
        // changing — the very thing pinning a CU exists to prevent.
        Assert.False(_validator.Create(Instance(imageTag: "2022-latest"), false).Valid);
    }

    [Fact]
    public void Update_CumulativeUpdateUpgrade_ReturnsSuccess()
    {
        var result = _validator.Update(Instance(cu: "CU19"), Instance(cu: "CU25"), false);

        Assert.True(result.Valid);
    }

    [Fact]
    public void Update_CumulativeUpdateDowngrade_ReturnsFail()
    {
        var result = _validator.Update(Instance(cu: "CU25"), Instance(cu: "CU19"), false);

        Assert.False(result.Valid);
        Assert.Contains("Cumulative update downgrade", result.Message);
    }

    [Fact]
    public void Update_CumulativeUpdateDowngrade_ViaImageTag_ReturnsFail()
    {
        // The guard compares the resolved builds, so pinning the CU one way and rolling it back the
        // other way does not slip past it.
        var result = _validator.Update(Instance(cu: "CU25"), Instance(imageTag: "2022-CU19-ubuntu-22.04"), false);

        Assert.False(result.Valid);
        Assert.Contains("Cumulative update downgrade", result.Message);
    }

    [Fact]
    public void Update_CumulativeUpdateDowngrade_WithApprovalAnnotation_ReturnsSuccess()
    {
        var approved = Instance(cu: "CU19", annotations: new Dictionary<string, string>
        {
            [MsSqlOperator.Configuration.SqlServerImages.AllowVersionDowngradeAnnotation] = "true",
        });

        Assert.True(_validator.Update(Instance(cu: "CU25"), approved, false).Valid);
    }
}
