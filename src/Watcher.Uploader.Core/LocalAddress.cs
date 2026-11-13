using System.Net;
using System.Net.Sockets;

namespace Watcher.Uploader;

// IP станции для heartbeat (колонка ip_address в «Парке»).
//
// UDP-"коннект" не отправляет ни одного пакета (у UDP нет handshake) — только
// спрашивает ОС, с какого локального адреса пошёл бы маршрут к цели.
//
// R16 (code-review-2026-10.md): цель — сам сервер Watcher, а не 8.8.8.8. В
// изолированных сетях без маршрута по умолчанию маршрут к 8.8.8.8 не строится
// и IP был null, хотя до сервера станция достаёт; на станции с несколькими
// интерфейсами это ещё и адрес того интерфейса, через который она реально
// ходит на сервер. 8.8.8.8 остаётся запасным вариантом — когда сервер на
// loopback (тестовый стенд, сервер и агент на одной машине) или его имя не
// резолвится.
public static class LocalAddress
{
    private const string FallbackTarget = "8.8.8.8";

    public static async Task<string?> TowardsServerAsync(string serverUrl, CancellationToken ct)
    {
        IPAddress? local = null;
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            try
            {
                IPAddress[] targets = IPAddress.TryParse(uri.IdnHost, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(uri.IdnHost, ct);
                // IPv4 первым: так ip_address в «Парке» совпадает с тем, что админ
                // видит в DHCP/inventory; IPv6 — если IPv4 у сервера нет.
                foreach (var target in targets.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
                {
                    local = LocalFor(target, uri.Port);
                    if (local is not null) break;
                }
            }
            catch (SocketException) { }
            catch (ArgumentException) { }
        }

        if (local is null || IPAddress.IsLoopback(local))
            local = LocalFor(IPAddress.Parse(FallbackTarget), 65530) ?? local;
        return local?.ToString();
    }

    public static IPAddress? LocalFor(IPAddress target, int port)
    {
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(target, port);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
