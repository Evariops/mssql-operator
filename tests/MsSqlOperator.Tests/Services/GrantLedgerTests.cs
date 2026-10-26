using MsSqlOperator.Services.SqlServer;

namespace MsSqlOperator.Tests.Services;

/// <summary>Grants removed from the spec are revoked, others' grants never.</summary>
public class GrantLedgerTests
{
    [Fact]
    public void Stale_IsWhatWasGrantedAndIsNoLongerWanted()
    {
        var ledger = GrantLedger.For(
            ["SqlInstance/a|srole|dbcreator", "SqlInstance/a|srole|bulkadmin", "SqlInstance/b|srole|sysadmin"],
            "SqlInstance/a");

        Assert.Equal(["bulkadmin"], ledger.Stale(GrantLedger.ServerRole, ["DBCREATOR"]));
    }

    [Fact]
    public void Merge_ReplacesOnlyThisTarget_Sorted()
    {
        var entries = new List<string> { "SqlInstance/b|srole|sysadmin", "SqlInstance/a|srole|bulkadmin" };
        var ledger = GrantLedger.For(entries, "SqlInstance/a");
        ledger.Replace(GrantLedger.ServerRole, ["dbcreator"]);
        ledger.Add(GrantLedger.DatabaseRole, GrantLedger.Join("App", "db_datareader"));

        Assert.Equal(
            ["SqlInstance/a|drole|App|db_datareader", "SqlInstance/a|srole|dbcreator", "SqlInstance/b|srole|sysadmin"],
            ledger.Merge(entries));
    }

    [Fact]
    public void Split_ReadsFromTheRight()
    {
        Assert.Equal(("we|ird", "role"), GrantLedger.Split2("we|ird|role"));
        Assert.Equal(("db", "sales", "SELECT"), GrantLedger.Split3("db|sales|SELECT"));
    }
}
