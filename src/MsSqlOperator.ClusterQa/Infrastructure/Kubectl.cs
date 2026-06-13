using System.Diagnostics;
using System.Text;

namespace MsSqlOperator.ClusterQa.Infrastructure;

/// <summary>
/// Thin, reliable subprocess wrapper around <c>kubectl</c>. The QA suite drives the deployed
/// operator as a black box (apply CRs, read status, exec sqlcmd, scrape probe logs, annotate for
/// failover) — exactly what an operator on a real cluster exposes. kubectl is used rather than the
/// in-process WebSocket exec API because it is far more robust and trivially reproducible by hand.
/// </summary>
public sealed class Kubectl(string context, string @namespace)
{
    public string Namespace { get; } = @namespace;

    public Task<string> ApplyAsync(string yaml, CancellationToken ct = default)
        => RunWithStdinAsync(yaml, ct, "apply", "-f", "-");

    public Task<string> ApplyFileAsync(string path, CancellationToken ct = default)
        => RunAsync(ct, "apply", "-f", path);

    public Task<string> DeleteNamespaceAsync(string ns, CancellationToken ct = default)
        => RunAsync(ct, "delete", "namespace", ns, "--ignore-not-found", "--wait=false");

    public Task<string> AnnotateAsync(string resource, string name, string keyVal, CancellationToken ct = default)
        => RunAsync(ct, "-n", Namespace, "annotate", "--overwrite", resource, name, keyVal);

    public Task<string> DeletePodAsync(string pod, bool now, CancellationToken ct = default)
        => now
            ? RunAsync(ct, "-n", Namespace, "delete", "pod", pod, "--grace-period=0", "--force")
            : RunAsync(ct, "-n", Namespace, "delete", "pod", pod);

    /// <summary>Reads a single jsonpath value from a namespaced resource (empty string if absent).</summary>
    public async Task<string> GetJsonPathAsync(string resource, string name, string jsonpath, CancellationToken ct = default)
    {
        try { return (await RunAsync(ct, "-n", Namespace, "get", resource, name, "-o", $"jsonpath={jsonpath}")).Trim(); }
        catch (KubectlException) { return ""; }
    }

    /// <summary>Runs a query via sqlcmd inside a pod and returns stdout (tab-separated, no headers).</summary>
    public Task<string> ExecSqlAsync(string pod, string saPassword, string query, string? database = null, CancellationToken ct = default)
    {
        var db = database is null ? Array.Empty<string>() : new[] { "-d", database };
        string[] sqlcmd =
        [
            "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", saPassword,
            "-C", "-b", "-h", "-1", "-W", "-s", "\t", .. db, "-Q", query,
        ];
        return RunAsync(ct, ["-n", Namespace, "exec", pod, "--", .. sqlcmd]);
    }

    /// <summary>Pipes a multi-batch T-SQL script (GO separators) to sqlcmd inside a pod via stdin.</summary>
    public Task<string> ExecSqlScriptAsync(string pod, string saPassword, string script, CancellationToken ct = default)
        => RunWithStdinAsync(script, ct,
            "-n", Namespace, "exec", "-i", pod, "--",
            "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", saPassword, "-C", "-b");

    public Task<string> LogsAsync(string selectorOrPod, int tailLines, bool bySelector = false, CancellationToken ct = default)
        => bySelector
            ? RunAsync(ct, "-n", Namespace, "logs", "-l", selectorOrPod, "--tail", tailLines.ToString())
            : RunAsync(ct, "-n", Namespace, "logs", selectorOrPod, "--tail", tailLines.ToString());

    public Task<string> RunAsync(CancellationToken ct, params string[] args) => Exec(args, null, ct);

    private Task<string> RunWithStdinAsync(string stdin, CancellationToken ct, params string[] args)
        => Exec(args, stdin, ct);

    private async Task<string> Exec(string[] args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("kubectl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        if (!string.IsNullOrEmpty(context)) psi.ArgumentList.Add($"--context={context}");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new KubectlException("kubectl failed to start");
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await outTask;
        var stderr = await errTask;
        if (p.ExitCode != 0)
            throw new KubectlException($"kubectl {string.Join(' ', args)} exited {p.ExitCode}: {stderr.Trim()}");
        return stdout;
    }
}

public sealed class KubectlException(string message) : Exception(message);
