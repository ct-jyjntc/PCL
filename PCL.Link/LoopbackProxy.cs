using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace PclLink;

public sealed class LoopbackProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly int _targetPort;
    private int _connections;
    private volatile bool _enabled = true;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    public string TargetIp { get; }
    public int Port { get; }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
            foreach (var client in _clients.Keys) client.Dispose();
    }

    public LoopbackProxy(string targetIp, int targetPort)
    {
        TargetIp = targetIp; _targetPort = targetPort;
        _listener.Start(16);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync(_stop.Token);
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                if (!_enabled) { client.Dispose(); continue; }
                if (Interlocked.Increment(ref _connections) > 16)
                { client.Dispose(); Interlocked.Decrement(ref _connections); continue; }
                _ = ForwardAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task ForwardAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var target = new TcpClient(AddressFamily.InterNetwork))
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            try
            {
                _clients.TryAdd(client, 0);
                _clients.TryAdd(target, 0);
                if (!_enabled) return;
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                await target.ConnectAsync(TargetIp, _targetPort, connect.Token);
                client.NoDelay = target.NoDelay = true;
                var outbound = client.GetStream().CopyToAsync(target.GetStream(), connection.Token);
                var inbound = target.GetStream().CopyToAsync(client.GetStream(), connection.Token);
                await Task.WhenAny(outbound, inbound);
                connection.Cancel();
                try { await Task.WhenAll(outbound, inbound); } catch (OperationCanceledException) { }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                _clients.TryRemove(client, out _);
                _clients.TryRemove(target, out _);
                Interlocked.Decrement(ref _connections);
            }
        }
    }

    public void Dispose() { SetEnabled(false); _stop.Cancel(); _listener.Stop(); }
}
