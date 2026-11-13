using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Watcher.Uploader;

public static class AgentTls
{
    public static HttpClientHandler PackageHandler() => new()
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true,
        ServerCertificateCustomValidationCallback = ValidateCertificate
    };

    private static bool ValidateCertificate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || chain is null) return false;
        bool publishesRevocation = chain.ChainElements.Cast<X509ChainElement>().Any(element =>
            element.Certificate.Extensions.Cast<X509Extension>().Any(extension => extension.Oid?.Value is "2.5.29.31" or "1.3.6.1.5.5.7.1.1"));
        return AllowsUnpublishedRevocation(errors, chain.ChainStatus.Select(status => status.Status), publishesRevocation);
    }

    // Внутренний Caddy CA не публикует CRL/OCSP. Доверие, срок, имя и известный
    // отзыв по-прежнему обязательны; игнорируется только отсутствие публикации.
    public static bool AllowsUnpublishedRevocation(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags> statuses, bool publishesRevocation)
    {
        const X509ChainStatusFlags missing = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        var values = statuses.ToArray();
        return errors == SslPolicyErrors.RemoteCertificateChainErrors && !publishesRevocation && values.Length > 0
            && values.Any(status => (status & missing) != 0) && values.All(status => (status & ~missing) == 0);
    }
}
