namespace MsSqlOperator.Validators;

/// <summary>
/// A spec value the operator does not implement is refused at admission instead of being
/// accepted and silently ignored. The refusal ratchets: an update that leaves an already stored value
/// unchanged only warns. CRD defaults persisted some of these values in every object created before
/// they were refused, and an unrelated update must not be blocked by a value its author never wrote.
/// </summary>
internal sealed class UnimplementedFields(List<string> warnings)
{
    /// <summary>The first refusal, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Refuses <paramref name="path"/> unless <paramref name="implemented"/>; only warns when
    /// <paramref name="unchanged"/> (an update keeping the stored value).</summary>
    public void Check(string path, bool implemented, bool unchanged, string why)
    {
        if (implemented || Error is not null)
            return;
        if (unchanged)
            warnings.Add($"{path}: {why} The stored value is kept for now; remove it from the manifest.");
        else
            Error = $"{path}: {why}";
    }

    /// <summary><see cref="Check"/> for a value compared with the stored one.</summary>
    public void Check<T>(string path, T value, bool hasOld, T oldValue, Func<T, bool> implemented, string why)
        => Check(path, implemented(value), hasOld && EqualityComparer<T>.Default.Equals(value, oldValue), why);
}
