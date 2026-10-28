using k8s.Models;

namespace MsSqlOperator.Operator.Client;

/// <summary>The operations of a JSON Patch (RFC 6902), and the kinds the operator never PUTs.</summary>
public static class JsonPatch
{
    public static Dictionary<string, object?> Test(string path, object? value) => new() { ["op"] = "test", ["path"] = path, ["value"] = value };
    public static Dictionary<string, object?> Add(string path, object? value) => new() { ["op"] = "add", ["path"] = path, ["value"] = value };
    public static Dictionary<string, object?> Replace(string path, object? value) => new() { ["op"] = "replace", ["path"] = path, ["value"] = value };
    public static Dictionary<string, object?> Remove(string path) => new() { ["op"] = "remove", ["path"] = path };

    /// <summary>RFC 6901: ~ as ~0, / as ~1 (annotation and label keys carry a /).</summary>
    public static string Escape(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// Why a PUT of <paramref name="type"/> is refused, or null. A PUT writes back every field as the
    /// operator read it: on a CR it raced GitOps on the spec; on a Node, the kubelet and the other
    /// controllers; on a PVC, the resizer; on a PDB, a CNP or a StatefulSet, the fields defaulted or
    /// mutated by others. The operator patches them, or applies them server-side.
    /// </summary>
    public static string? NoPut(Type type)
    {
        if (type.Namespace?.StartsWith("MsSqlOperator.Entities", StringComparison.Ordinal) == true)
            return "custom resources are patched (PatchMetadataAsync, PatchStatusAsync), never PUT";
        if (type == typeof(V1Node) || type == typeof(V1PersistentVolumeClaim))
            return "patch it (PatchAsync)";
        if (type == typeof(V1PodDisruptionBudget) || type == typeof(V1StatefulSet)
            || type.Name == "GenericCiliumNetworkPolicy")
            return "apply it server-side (ApplyAsync) or patch it (PatchAsync)";
        return null;
    }
}
