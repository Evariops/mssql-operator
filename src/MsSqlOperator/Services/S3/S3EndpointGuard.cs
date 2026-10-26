using System.Net;
using System.Net.Sockets;

namespace MsSqlOperator.Services.S3;

/// <summary>
/// The S3 endpoint comes from the CR. Connections to loopback, link-local (the cloud metadata
/// services: 169.254.169.254, fd00:ec2::254) and unspecified addresses are refused at connect time —
/// after DNS resolution, so a name that resolves there is refused too. In-cluster object stores
/// (private addresses) stay reachable.
/// </summary>
public static class S3EndpointGuard
{
    private static readonly IPAddress AwsMetadataV6 = IPAddress.Parse("fd00:ec2::254");

    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }
        return address.IsIPv6LinkLocal || address.Equals(AwsMetadataV6);
    }

    /// <summary><see cref="SocketsHttpHandler.ConnectCallback"/> connecting only to allowed addresses.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = (await Dns.GetHostAddressesAsync(host, ct)).Where(a => !IsForbidden(a)).ToArray();
        if (addresses.Length == 0)
            throw new HttpRequestException($"S3 endpoint '{host}' resolves only to forbidden addresses (loopback, link-local, metadata service).");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
