using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Watcher.Server;

public interface ISiemTransport : IDisposable
{
    // Отправить пачку; исключение — пачка не доставлена (курсор не двигается).
    Task SendAsync(IReadOnlyList<string> messages, CancellationToken ct);

    // true — соединение оказалось закрыто сервером и сброшено: всё, что ушло
    // в него после последней проверки, могло не дойти.
    bool ResetIfClosed();
}

// RFC 5426: одно сообщение — одна датаграмма. Подтверждения нет: UDP годится,
// только если потеря части записей журнала допустима.
public sealed class UdpSiemTransport : ISiemTransport
{
    // Безопасный размер датаграммы syslog (RFC 5426 рекомендует поддерживать до 2048).
    public const int MaxDatagramBytes = 2048;
    private readonly UdpClient _udp = new();
    private readonly string _host;
    private readonly int _port;

    public UdpSiemTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task SendAsync(IReadOnlyList<string> messages, CancellationToken ct)
    {
        foreach (var m in messages)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(m);
            if (bytes.Length > MaxDatagramBytes) bytes = bytes[..MaxDatagramBytes];
            await _udp.SendAsync(bytes, _host, _port, ct);
        }
    }

    // Подтверждений в UDP нет — и проверять нечего.
    public bool ResetIfClosed() => false;

    public void Dispose() => _udp.Dispose();
}

// TCP (RFC 6587) и TLS (RFC 5425). Соединение держится между пачками и
// пересоздаётся после ошибки.
public sealed class StreamSiemTransport : ISiemTransport
{
    private readonly SiemConfig _cfg;
    private readonly X509Certificate2? _ca;
    private TcpClient? _tcp;
    private Stream? _stream;

    public StreamSiemTransport(SiemConfig cfg)
    {
        _cfg = cfg;
        if (cfg.Protocol == "tls" && !string.IsNullOrEmpty(cfg.CaCertPath))
            _ca = new X509Certificate2(cfg.CaCertPath);
    }

    public async Task SendAsync(IReadOnlyList<string> messages, CancellationToken ct)
    {
        ResetIfClosed();
        try
        {
            var stream = _stream ?? await ConnectAsync(ct);
            foreach (var m in messages)
            {
                byte[] body = Encoding.UTF8.GetBytes(m);
                byte[] frame = _cfg.Framing == "lf"
                    ? [.. body, (byte)'\n']
                    : [.. Encoding.ASCII.GetBytes(body.Length + " "), .. body];
                await stream.WriteAsync(frame, ct);
            }
            await stream.FlushAsync(ct);
        }
        catch
        {
            Reset();
            throw;
        }
    }

    // Сервер syslog закрыл соединение (перезапуск, обновление SIEM) — запись в
    // такой сокет обычно «проходит» в буфер ядра, и пачка молча теряется:
    // подтверждений в syslog по TCP нет. Найдено живой проверкой с syslog-ng.
    // Сервер по RFC 6587/5425 нам ничего не шлёт, поэтому любое «есть что
    // читать» — FIN, RST или TLS close_notify (у TLS это байты, не 0) — значит,
    // соединение кончилось.
    public bool ResetIfClosed()
    {
        if (_tcp is not { } tcp) return false;
        bool closed;
        try { closed = tcp.Client.Poll(0, SelectMode.SelectRead); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { closed = true; }
        if (closed) Reset();
        return closed;
    }

    private async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_cfg.Host, _cfg.Port, ct);
        Stream stream = _tcp.GetStream();
        if (_cfg.Protocol == "tls")
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions { TargetHost = _cfg.Host };
            if (_ca is not null) options.RemoteCertificateValidationCallback = ValidateWithCustomCa;
            await ssl.AuthenticateAsClientAsync(options, ct);
            stream = ssl;
        }
        _stream = stream;
        return stream;
    }

    // Свой CA вместо системного хранилища — но с полной проверкой: имя сервера
    // и цепочка до этого CA. Ошибки, кроме «корень не в системном хранилище»,
    // не прощаются.
    private bool ValidateWithCustomCa(object sender, X509Certificate? cert, X509Chain? _, SslPolicyErrors errors)
    {
        if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca!);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(cert));
    }

    private void Reset()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        Reset();
        _ca?.Dispose();
    }
}
