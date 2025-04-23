using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Altruist.Security;

public interface ICertLoadStrategy
{
    string Name { get; }
    X509Certificate2? LoadCertificate(string directory);
}


public class PfxCertStrategy : ICertLoadStrategy
{
    private readonly ILogger _logger;
    public string Name => "PFX";

    public PfxCertStrategy(ILogger logger)
    {
        _logger = logger;
    }

    public X509Certificate2? LoadCertificate(string directory)
    {
        var pfxFile = Directory.GetFiles(directory, "*.pfx").FirstOrDefault();
        if (pfxFile == null)
            throw new FileNotFoundException("No .pfx file found.");

        string? password = Environment.GetEnvironmentVariable("CERT_PFX_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("CERT_PFX_PASSWORD environment variable is not set.");

        _logger.LogInformation("Attempting to load PFX certificate from: {Path}", pfxFile);

#pragma warning disable SYSLIB0057
        return new X509Certificate2(
            pfxFile,
            password,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet
        );
#pragma warning restore SYSLIB0057
    }
}

public class PemKeyCertStrategy : ICertLoadStrategy
{
    public string Name => "PEM+KEY";
    private readonly ILogger _logger;

    public PemKeyCertStrategy(ILogger logger)
    {
        _logger = logger;
    }

    public X509Certificate2? LoadCertificate(string directory)
    {
        var certFile = Directory.GetFiles(directory, "*.crt")
                        .Concat(Directory.GetFiles(directory, "*.pem"))
                        .FirstOrDefault(f => f.Contains("cert"));
        var keyFile = Directory.GetFiles(directory, "*.key").FirstOrDefault();

        if (certFile == null)
            throw new FileNotFoundException("Certificate file (.crt or cert.pem) not found.");
        if (keyFile == null)
            throw new FileNotFoundException("Private key file (.key) not found.");

        // You could integrate BouncyCastle here to actually create the certificate.
        throw new NotImplementedException("PEM+KEY loading is not implemented. Convert to .pfx instead.");
    }
}

