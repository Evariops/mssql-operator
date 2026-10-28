using System.Reflection;
using Microsoft.Data.SqlClient;

namespace MsSqlOperator.Tests.Fakes;

/// <summary>Builds a <see cref="SqlException"/> carrying the given error numbers. SqlClient exposes no
/// constructor for it, so this goes through its internal factory — the only way to test a
/// classification by number without a server.</summary>
public static class SqlExceptions
{
    public static SqlException WithNumbers(params int[] numbers)
    {
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var number in numbers)
            add.Invoke(collection, [NewError(number)]);
        var create = typeof(SqlException).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateException" && m.GetParameters() is [{ ParameterType: var c }, { ParameterType: var v }]
                        && c == typeof(SqlErrorCollection) && v == typeof(string));
        return (SqlException)create.Invoke(null, [collection, "16.0.0"])!;
    }

    private static SqlError NewError(int number)
    {
        var ctor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .First(c => c.GetParameters().FirstOrDefault()?.ParameterType == typeof(int));
        var args = ctor.GetParameters().Select((p, i) => i == 0 ? number : p.ParameterType switch
        {
            var t when t == typeof(byte) => (object)(byte)16,
            var t when t == typeof(string) => $"error {number}",
            var t when t == typeof(int) => 0,
            var t when t == typeof(uint) => 0u,
            _ => p.HasDefaultValue ? p.DefaultValue : null,
        }).ToArray();
        return (SqlError)ctor.Invoke(args);
    }
}
