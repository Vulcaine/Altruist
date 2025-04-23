using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace Altruist.Security;

public static class CertFileNames
{
    public const string PfxFile = "server.pfx";
    public const string CertFile = "server.crt";
    public const string PemFile = "server.pem"; // fallback if .crt not found
    public const string KeyFile = "server.key";
    public const string CaBundleFile = "ca-bundle.crt";
    public const string P7bFile = "server.p7b";
}


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
        var pfxFile = Path.Combine(directory, CertFileNames.PfxFile);
        if (!File.Exists(pfxFile))
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
        var certFile = File.Exists(Path.Combine(directory, CertFileNames.CertFile))
            ? Path.Combine(directory, CertFileNames.CertFile)
            : Path.Combine(directory, CertFileNames.PemFile);
        var keyFile = Path.Combine(directory, CertFileNames.KeyFile);

        if (certFile == null)
            throw new FileNotFoundException("Certificate file (.crt or cert.pem) not found.");
        if (keyFile == null)
            throw new FileNotFoundException("Private key file (.key) not found.");

        _logger.LogInformation("Loading PEM certificate from: {Path}", certFile);
        _logger.LogInformation("Loading private key from: {Path}", keyFile);

        // Read certificate
        AsymmetricKeyParameter privateKey;
        Org.BouncyCastle.X509.X509Certificate cert;

        using (var certReader = File.OpenText(certFile))
        {
            var pemReader = new PemReader(certReader);
            cert = (Org.BouncyCastle.X509.X509Certificate)pemReader.ReadObject();
        }

        using (var keyReader = File.OpenText(keyFile))
        {
            var pemReader = new PemReader(keyReader);
            var keyObj = pemReader.ReadObject();

            if (keyObj is AsymmetricCipherKeyPair keyPair)
                privateKey = keyPair.Private;
            else if (keyObj is AsymmetricKeyParameter keyParam)
                privateKey = keyParam;
            else
                throw new InvalidOperationException("Unsupported private key format.");
        }

        var store = new Pkcs12StoreBuilder().Build();
        var certEntry = new X509CertificateEntry(cert);
        store.SetCertificateEntry("cert-alias", certEntry);
        store.SetKeyEntry("key-alias", new AsymmetricKeyEntry(privateKey), new[] { certEntry });

        using var ms = new MemoryStream();
        store.Save(ms, Array.Empty<char>(), new SecureRandom());
        return new X509Certificate2(ms.ToArray(), (string?)null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
    }
}

public class CaBundleCertStrategy : ICertLoadStrategy
{
    private readonly ILogger _logger;
    public string Name => "CA-BUNDLE";

    public CaBundleCertStrategy(ILogger logger)
    {
        _logger = logger;
    }

    public X509Certificate2? LoadCertificate(string directory)
    {
        var certFile = File.Exists(Path.Combine(directory, CertFileNames.CertFile))
            ? Path.Combine(directory, CertFileNames.CertFile)
            : Path.Combine(directory, CertFileNames.PemFile);
        var caBundleFile = Path.Combine(directory, "ca-bundle.crt");

        // Check if the files exist, if not, throw an exception
        if (!File.Exists(certFile))
            throw new FileNotFoundException("Certificate file (server.crt) not found.");

        if (!File.Exists(caBundleFile))
            throw new FileNotFoundException("CA-Bundle file (ca-bundle.crt) not found.");

        _logger.LogInformation("Loading certificate from: {Path}", certFile);
        _logger.LogInformation("Loading CA-Bundle from: {Path}", caBundleFile);

        // Load the certificate file
        var cert = new X509Certificate2(certFile);

        // Read the CA bundle file content
        var caBundle = File.ReadAllText(caBundleFile);
        var certChain = new X509Certificate2Collection(cert);

        // Parse and add CA bundle certificates to the chain
        foreach (var certLine in caBundle.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                // Handle possible base64 encoded certificates
                var caCert = new X509Certificate2(Convert.FromBase64String(certLine));
                certChain.Add(caCert);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse CA certificate: {Error}", ex.Message);
            }
        }

        // Return the certificate chain's first element (the server cert)
        return certChain.Count > 0 ? certChain[0] : null;
    }
}

public class P7bCertStrategy : ICertLoadStrategy
{
    private readonly ILogger _logger;
    public string Name => "P7B";

    public P7bCertStrategy(ILogger logger)
    {
        _logger = logger;
    }

    public X509Certificate2? LoadCertificate(string directory)
    {
        var p7bFile = Path.Combine(directory, CertFileNames.P7bFile);
        if (p7bFile == null)
            throw new FileNotFoundException("P7B certificate file (.p7b) not found.");

        _logger.LogInformation("Loading P7B certificate from: {Path}", p7bFile);

        try
        {
            // Load P7B file and extract certificates.
            var certCollection = new X509Certificate2Collection();
            certCollection.Import(p7bFile);

            // Return the first certificate in the chain (typically the server certificate)
            return certCollection.Count > 0 ? certCollection[0] : null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load P7B certificate: {ex.Message}", ex);
        }
    }
}

public class CombinedP7bAndCaBundleCertStrategy : ICertLoadStrategy
{
    private readonly ILogger _logger;
    private readonly P7bCertStrategy _p7bCertStrategy;
    private readonly CaBundleCertStrategy _caBundleCertStrategy;

    public string Name => "COMBINED-P7B-CA-BUNDLE";

    public CombinedP7bAndCaBundleCertStrategy(
        ILogger logger,
        P7bCertStrategy p7bCertStrategy,
        CaBundleCertStrategy caBundleCertStrategy)
    {
        _logger = logger;
        _p7bCertStrategy = p7bCertStrategy;
        _caBundleCertStrategy = caBundleCertStrategy;
    }

    public X509Certificate2? LoadCertificate(string directory)
    {
        var p7bCert = _p7bCertStrategy.LoadCertificate(directory);
        var certChain = new X509Certificate2Collection();

        if (p7bCert != null)
        {
            certChain.Add(p7bCert);
        }

        var caCert = _caBundleCertStrategy.LoadCertificate(directory);
        if (caCert != null)
        {
            certChain.Add(caCert);
        }

        return certChain.Count > 0 ? certChain[0] : null;
    }
}
