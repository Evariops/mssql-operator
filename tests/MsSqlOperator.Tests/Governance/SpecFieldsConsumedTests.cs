using System.Reflection;
using System.Reflection.Emit;
using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Tests.Governance;

/// <summary>
/// Governance guard: every public property of a
/// <c>*Spec</c> type is a contract the CRD exposes to users. A property that no non-Entities code
/// ever reads is a DEAD field — worse, a lying one when it carries a default (e.g.
/// <c>rollingUpdate.preUpdateBackup=true</c> promised a backup that never ran).
/// <para>
/// The check resolves REAL getter calls in the operator's IL, not source tokens, so a name collision
/// with an unrelated member (the dead <c>RollingUpdateSpec.MaxUnavailable</c> vs the live
/// <c>V1PodDisruptionBudgetSpec.MaxUnavailable</c> set in a controller) cannot mask a dead field.
/// A field that is genuinely reserved for a future version must be added to <see cref="Allowlist"/>
/// with a justification; the companion test fails if an allowlisted field becomes consumed, so the
/// list can never rot.
/// </para>
/// </summary>
public class SpecFieldsConsumedTests
{
    /// <summary>
    /// Known-dead Spec properties ledger. This is a SHRINK-ONLY debt register: every entry is a real
    /// unread CRD contract, with the reason it is kept. The
    /// companion <see cref="Allowlist_HasNoStaleEntries"/> test fails the moment a listed field is
    /// implemented or removed, forcing the entry out — so the list can only get shorter. Any NEW dead
    /// field (not on this list) fails <see cref="EverySpecProperty_IsReadByNonEntitiesCode"/>.
    /// Discovered by this very guard on 2026-07-03.
    /// </summary>
    private static readonly (string Type, string Property, string Justification)[] Allowlist =
    [
        // The fields that were accepted and ignored are now refused at
        // admission (UnimplementedFields) or implemented — the validators read them, so the guard
        // covers them again. What remains is deliberately reserved.
        // ── HADR certificate import: the validator refuses source=Import and importFrom ──
        ("CertificateImportSpec", "SecretName",         "Reserved. HADR cert import path unimplemented; importFrom is refused at admission."),
        ("CertificateImportSpec", "CertificateKey",     "Reserved. HADR cert import path unimplemented; importFrom is refused at admission."),
        ("CertificateImportSpec", "PrivateKeyKey",      "Reserved. HADR cert import path unimplemented; importFrom is refused at admission."),
        ("CertificateImportSpec", "PasswordKey",        "Reserved. HADR cert import path unimplemented; importFrom is refused at admission."),
        ("NetworkPolicySpec", "OperatorNamespace",      "Deprecated ([Obsolete]): the operator namespace comes from POD_NAMESPACE; kept for compatibility."),
        ("EnvelopeEncryptionSpec", "PrivateKeySecretRef", "DEAD in operator (encrypt-only). The private key is for out-of-band restore tooling."),
    ];

    [Fact]
    public void EverySpecProperty_IsReadByNonEntitiesCode()
    {
        var asm = typeof(SqlAvailabilityGroupSpec).Assembly;
        var types = SafeGetTypes(asm);

        // Target getters: public instance property getters on *Spec types under MsSqlOperator.Entities.
        var getterToKey = new Dictionary<MethodInfo, (string Type, string Prop)>();
        foreach (var t in types.Where(IsSpecType))
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var getter = p.GetGetMethod();
                if (getter is not null)
                    getterToKey[getter] = (t.Name, p.Name);
            }

        // Consumers: every method on a NON-Entities type (controllers, services, builders, and the
        // compiler-generated closure types that carry lambda bodies — those keep their enclosing
        // namespace, so they are captured here too).
        var consumed = new HashSet<(string, string)>();
        foreach (var type in types.Where(t => !IsEntitiesType(t)))
            foreach (var method in EnumerateMethods(type))
                foreach (var called in ReferencedMethods(method))
                    if (called is MethodInfo mi && getterToKey.TryGetValue(mi, out var key))
                        consumed.Add(key);

        var allowed = Allowlist.Select(a => (a.Type, a.Property)).ToHashSet();
        var dead = getterToKey.Values
            .Where(k => !consumed.Contains(k) && !allowed.Contains(k))
            .OrderBy(k => k.Type, StringComparer.Ordinal).ThenBy(k => k.Prop, StringComparer.Ordinal)
            .Select(k => $"{k.Type}.{k.Prop}")
            .ToList();

        Assert.True(dead.Count == 0,
            "These *Spec properties are exposed in the CRD but no non-Entities code reads them — dead "
            + "fields (a lying contract). Implement their consumption, reject them at the webhook, or "
            + "remove them; add a justified Allowlist entry only for a deliberately-reserved field:\n"
            + string.Join('\n', dead));
    }

    [Fact]
    public void Allowlist_HasNoStaleEntries()
    {
        var asm = typeof(SqlAvailabilityGroupSpec).Assembly;
        var types = SafeGetTypes(asm);

        var getterToKey = new Dictionary<MethodInfo, (string Type, string Prop)>();
        foreach (var t in types.Where(IsSpecType))
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetGetMethod() is { } g) getterToKey[g] = (t.Name, p.Name);

        var consumed = new HashSet<(string, string)>();
        foreach (var type in types.Where(t => !IsEntitiesType(t)))
            foreach (var method in EnumerateMethods(type))
                foreach (var called in ReferencedMethods(method))
                    if (called is MethodInfo mi && getterToKey.TryGetValue(mi, out var key))
                        consumed.Add(key);

        var known = getterToKey.Values.ToHashSet();
        var stale = Allowlist
            .Where(a => !known.Contains((a.Type, a.Property))            // property no longer exists
                        || consumed.Contains((a.Type, a.Property)))       // property became consumed
            .Select(a => $"{a.Type}.{a.Property}")
            .ToList();

        Assert.True(stale.Count == 0,
            "Allowlist entries are stale (property removed, or now consumed — remove them so the guard "
            + "keeps meaning):\n" + string.Join('\n', stale));
    }

    private static bool IsSpecType(Type t) => IsEntitiesType(t) && t.Name.EndsWith("Spec", StringComparison.Ordinal);
    private static bool IsEntitiesType(Type t) => t.Namespace?.StartsWith("MsSqlOperator.Entities", StringComparison.Ordinal) == true;

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null).ToArray()!; }
    }

    private static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var m in type.GetMethods(flags)) yield return m;
        foreach (var c in type.GetConstructors(flags)) yield return c;
    }

    // ── Minimal, correct IL walker: resolves Call/Callvirt/Newobj/Ldftn/Ldvirtftn targets ──────
    private static readonly OpCode[] OneByte = new OpCode[256];
    private static readonly OpCode[] TwoByte = new OpCode[256];

    static SpecFieldsConsumedTests()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op) continue;
            var v = (ushort)op.Value;
            if (v < 0x100) OneByte[v] = op; else TwoByte[v & 0xff] = op;
        }
    }

    private static IEnumerable<MethodBase> ReferencedMethods(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { yield break; } // abstract/extern/unavailable
        if (il is null) yield break;

        var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var pos = 0;
        while (pos < il.Length)
        {
            OpCode op;
            if (il[pos] == 0xFE && pos + 1 < il.Length) { op = TwoByte[il[pos + 1]]; pos += 2; }
            else { op = OneByte[il[pos]]; pos += 1; }

            var isMethodCall = op.OperandType == OperandType.InlineMethod;
            if (isMethodCall && pos + 4 <= il.Length)
            {
                var token = BitConverter.ToInt32(il, pos);
                MethodBase? resolved = null;
                try { resolved = method.Module.ResolveMethod(token, typeArgs, methodArgs); }
                catch { /* unresolvable in this generic context — skip */ }
                if (resolved is not null) yield return resolved;
            }

            pos += OperandSize(op.OperandType, il, pos);
        }
    }

    private static int OperandSize(OperandType type, byte[] il, int pos) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * (pos + 4 <= il.Length ? BitConverter.ToInt32(il, pos) : 0),
        _ => 4, // InlineBrTarget/Field/I/Method/Sig/String/Tok/Type + ShortInlineR
    };
}
