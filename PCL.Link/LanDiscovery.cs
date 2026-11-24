using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlienLink;

namespace PclLink;

public sealed record LanRoom(string Address, string Motd, int LocalPort, bool IsLocal);
public sealed record LanNotice(string Protocol, string Motd, int Port);

public sealed class LanDiscovery : IDisposable
{
    public const int ExchangePort = 45889;
    public const string ReplaySuffix = " · PCL 联机";
    public static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PeerGracePeriod = TimeSpan.FromSeconds(30);
    private readonly Func<EngineState> _state;
    private readonly TimeProvider _clock;
    private readonly int _announcementPort;
    private Session? _session;
    public string? Error { get; private set; }
    public LanDiscovery(Func<EngineState> state, TimeProvider? clock = null, int announcementPort = 4445)
    {
        _state = state;
        _clock = clock ?? TimeProvider.System;
        _announcementPort = announcementPort;
    }
    public LanRoom[] Rooms => _session?.Rooms ?? [];

    public void Reconcile()
    {
        var state = _state();
        var ip = state.Running && state.RoomReady ? state.VirtualIp : null;
        var network = state.RoomId ?? state.InviteCode ?? "";
        if (_session?.Ip == ip && _session?.Network == network) return;
        _session?.Dispose();
        _session = null;
        Error = null;
        if (ip is null) return;
        try { _session = new Session(ip, network, _state, _clock, _announcementPort); }
        catch (Exception ex) { Error = "自动发现暂不可用：" + ex.Message; }
    }

    public static bool TryParseAnnouncement(string text, out string motd, out int port)
    {
        motd = ""; port = 0;
        if (text.Length > 1024) return false;
        var match = Regex.Match(text, @"^\[MOTD\](.*?)\[/MOTD\]\[AD\]([0-9]{1,5})\[/AD\]$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out port) || port is < 1 or > 65535) return false;
        motd = CleanMotd(match.Groups[1].Value);
        return true;
    }

    public static string CleanMotd(string value) => new(value.Where(c => !char.IsControl(c) && c != '[' && c != ']').Take(120).ToArray());
    public static bool IsReplayedAnnouncement(string value) => value.Contains(ReplaySuffix, StringComparison.Ordinal);
    public void Dispose() { _session?.Dispose(); _session = null; }

    private sealed class Session : IDisposable
    {
        public string Ip { get; }
        public string Network { get; }
        private static readonly IPAddress Group = IPAddress.Parse("224.0.2.60");
        private readonly Func<EngineState> _state;
        private readonly TimeProvider _clock;
        private readonly int _announcementPort;
        private readonly CancellationTokenSource _stop = new();
        private readonly UdpClient _capture = new(AddressFamily.InterNetwork);
        private readonly UdpClient _exchange;
        private readonly UdpClient _replay = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly HashSet<IPAddress> _localAddresses;
        private readonly ConcurrentDictionary<int, (string Motd, DateTime Seen)> _local = new();
        private readonly ConcurrentDictionary<string, RemoteRoom> _remote = new();
        private readonly ConcurrentDictionary<string, DateTime> _knownPeers = new();
        private readonly object _remoteGate = new();

        public Session(string ip, string network, Func<EngineState> state, TimeProvider clock, int announcementPort)
        {
            Ip = ip; Network = network; _state = state; _clock = clock; _announcementPort = announcementPort;
            _exchange = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), ExchangePort));
            _localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToHashSet();
            _localAddresses.Add(IPAddress.Loopback);
            try
            {
                _replay.EnableBroadcast = true;
                _capture.ExclusiveAddressUse = false;
                _capture.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _capture.Client.Bind(new IPEndPoint(IPAddress.Any, _announcementPort));
                var joined = false;
                foreach (var address in _localAddresses)
                    try { _capture.JoinMulticastGroup(Group, address); joined = true; } catch (SocketException) { }
                if (!joined) throw new IOException("无法监听 Minecraft 局域网公告，请检查防火墙。");
                _ = CaptureAsync(_stop.Token);
                _ = ReceiveAsync(_stop.Token);
                _ = TickAsync(_stop.Token);
            }
            catch { Dispose(); throw; }
        }

        private DateTime Now => _clock.GetUtcNow().UtcDateTime;

        public LanRoom[] Rooms => _local.Select(x => new LanRoom($"{Ip}:{x.Key}", x.Value.Motd, 0, true))
            .Concat(_remote.Where(x => x.Value.Visible).Select(x => new LanRoom(x.Key, x.Value.Motd, x.Value.Proxy.Port, false))).ToArray();

        private async Task CaptureAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await _capture.ReceiveAsync(token);
                    // Do not relay physical LAN neighbours or our own replay packets.
                    if (!_localAddresses.Contains(result.RemoteEndPoint.Address) || result.Buffer.Length > 1024) continue;
                    if (result.RemoteEndPoint.Equals(_replay.Client.LocalEndPoint)) continue;
                    var announcement = Encoding.UTF8.GetString(result.Buffer);
                    if (IsReplayedAnnouncement(announcement)) continue;
                    if (!TryParseAnnouncement(announcement, out var motd, out var port)) continue;
                    if (_remote.Values.Any(r => r.Proxy.Port == port)) continue;
                    if (_local.Count < 16 || _local.ContainsKey(port)) _local[port] = (motd, Now);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private HashSet<string> Peers()
        {
            var state = _state();
            var now = Now;
            var departed = state.DepartedMembers.ToHashSet();
            foreach (var ip in departed) _knownPeers.TryRemove(ip, out _);
            foreach (var peer in state.Members)
                if (!peer.IsSelf && peer.Ip != Ip && !departed.Contains(peer.Ip) &&
                    IPAddress.TryParse(peer.Ip, out var address) && SameSubnet(IPAddress.Parse(Ip), address))
                    _knownPeers[peer.Ip] = now;
            foreach (var peer in _knownPeers.ToArray())
                if (now - peer.Value > PeerGracePeriod) _knownPeers.TryRemove(peer.Key, out _);
            return _knownPeers.Keys.ToHashSet();
        }

        private static bool SameSubnet(IPAddress a, IPAddress b)
        {
            var left = a.GetAddressBytes(); var right = b.GetAddressBytes();
            return right.Length == 4 && left.AsSpan(0, 3).SequenceEqual(right.AsSpan(0, 3));
        }

        private async Task ReceiveAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await _exchange.ReceiveAsync(token);
                    var ip = result.RemoteEndPoint.Address.ToString();
                    if (result.Buffer.Length > 1024 || result.RemoteEndPoint.Port != ExchangePort || !Peers().Contains(ip)) continue;
                    LanNotice? notice;
                    try { notice = JsonSerializer.Deserialize<LanNotice>(result.Buffer); } catch (JsonException) { continue; }
                    if (notice is not { Protocol: "PCL-LAN/1", Port: >= 1 and <= 65535 } || notice.Motd is null) continue;
                    if (IsReplayedAnnouncement(notice.Motd)) continue;
                    var key = $"{ip}:{notice.Port}";
                    lock (_remoteGate)
                    {
                        if (token.IsCancellationRequested) break;
                        if (_remote.TryGetValue(key, out var room))
                        {
                            room.Motd = CleanMotd(notice.Motd);
                            room.Seen = Now;
                            room.Visible = true;
                            room.Proxy.SetEnabled(true);
                        }
                        else if (_remote.Count < 32)
                        {
                            try { _remote[key] = new RemoteRoom(CleanMotd(notice.Motd), new LoopbackProxy(ip, notice.Port), Now); }
                            catch (SocketException) { }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task TickAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var cutoff = Now - AnnouncementLifetime;
                    var peers = Peers();
                    foreach (var entry in _local.ToArray())
                    {
                        if (entry.Value.Seen < cutoff) { _local.TryRemove(entry.Key, out _); continue; }
                        var data = JsonSerializer.SerializeToUtf8Bytes(new LanNotice("PCL-LAN/1", entry.Value.Motd, entry.Key));
                        foreach (var peer in peers)
                            try { await _exchange.SendAsync(data, new IPEndPoint(IPAddress.Parse(peer), ExchangePort), token); }
                            catch (SocketException) { }
                    }
                    foreach (var entry in _remote.ToArray())
                    {
                        var peerPresent = peers.Contains(entry.Value.Proxy.TargetIp);
                        lock (_remoteGate)
                        {
                            entry.Value.Visible = entry.Value.Seen >= cutoff && peerPresent;
                            entry.Value.Proxy.SetEnabled(peerPresent);
                        }
                        if (!entry.Value.Visible) continue;
                        // Minecraft derives the host from the UDP source address. A local
                        // TCP proxy preserves this behaviour, including offline LAN login.
                        var text = $"[MOTD]{entry.Value.Motd}{ReplaySuffix}[/MOTD][AD]{entry.Value.Proxy.Port}[/AD]";
                        // Loopback broadcast reaches every shared 4445 listener. Unicast
                        // can be consumed by our capture socket instead of Minecraft.
                        try { await _replay.SendAsync(Encoding.UTF8.GetBytes(text), new IPEndPoint(IPAddress.Parse("127.255.255.255"), _announcementPort), token); }
                        catch (SocketException) { }
                    }
                    await Task.Delay(1500, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _capture.Dispose(); _exchange.Dispose(); _replay.Dispose();
            lock (_remoteGate)
            {
                foreach (var room in _remote.Values) room.Proxy.Dispose();
                _remote.Clear();
            }
        }

        private sealed class RemoteRoom(string motd, LoopbackProxy proxy, DateTime seen)
        {
            public string Motd = motd;
            public DateTime Seen = seen;
            public LoopbackProxy Proxy = proxy;
            public volatile bool Visible = true;
        }
    }
}
