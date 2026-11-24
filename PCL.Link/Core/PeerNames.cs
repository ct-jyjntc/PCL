using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AlienLink;

/// <summary>
/// Tiny name exchange over the virtual network. The kernel CLI never reports
/// the display name of an already-present peer, so each Alien Link instance
/// answers a unicast UDP query on the tunnel. Names stay in memory only.
/// </summary>
public sealed class PeerNames : IDisposable
{
    public const int Port = 45888;
    private const string Query = "ALIENLINK?";
    private const string ReplyPrefix = "ALIENLINK!";

    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private string _nickname = "";
    private int _started;

    public event Action<string, string>? PeerFound;

    public void Start(string nickname, string? bindIp = null)
    {
        _nickname = Clean(nickname);
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            var address = IPAddress.TryParse(bindIp, out var parsed) ? parsed : IPAddress.Any;
            _listener = new UdpClient(AddressFamily.InterNetwork);
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(address, Port));
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenAsync(_cts.Token));
        }
        catch
        {
            Stop();
        }
    }

    public void UpdateName(string nickname) => _nickname = Clean(nickname);

    public async Task<string?> QueryAsync(string ip, int timeoutMs = 250)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var address)) return null;
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var data = Encoding.UTF8.GetBytes(Query);
            await udp.SendAsync(data, new IPEndPoint(address, Port));
            var receive = udp.ReceiveAsync();
            var done = await Task.WhenAny(receive, Task.Delay(timeoutMs));
            if (done != receive) return null;
            var result = await receive;
            if (!result.RemoteEndPoint.Address.Equals(address) || result.RemoteEndPoint.Port != Port || result.Buffer.Length > 256) return null;
            var text = Encoding.UTF8.GetString(result.Buffer);
            if (text.StartsWith(ReplyPrefix, StringComparison.Ordinal))
                return Clean(text[ReplyPrefix.Length..]);
        }
        catch { }
        return null;
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var udp = _listener;
        if (udp is null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(token);
                if (result.Buffer.Length > 256) continue;
                if (!IsPrivate(result.RemoteEndPoint.Address)) continue;
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text == Query)
                {
                    var reply = Encoding.UTF8.GetBytes(ReplyPrefix + _nickname);
                    await udp.SendAsync(reply, result.RemoteEndPoint, token);
                }
                else if (text.StartsWith(ReplyPrefix, StringComparison.Ordinal))
                {
                    var name = Clean(text[ReplyPrefix.Length..]);
                    if (name.Length > 0)
                        PeerFound?.Invoke(result.RemoteEndPoint.Address.ToString(), name);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _started, 0);
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        try { _listener?.Dispose(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value.Where(c => c >= ' ' && c != '\r' && c != '\n').Take(32).ToArray();
        return new string(chars).Trim();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }
}
