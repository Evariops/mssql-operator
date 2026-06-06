namespace MsSqlOperator.Services.SqlServer;

/// <summary>
/// Derives the read-routing labels for an AG replica, shared by the controller's steady-state
/// labelling and the failover orchestrator's write-path fencing so both agree on what makes a
/// replica a valid read target.
/// </summary>
public static class AgReadRouting
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// A secondary is <c>readable</c> when connected and its <c>ALLOW_CONNECTIONS</c> is not
    /// <c>NO</c>; it is <c>read-intent-ready</c> only when also synchronized. The primary is the
    /// writer; everything else is <c>none</c>.
    /// </summary>
    public static (bool Readable, bool ReadIntentReady, string ListenerReady) Compute(AgReplicaStateInfo r, bool isPrimary)
    {
        if (isPrimary)
            return (false, false, "writer");

        var connected = r.ConnectionState.Equals("CONNECTED", Ci);
        var allowsRead = !r.AllowConnections.Equals("NO", Ci) && !r.AllowConnections.Equals("UNKNOWN", Ci);
        var synchronized = r.SynchronizationHealth.Equals("HEALTHY", Ci);

        var readable = connected && allowsRead;
        var readIntentReady = readable && synchronized;
        return (readable, readIntentReady, readable ? "reader" : "none");
    }
}
