using k8s.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MsSqlOperator.Entities.V1Alpha1;
using MsSqlOperator.Services.SqlServer;
using MsSqlOperator.Tests.Fakes;

namespace MsSqlOperator.Tests.Services;

public class SmkBackupsTests
{
    private readonly FakeKubernetesClient _k8s = new() { StoreWrites = true };
    private readonly FakeServiceMasterKeyManager _smk = new();
    private readonly SqlConnection _connection = new("Server=my-sql-0.my-sql.default.svc.cluster.local");
    private readonly SqlInstance _owner = new() { Metadata = new V1ObjectMeta { Name = "my-sql", NamespaceProperty = "default", Uid = "uid-1" } };

    private Task<SmkBackupResult> EnsureAsync() => SmkBackups.EnsureAsync(_connection, "my-sql-smk-backup", "default", _owner, "pw",
        _smk, _k8s, TimeProvider.System, NullLogger.Instance, CancellationToken.None);

    private V1Secret Stored() => _k8s.GetAsync<V1Secret>("my-sql-smk-backup", "default").Result!;

    [Fact]
    public async Task EnsureAsync_KeyRegeneratedByHand_AddsAVersionAndKeepsTheEarlierOne()
    {
        // acceptance: a manual regeneration of the SMK on a replica adds a version and keeps the old one.
        var first = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Unspecified);
        _smk.SmkModifyDate = first;
        _smk.BackupBytes = [1];
        Assert.Equal(new SmkBackupResult(true, "smk-20261001T080000Z.bak", false), await EnsureAsync());

        _smk.SmkModifyDate = first.AddDays(3);
        _smk.BackupBytes = [2];
        var result = await EnsureAsync();

        Assert.Equal(new SmkBackupResult(true, "smk-20261004T080000Z.bak", true), result);
        var data = Stored().Data;
        Assert.Equal([1], data["smk-20261001T080000Z.bak"]);
        Assert.Equal([2], data["smk-20261004T080000Z.bak"]);
        Assert.Equal("smk-20261004T080000Z.bak", Stored().Metadata.Annotations[SmkBackups.LatestKeyAnnotation]);
    }

    [Fact]
    public async Task EnsureAsync_KeyUnchanged_TakesNoBackup()
    {
        _smk.SmkModifyDate = new DateTime(2026, 10, 1, 8, 0, 0);
        await EnsureAsync();

        var result = await EnsureAsync();

        Assert.False(result.Added);
        Assert.Single(_smk.BackupCalls);
    }

    [Fact]
    public async Task EnsureAsync_BackupTakenByAnEarlierVersion_IsKeptAsTheOldest()
    {
        _k8s.SetGet("my-sql-smk-backup", "default", new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = "my-sql-smk-backup", NamespaceProperty = "default" },
            Data = new Dictionary<string, byte[]> { [SmkBackups.LegacyKey] = [9] },
        });
        _smk.SmkModifyDate = new DateTime(2026, 10, 1, 8, 0, 0);

        var result = await EnsureAsync();

        Assert.True(result.Added);
        Assert.Equal([SmkBackups.LegacyKey, "smk-20261001T080000Z.bak"], SmkBackups.Versions(Stored().Data));
    }

    [Fact]
    public void WithVersion_BeyondTheLimit_PrunesTheOldestNeverTheLatest()
    {
        var data = new Dictionary<string, byte[]> { [SmkBackups.LegacyKey] = [0] };
        for (var day = 1; day <= SmkBackups.MaxVersions; day++)
            data = SmkBackups.WithVersion(data, SmkBackups.KeyFor(new DateTime(2026, 10, day)), [(byte)day]);

        Assert.Equal(SmkBackups.MaxVersions, SmkBackups.Versions(data).Count);
        Assert.DoesNotContain(SmkBackups.LegacyKey, data.Keys);
        Assert.Contains(SmkBackups.KeyFor(new DateTime(2026, 10, 10)), data.Keys);
    }

    [Fact]
    public void WithVersion_LatestSortingBeforeTheOthers_IsStillKept()
    {
        // A key whose modify date went back (a restored SMK) is the latest even if it sorts first.
        var data = new Dictionary<string, byte[]>();
        for (var day = 10; day < 10 + SmkBackups.MaxVersions; day++)
            data = SmkBackups.WithVersion(data, SmkBackups.KeyFor(new DateTime(2026, 10, day)), [(byte)day]);

        var key = SmkBackups.KeyFor(new DateTime(2026, 9, 1));
        data = SmkBackups.WithVersion(data, key, [1]);

        Assert.Contains(key, data.Keys);
        Assert.Equal(SmkBackups.MaxVersions, SmkBackups.Versions(data).Count);
        Assert.DoesNotContain(SmkBackups.KeyFor(new DateTime(2026, 10, 10)), data.Keys);
    }
}
