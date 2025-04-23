using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.OpenSsl;

namespace Altruist.Security;

public class CertLoader
{
    private readonly ILogger<CertLoader> _logger;
    private readonly List<ICertLoadStrategy> _strategies;

    public CertLoader(ILogger<CertLoader> logger, IEnumerable<ICertLoadStrategy> strategies)
    {
        _logger = logger;
        _strategies = strategies.ToList();
    }

    public X509Certificate2? Load()
    {
        var certDir = Environment.GetEnvironmentVariable("CERT_DIR")
                      ?? Path.Combine(AppContext.BaseDirectory, "certs");

        if (!Directory.Exists(certDir))
        {
            _logger.LogError("Certificate directory not found at path: {CertDir}", certDir);
            CertUtils.ShowCertLoadingGuide(_logger);
            return null;
        }

        foreach (var strategy in _strategies)
        {
            try
            {
                _logger.LogInformation("Trying certificate strategy: {Strategy}", strategy.Name);
                var cert = strategy.LoadCertificate(certDir);
                if (cert != null)
                {
                    _logger.LogInformation("Certificate loaded using strategy: {Strategy}", strategy.Name);
                    return cert;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load certificate using strategy {Strategy}", strategy.Name);
            }
        }

        _logger.LogError("No suitable certificate strategy found. Please check the certificate files.");
        CertUtils.ShowCertLoadingGuide(_logger);
        return null;
    }
}

public static class CertUtils
{
    public static void ShowCertLoadingGuide(ILogger logger)
    {
        logger.LogWarning("⚠️ TLS certificate not loaded.");
        logger.LogWarning("👉 Place your certs in the /certs folder.");
        logger.LogWarning("✔️ Supported formats:");
        logger.LogWarning("  - .pfx + set CERT_PFX_PASSWORD (env)");
        logger.LogWarning("  - .pem/.crt + .key (support coming soon)");

        logger.LogWarning("🔑 To convert PEM to PFX with OpenSSL:");
        logger.LogWarning("  openssl pkcs12 -export -out fullcert.pfx -inkey private.key -in cert.pem -certfile chain.pem");
    }
}
