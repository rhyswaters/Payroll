using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Payroll.Ros;

/// <summary>
/// Signs every outgoing request per Revenue's REST Web Service Integration Guide s.4: HTTP Signatures
/// (draft-cavage-http-signatures-08), algorithm rsa-sha512, keyId = Base64(X509 cert DER bytes),
/// signed headers "(request-target) host date" plus "digest" whenever there's a body.
/// </summary>
public sealed class RosHttpSignatureHandler : DelegatingHandler
{
    private readonly RSA _privateKey;
    private readonly string _keyId;

    public RosHttpSignatureHandler(X509Certificate2 certificate, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _privateKey = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("ROS certificate has no accessible RSA private key.");
        _keyId = Convert.ToBase64String(certificate.RawData);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("Date", DateTime.UtcNow.ToString("r"));
        request.Headers.Host = request.RequestUri!.Host;

        var signedHeaders = new List<string> { "(request-target)", "host", "date" };

        if (request.Content is not null)
        {
            var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var digest = Convert.ToBase64String(SHA512.HashData(bodyBytes));
            request.Headers.TryAddWithoutValidation("Digest", digest);
            signedHeaders.Add("digest");
        }

        var signingString = BuildSigningString(request, signedHeaders);
        var signature = _privateKey.SignData(Encoding.UTF8.GetBytes(signingString), HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);

        var headerList = string.Join(' ', signedHeaders);
        request.Headers.TryAddWithoutValidation("Signature",
            $"keyId=\"{_keyId}\",algorithm=\"rsa-sha512\",headers=\"{headerList}\",signature=\"{Convert.ToBase64String(signature)}\"");

        return await base.SendAsync(request, cancellationToken);
    }

    private static string BuildSigningString(HttpRequestMessage request, IReadOnlyList<string> signedHeaders)
    {
        var lines = signedHeaders.Select(name => name switch
        {
            "(request-target)" => $"(request-target): {request.Method.Method.ToLowerInvariant()} {request.RequestUri!.PathAndQuery}",
            "host" => $"host: {request.RequestUri!.Host}",
            _ => $"{name}: {string.Join(", ", request.Headers.TryGetValues(name, out var values) ? values : [])}"
        });
        return string.Join('\n', lines);
    }
}
