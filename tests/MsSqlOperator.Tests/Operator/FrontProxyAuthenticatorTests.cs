using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using MsSqlOperator.Operator.ViewApi;

namespace MsSqlOperator.Tests.Operator;

/// <summary>
/// The aggregated API disables TLS-layer client-cert validation, so the entire trust
/// boundary is FrontProxyAuthenticator.ChainsToCa. These tests pin its accept/reject behaviour
/// with in-memory generated certificates.
/// </summary>
public class FrontProxyAuthenticatorTests
{
    private static (X509Certificate2 ca, X509Certificate2 leaf) MakeChain(string cn = "front-proxy-client")
    {
        // Fixed window so the leaf is strictly inside the CA validity (no clock-skew between
        // two UtcNow reads).
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(10);

        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=test-requestheader-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var ca = caReq.CreateSelfSigned(notBefore, notAfter);

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest($"CN={cn}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false)); // clientAuth
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        var leaf = leafReq.Create(ca, notBefore, notAfter.AddDays(-1), serial);
        return (ca, leaf);
    }

    [Fact]
    public void ChainsToCa_TrustsLeafIssuedByConfiguredCa()
    {
        var (ca, leaf) = MakeChain();
        FrontProxyAuthenticator.ChainsToCa(leaf, [ca]).Should().BeTrue();
    }

    [Fact]
    public void ChainsToCa_RejectsLeafFromADifferentCa()
    {
        var (_, leaf) = MakeChain();
        var (otherCa, _) = MakeChain();
        // The presented cert chains to a CA the operator does not trust → rejected.
        FrontProxyAuthenticator.ChainsToCa(leaf, [otherCa]).Should().BeFalse();
    }

    [Fact]
    public void ChainsToCa_RejectsSelfSignedClient()
    {
        // A client that minted its own cert (not issued by the requestheader CA) is rejected.
        var (selfCa, _) = MakeChain();
        var (trustedCa, _) = MakeChain();
        FrontProxyAuthenticator.ChainsToCa(selfCa, [trustedCa]).Should().BeFalse();
    }
}
