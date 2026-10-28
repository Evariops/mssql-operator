using MsSqlOperator.Core.Election;
using MsSqlOperator.Core.Timing;

namespace MsSqlOperator.Tests.Core.Simulation;

/// <summary>
/// An availability group in virtual time — replicas, links, the commit rule (a write is
/// acknowledged once hardened on k secondaries), the watchdog job of each replica, and the operator
/// applying <see cref="ElectionCore"/>. Faults are injected from a seed, and I1, I2, I10 and I11 are
/// checked at every step; a violation carries the seed that replays it.
/// </summary>
public sealed class AgSimulator
{
    public sealed class Replica(string name)
    {
        public string Name { get; } = name;
        public string Role { get; set; } = "SECONDARY";
        public bool Offline { get; set; }
        public bool Alive { get; set; } = true;
        public bool Suspended { get; set; }
        public List<int> History { get; } = [];
        public int? LostPrimaryAt { get; set; }
        public int? BelowQuorumSince { get; set; }
        public int LastWatchdogRun { get; set; }
        /// <summary>The primary this secondary is joined to (the AG's view): set by a resume.</summary>
        public string Follows { get; set; } = "r0";
        public bool AcceptsWrites(AgSimulator sim) => Alive && Role == "PRIMARY" && !Offline && sim.Synced(this).Count >= sim.K;
    }

    private readonly Random _random;
    private readonly TimingBudget _budget;
    private readonly HashSet<(string, string)> _cut = [];
    private readonly HashSet<string> _operatorCut = [];
    private ElectionState? _election;
    private int _nextWrite = 1;

    public int Seed { get; }
    public int Now { get; private set; }
    public int K { get; }
    public List<Replica> Replicas { get; }
    public string Routed { get; private set; }
    public bool OperatorPresent { get; set; } = true;
    public List<int> Acknowledged { get; } = [];
    public List<string> Violations { get; } = [];
    public int Elections { get; private set; }

    public AgSimulator(int seed, int replicas = 3, int k = 1, TimingBudget? budget = null)
    {
        Seed = seed;
        _random = new Random(seed);
        _budget = budget ?? TimingBudget.Default;
        K = k;
        Replicas = Enumerable.Range(0, replicas).Select(i => new Replica($"r{i}")).ToList();
        Replicas[0].Role = "PRIMARY";
        Routed = "r0";
    }

    // ── Faults ────────────────────────────────────────────────────────────
    public void Cut(string a, string b) { _cut.Add((a, b)); _cut.Add((b, a)); }
    public void Heal() { _cut.Clear(); _operatorCut.Clear(); }
    public void CutFromOperator(string replica) => _operatorCut.Add(replica);
    public void Isolate(string replica) { foreach (var r in Replicas.Where(r => r.Name != replica)) Cut(replica, r.Name); }
    public void Kill(string replica) => Replicas.Single(r => r.Name == replica).Alive = false;

    private bool Linked(Replica a, Replica b) => a.Alive && b.Alive && !_cut.Contains((a.Name, b.Name));
    private Replica? PrimaryOf(Replica secondary)
        => Replicas.FirstOrDefault(p => p.Name == secondary.Follows && p.Role == "PRIMARY" && !p.Offline && p != secondary);

    public List<Replica> Synced(Replica primary)
        => Replicas.Where(s => s != primary && s.Role == "SECONDARY" && !s.Suspended && Linked(primary, s)
                               && s.History.SequenceEqual(primary.History)).ToList();

    // ── One second ────────────────────────────────────────────────────────
    public void Step()
    {
        Now++;
        Replicate();
        Write();
        Watchdogs();
        if (OperatorPresent && Now % (int)_budget.StateRead.TotalSeconds == 0)
            Operate();
        Check();
    }

    private void Replicate()
    {
        foreach (var s in Replicas.Where(r => r.Alive && r.Role == "SECONDARY" && !r.Suspended))
        {
            var primary = PrimaryOf(s);
            if (primary is not null && Linked(primary, s) && primary.History.Take(s.History.Count).SequenceEqual(s.History))
                s.History.AddRange(primary.History.Skip(s.History.Count));
        }
    }

    private void Write()
    {
        // The client writes to the routed primary. The write is acknowledged once hardened on K secondaries.
        var target = Replicas.Single(r => r.Name == Routed);
        if (!target.Alive || target.Role != "PRIMARY" || target.Offline)
            return;
        var id = _nextWrite++;
        target.History.Add(id);
        var hardened = Replicas.Where(s => s != target && s.Role == "SECONDARY" && !s.Suspended && Linked(target, s)
                                           && s.History.SequenceEqual(target.History.Take(target.History.Count - 1))).ToList();
        foreach (var s in hardened) s.History.Add(id);
        if (hardened.Count >= K)
        {
            Acknowledged.Add(id);
            // I10: once a new primary acknowledges, no other primary may still serve its stale history.
            var stale = Replicas.Where(r => r != target && r.Alive && r.Role == "PRIMARY" && !r.Offline).ToList();
            if (stale.Count > 0)
                Violations.Add($"t={Now} I10: {target.Name} acknowledged while {string.Join(",", stale.Select(r => r.Name))} still serves (seed {Seed})");
        }
    }

    private void Watchdogs()
    {
        var j = (int)_budget.WatchdogPeriod.TotalSeconds;
        foreach (var r in Replicas.Where(r => r.Alive && Now % j == 0))
        {
            r.LastWatchdogRun = Now;
            if (r.Role == "PRIMARY" && !r.Offline)
            {
                if (Synced(r).Count < K) r.BelowQuorumSince ??= Now;
                else r.BelowQuorumSince = null;
                if (r.BelowQuorumSince is { } since && Now - since >= _budget.StepDown.TotalSeconds)
                    r.Offline = true; // step-down: no quorum for T_step
            }
            if (r.Role == "SECONDARY")
            {
                var primary = PrimaryOf(r);
                // S: the cut is seen after SESSION_TIMEOUT; the watchdog records the first observation.
                if (primary is null || !Linked(primary, r)) r.LostPrimaryAt ??= Now + (int)_budget.SessionTimeout.TotalSeconds;
                else r.LostPrimaryAt = null;
            }
        }
    }

    private void Operate()
    {
        var obs = new AgObservation("ag", Elections + 1, K,
            Replicas.Select(Observe).ToList(), Routed, Routed, DateTimeOffset.UnixEpoch.AddSeconds(Now));
        var decision = ElectionCore.Decide(_election, obs, _budget);
        _election = decision.State;
        foreach (var command in decision.Commands)
            Apply(command);
    }

    private ReplicaObservation Observe(Replica r)
    {
        var reachable = r.Alive && !_operatorCut.Contains(r.Name);
        if (!reachable)
            return new(r.Name, false, WatchdogAlive: Now - r.LastWatchdogRun <= 3 * _budget.WatchdogPeriod.TotalSeconds && r.Alive,
                DeathProof: !r.Alive);
        var primary = PrimaryOf(r);
        var lost = r.LostPrimaryAt is { } at && Now >= at ? TimeSpan.FromSeconds(Now - at) : (TimeSpan?)null;
        return new(r.Name, true, r.Role, r.Offline,
            SeesPrimaryConnected: r.Role == "SECONDARY" && primary is not null && Linked(primary, r) && lost is null,
            PrimaryDisconnectedFor: lost,
            SynchronizedSecondaries: r.Role == "PRIMARY" ? Synced(r).Count : 0,
            Suspended: r.Suspended,
            HardenedLsn: new Dictionary<string, decimal> { ["db"] = r.History.Count },
            WatchdogAlive: true);
    }

    private void Apply(Command command)
    {
        Replica R(string n) => Replicas.Single(r => r.Name == n);
        switch (command)
        {
            case MsSqlOperator.Core.Election.Isolate i when R(i.Replica) is var s && s.Alive && !_operatorCut.Contains(s.Name):
                var primary = PrimaryOf(s);
                if (primary is null || !Linked(primary, s)) s.Suspended = true; // refused while the primary is seen
                break;
            case Promote p when R(p.Replica) is var t && t.Alive:
                Elections++;
                t.Role = "PRIMARY";
                t.Suspended = false;
                t.Offline = false;
                t.BelowQuorumSince = null;
                // The old primary learns of it only when it can reach the new one; until then it
                // keeps its role — offline if its watchdog stepped it down, serving otherwise.
                foreach (var other in Replicas.Where(o => o != t && o.Role == "PRIMARY" && Linked(o, t)))
                    other.Role = "RESOLVING";
                break;
            case Resume r when R(r.Replica) is var s && s.Alive:
                s.Suspended = false;
                var newPrimary = Replicas.FirstOrDefault(x => x.Name == Routed && x.Role == "PRIMARY" && !x.Offline);
                if (newPrimary is not null)
                    s.Follows = newPrimary.Name;
                if (newPrimary is not null && !newPrimary.History.Take(s.History.Count).SequenceEqual(s.History))
                    s.History.RemoveRange(0, s.History.Count); // re-seeded: never more than the primary
                break;
            case TakeOffline o:
                R(o.Replica).Offline = true;
                break;
            case Route route:
                Routed = route.Replica;
                break;
        }
    }

    /// <summary>I1: at most one replica accepts writes. I2: every acknowledged write is on the routed
    /// primary while it serves. I10: the routed primary never serves a history missing an acknowledged write.</summary>
    private void Check()
    {
        var writers = Replicas.Where(r => r.AcceptsWrites(this)).ToList();
        if (writers.Count > 1)
            Violations.Add($"t={Now} I1: {string.Join(",", writers.Select(w => w.Name))} accept writes (seed {Seed})");
        var routed = Replicas.Single(r => r.Name == Routed);
        if (routed is { Alive: true, Role: "PRIMARY", Offline: false })
        {
            var missing = Acknowledged.Except(routed.History).ToList();
            if (missing.Count > 0)
                Violations.Add($"t={Now} I2/I10: routed primary {routed.Name} misses acknowledged writes {string.Join(",", missing.Take(5))} (seed {Seed})");
        }
    }

    public void Run(int seconds) { for (var i = 0; i < seconds; i++) Step(); }

    /// <summary>A random fault scenario: some seconds healthy, then one fault among the classes.</summary>
    public string RandomScenario()
    {
        Run(20 + _random.Next(20));
        var primary = Routed;
        var fault = _random.Next(5);
        switch (fault)
        {
            case 0: Isolate(primary); Run(120 + _random.Next(60)); Heal(); Run(60); return "primary isolated";
            case 1: Kill(primary); Run(120); return "primary killed";
            case 2: Isolate(primary); Run(5 + _random.Next((int)_budget.StepDown.TotalSeconds - 6)); Heal(); Run(60); return "short partition";
            case 3: OperatorPresent = false; Run(300); OperatorPresent = true; Run(60); return "operator absent";
            default: CutFromOperator(primary); Run(180); Heal(); Run(30); return "operator cut from the primary";
        }
    }
}
