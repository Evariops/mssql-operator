using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MsSqlOperator.Operator.Webhooks;

/// <summary>
/// The HTTPS serving certificate of the admission webhook and the aggregated API, read from
/// the mounted Secret (<c>tls.crt</c>, <c>tls.key</c>) and reloaded when the Secret changes. Kestrel
/// loaded it once at startup: after a renewal the pods kept serving the previous certificate until
/// they restarted, while the API server already trusted the new one.
/// </summary>
public sealed class ServingCertificate : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private X509Certificate2 _current;
    private byte[] _fingerprint;
    private DateTimeOffset _nextCheck;
    private readonly FileSystemWatcher? _watcher;

    /// <summary>Loads the certificate now: a missing or invalid one fails the startup, as the
    /// configuration-based certificate did.</summary>
    public ServingCertificate(string directory, TimeProvider time)
    {
        _directory = directory;
        _time = time;
        (_current, _fingerprint) = Load();
        _nextCheck = time.GetUtcNow() + CheckInterval;
        // A Secret volume is updated by swapping its ..data link — seen at once, the
        // 30 s check stays as a fallback (inotify can drop events).
        try
        {
            _watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, _) => Recheck();
            _watcher.Created += (_, _) => Recheck();
            _watcher.Renamed += (_, _) => Recheck();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _watcher = null;
        }
    }

    /// <summary>The next access re-reads the files.</summary>
    public void Recheck()
    {
        lock (_gate)
            _nextCheck = DateTimeOffset.MinValue;
    }

    public void Dispose() => _watcher?.Dispose();

    /// <summary>The certificate to present; re-read at most every 30 seconds. A Secret caught
    /// half-written keeps the previous certificate until the next check.</summary>
    public X509Certificate2 Current
    {
        get
        {
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                if (now < _nextCheck)
                    return _current;
                _nextCheck = now + CheckInterval;
                try
                {
                    var fingerprint = Fingerprint(out var certPem, out var keyPem);
                    if (!fingerprint.AsSpan().SequenceEqual(_fingerprint))
                    {
                        // The previous instance is not disposed: a handshake in flight may still use it.
                        _current = Create(certPem, keyPem);
                        _fingerprint = fingerprint;
                    }
                }
                catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
                {
                }

                return _current;
            }
        }
    }

    private (X509Certificate2, byte[]) Load()
    {
        var fingerprint = Fingerprint(out var certPem, out var keyPem);
        return (Create(certPem, keyPem), fingerprint);
    }

    private byte[] Fingerprint(out string certPem, out string keyPem)
    {
        certPem = File.ReadAllText(Path.Combine(_directory, "tls.crt"));
        keyPem = File.ReadAllText(Path.Combine(_directory, "tls.key"));
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(certPem + "\n" + keyPem));
    }

    private static X509Certificate2 Create(string certPem, string keyPem)
    {
        using var pem = X509Certificate2.CreateFromPem(certPem, keyPem);
        // A PEM-loaded key is ephemeral; the PKCS#12 round-trip gives SslStream a key it can use on
        // every platform.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }
}
