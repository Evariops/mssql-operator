using k8s.Models;

namespace MsSqlOperator.Configuration;

/// <summary>
/// What <c>securityContext</c> / <c>podSecurityContext</c> may ask for. The operator creates the
/// pod, so an author allowed to create a SqlInstance — but not pods — used to get a privileged or root
/// container of their choosing, with the sa password Secret mounted. The rules follow the Kubernetes
/// "baseline" Pod Security Standard, plus the non-root user SQL Server runs as; <c>IPC_LOCK</c> stays
/// available for lock pages in memory, and privilege escalation stays allowed (sqlservr needs it).
/// </summary>
public static class SecurityContextPolicy
{
    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUDIT_WRITE", "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "MKNOD", "NET_BIND_SERVICE",
        "SETFCAP", "SETGID", "SETPCAP", "SETUID", "SYS_CHROOT", "IPC_LOCK",
    };

    private static readonly HashSet<string> AllowedSeLinuxTypes = new(StringComparer.Ordinal)
    {
        "container_t", "container_init_t", "container_kvm_t", "container_engine_t",
    };

    private static readonly HashSet<string> SafeSysctls = new(StringComparer.Ordinal)
    {
        "kernel.shm_rmid_forced", "net.ipv4.ip_local_port_range", "net.ipv4.ip_unprivileged_port_start",
        "net.ipv4.tcp_syncookies", "net.ipv4.ping_group_range", "net.ipv4.ip_local_reserved_ports",
        "net.ipv4.tcp_keepalive_time", "net.ipv4.tcp_fin_timeout", "net.ipv4.tcp_keepalive_intvl",
        "net.ipv4.tcp_keepalive_probes",
    };

    /// <summary>The first refused setting, or null. <paramref name="path"/> prefixes the field names
    /// (<c>spec</c>, <c>instanceTemplate</c>).</summary>
    public static string? Check(V1PodSecurityContext? pod, V1SecurityContext? container, string path)
    {
        if (container is not null)
        {
            if (container.Privileged == true)
                return $"{path}.securityContext.privileged: privileged containers are refused.";
            if (container.Capabilities?.Add?.FirstOrDefault(c => !AllowedCapabilities.Contains(c)) is { } capability)
                return $"{path}.securityContext.capabilities.add: {capability} is refused (allowed: {string.Join(", ", AllowedCapabilities.Order())}).";
            if (container.ProcMount is { } procMount && !procMount.Equals("Default", StringComparison.Ordinal))
                return $"{path}.securityContext.procMount: only Default is allowed.";
            if (Common(container.RunAsUser, container.RunAsNonRoot, container.SeccompProfile?.Type,
                    container.AppArmorProfile?.Type, container.SeLinuxOptions, container.WindowsOptions?.HostProcess) is { } error)
                return $"{path}.securityContext.{error}";
        }

        if (pod is not null)
        {
            if (Common(pod.RunAsUser, pod.RunAsNonRoot, pod.SeccompProfile?.Type,
                    pod.AppArmorProfile?.Type, pod.SeLinuxOptions, pod.WindowsOptions?.HostProcess) is { } error)
                return $"{path}.podSecurityContext.{error}";
            if (pod.Sysctls?.FirstOrDefault(s => !SafeSysctls.Contains(s.Name)) is { } sysctl)
                return $"{path}.podSecurityContext.sysctls: {sysctl.Name} is not a safe sysctl.";
        }

        return null;
    }

    private static string? Common(long? runAsUser, bool? runAsNonRoot, string? seccomp, string? appArmor,
        V1SELinuxOptions? seLinux, bool? hostProcess)
    {
        if (runAsUser == 0 || runAsNonRoot == false)
            return "runAsUser/runAsNonRoot: running as root is refused (SQL Server runs as the mssql user, 10001).";
        if (string.Equals(seccomp, "Unconfined", StringComparison.Ordinal))
            return "seccompProfile: Unconfined is refused.";
        if (string.Equals(appArmor, "Unconfined", StringComparison.Ordinal))
            return "appArmorProfile: Unconfined is refused.";
        if (seLinux is not null
            && (!string.IsNullOrEmpty(seLinux.User) || !string.IsNullOrEmpty(seLinux.Role)
                || (!string.IsNullOrEmpty(seLinux.Type) && !AllowedSeLinuxTypes.Contains(seLinux.Type))))
            return "seLinuxOptions: a custom SELinux user, role or type is refused.";
        if (hostProcess == true)
            return "windowsOptions.hostProcess is refused.";
        return null;
    }
}
