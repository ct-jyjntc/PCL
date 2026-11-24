using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace AlienLink;

public sealed class PeerInfo
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsSelf { get; set; }
    public bool IsHost { get; set; }
    public bool Member { get; set; }
    public bool NameFromPeer { get; set; }
    public int Ping { get; set; } = -1;
    public long LastSeen { get; set; }
}

public sealed class EngineState
{
    public string Phase { get; set; } = "idle";      // idle | starting | connected | error | stopping
    public bool Running { get; set; }
    public bool Demo { get; set; }
    public bool Admin { get; set; }
    public bool RoomReady { get; set; }

    public string? RoomId { get; set; }
    public string? InviteCode { get; set; }
    public string? MemberId { get; set; }
    public string? VirtualIp { get; set; }
    public string? Subnet { get; set; }
    public string? Topology { get; set; }
    public string? Relay { get; set; }
    public string? Adapter { get; set; }
    public string? NatMap { get; set; }
    public string? NatFilter { get; set; }
    public string? Punch { get; set; }
    public string? Error { get; set; }
    public string? Nickname { get; set; }

    public bool IsHost { get; set; }
    public bool SignalOk { get; set; }
    public bool RelayOk { get; set; }
    public bool PunchOk { get; set; }
    public int Mtu { get; set; }
    public int Candidates { get; set; }

    public long PacketsUp { get; set; }
    public long PacketsDown { get; set; }
    public long DirectPackets { get; set; }
    public long RelayPackets { get; set; }
    public long PunchSent { get; set; }
    public long PunchRecv { get; set; }
    public long DirectPeers { get; set; }
    public long TotalPeers { get; set; }

    public double UpBps { get; set; }
    public double DownBps { get; set; }
    public long TotalUp { get; set; }
    public long TotalDown { get; set; }

    public List<PeerInfo> Members { get; set; } = new();
    public List<string> DepartedMembers { get; set; } = new();

    public EngineState Clone()
    {
        var copy = (EngineState)MemberwiseClone();
        copy.Members = Members.Select(p => new PeerInfo { Ip = p.Ip, Name = p.Name,
            IsSelf = p.IsSelf, IsHost = p.IsHost, Member = p.Member, NameFromPeer = p.NameFromPeer,
            Ping = p.Ping, LastSeen = p.LastSeen }).ToList();
        copy.DepartedMembers = DepartedMembers.ToList();
        return copy;
    }
}

public sealed class EngineRunner : IDisposable
{
    private readonly AppSettings _settings;
    private Process? _proc;
    private readonly object _gate = new();
    private readonly StringBuilder _tail = new();
    private readonly List<string> _logs = new();
    private readonly PeerNames _peerNames = new();
    private System.Threading.Timer? _statsTimer;
    private System.Threading.Timer? _scanTimer;
    private System.Threading.Timer? _demoTimer;
    private NetworkInterface? _nic;
    private long _lastBytesSent;
    private long _lastBytesRecv;
    private DateTime _lastSample = DateTime.UtcNow;
    private int _scanning;
    private int _generation;

    public EngineState State { get; } = new();
    public event Action? Changed;
    public event Action<string>? LogLine;

    public EngineRunner(AppSettings settings)
    {
        _settings = settings;
        State.Nickname = settings.Nickname;
        State.Admin = HostEnv.IsAdmin;
        _peerNames.PeerFound += (ip, name) =>
        {
            lock (_gate)
            {
                if (!State.Running) return;
                TouchPeer(ip, -1, name);
            }
            Changed?.Invoke();
        };
    }

    public void UpdateNickname(string? nickname)
    {
        var name = string.IsNullOrWhiteSpace(nickname) ? _settings.Account : nickname;
        if (string.IsNullOrWhiteSpace(name)) return;
        State.Nickname = name;
        _peerNames.UpdateName(name);
        Changed?.Invoke();
    }

    public bool IsRunning => _proc is { HasExited: false };

    public IReadOnlyList<string> Logs
    {
        get { lock (_gate) return _logs.ToArray(); }
    }

    public async Task<(bool ok, string? error)> StartAsync(bool create, string? code, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!HostEnv.IsAdmin)
            return (false, "Windows 还没有授权组网。");
        if (!File.Exists(_settings.CorePath))
            return (false, "内置组网内核缺失，请使用包含 Link 文件夹的完整构建。");
        if (!File.Exists(_settings.WintunPath))
            return (false, "网络适配器组件缺失，请使用包含 Link 文件夹的完整构建。");
        if (!_settings.LoggedIn)
            return (false, "登录状态失效，请重新登录。");

        FirewallHelper.AllowProgram(_settings.CorePath);
        token.ThrowIfCancellationRequested();

        if (!create && (string.IsNullOrWhiteSpace(code) || !Regex.IsMatch(code, "^[A-Za-z0-9]{6}$")))
            return (false, "网络 ID 应为 6 位字母或数字。");

        Stop();
        ResetState();
        State.Phase = "starting";
        State.Nickname = string.IsNullOrWhiteSpace(_settings.Nickname)
            ? (_settings.Account ?? Environment.UserName)
            : _settings.Nickname;
        State.Admin = true;
        AdapterBranding.ResetRenameGate();
        Changed?.Invoke();

        var runDir = Path.Combine(AppSettings.DataDirectory, "run");
        Directory.CreateDirectory(runDir);

        var psi = new ProcessStartInfo
        {
            FileName = _settings.CorePath,
            WorkingDirectory = runDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        Add(psi, "-token", _settings.Token);
        Add(psi, "-server", _settings.Server);
        Add(psi, "-game", _settings.GameId.ToString());
        Add(psi, "-mtu", _settings.Mtu.ToString());
        Add(psi, "-prefix", _settings.Prefix.ToString());
        Add(psi, "-wintun", _settings.WintunPath);
        Add(psi, "-user", State.Nickname!);
        if (!create) Add(psi, "-join", code!.ToUpperInvariant());

        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Exited += (_, _) => OnProcessExit(p);
            _proc = p;
            p.Start();
            _ = ReadStreamAsync(p.StandardOutput, p);
            _ = ReadStreamAsync(p.StandardError, p);
            AppendLog($"[alien] engine pid={p.Id} create={create} code={code ?? "-"}");
        }
        catch (Exception ex)
        {
            Stop();
            State.Phase = "error";
            State.Error = "组网服务启动失败：" + ex.Message;
            Changed?.Invoke();
            return (false, State.Error);
        }

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(200, token); }
            catch (OperationCanceledException) { LeaveRoom(); throw; }
            if (State.Running && State.RoomReady) return (true, null);
            if (_proc is { HasExited: true })
            {
                var err = State.Error ?? LastMeaningfulLine() ?? "组网服务已退出。";
                State.Phase = "error";
                State.Error = err;
                Changed?.Invoke();
                return (false, err);
            }
        }

        Stop();
        State.Phase = "error";
        State.Error = "连接超时。请检查网络或稍后重试。";
        Changed?.Invoke();
        return (false, State.Error);
    }

    public void StartDemo()
    {
        Stop();
        ResetState();
        State.Demo = true;
        State.Admin = true;
        State.Nickname = "Luna";
        State.Running = true;
        State.RoomReady = true;
        State.Phase = "connected";
        State.RoomId = "r_demo";
        State.InviteCode = "7K2M9Q";
        State.MemberId = "m_001";
        State.VirtualIp = "10.81.199.2";
        State.Subnet = "10.81.199.0/24";
        State.Topology = "star";
        State.Relay = "106.75.146.217:12100";
        State.Adapter = AdapterBranding.AppAdapterName;
        State.Mtu = 1200;
        State.IsHost = true;
        State.SignalOk = true;
        State.RelayOk = true;
        State.NatMap = "apdm";
        State.NatFilter = "apdf";
        State.Candidates = 6;
        State.Punch = "P2P 直连已打通";
        State.PunchOk = true;
        State.Members = new List<PeerInfo>
        {
            new() { Ip = "10.81.199.2", Name = "Luna", IsSelf = true, IsHost = true, Ping = 0, LastSeen = Now() },
            new() { Ip = "10.81.199.3", Name = "Kai", Ping = 18, LastSeen = Now() },
            new() { Ip = "10.81.199.4", Name = "Momo", Ping = 34, LastSeen = Now() }
        };
        State.TotalPeers = 2;
        State.DirectPeers = 2;
        State.PacketsUp = 18420;
        State.PacketsDown = 12966;
        State.DirectPackets = 30210;
        State.RelayPackets = 1176;
        State.TotalUp = 38_400_000;
        State.TotalDown = 27_100_000;
        State.UpBps = 186_000;
        State.DownBps = 124_000;
        AppendLog("[alien] 预览模式：真实内核未启动，数据为演示。");
        AppendLog("[alien] 账号 Luna 已就绪");
        AppendLog("[alien] 房间 r_demo    网络 ID 7K2M9Q");
        AppendLog("[alien] 虚拟网卡就绪：Alien Link  10.81.199.2（路由 10.81.199.0/24）MTU=1200");
        AppendLog("[alien] 协议版本 v2（载荷加密）");
        AppendLog("[alien] 信令已连接");
        AppendLog("[alien] 中转握手成功  session_id=1052054");
        _demoTimer = new System.Threading.Timer(_ => DemoTick(), null, 1000, 1000);
        Changed?.Invoke();
    }

    private void DemoTick()
    {
        if (!State.Demo) return;
        var rnd = new Random();
        State.UpBps = 120_000 + rnd.Next(0, 120_000);
        State.DownBps = 80_000 + rnd.Next(0, 90_000);
        State.TotalUp += (long)(State.UpBps / 8);
        State.TotalDown += (long)(State.DownBps / 8);
        State.PacketsUp += rnd.Next(3, 12);
        State.PacketsDown += rnd.Next(2, 9);
        foreach (var p in State.Members.Where(x => !x.IsSelf))
            p.Ping = Math.Max(8, p.Ping + rnd.Next(-3, 4));
        Changed?.Invoke();
    }

    private async Task ReadStreamAsync(StreamReader reader, Process owner)
    {
        var buf = new char[4096];
        var sb = new StringBuilder(512);
        while (true)
        {
            int n;
            try { n = await reader.ReadAsync(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            for (var i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c == '\r' || c == '\n')
                {
                    if (sb.Length > 0)
                    {
                        var line = sb.ToString();
                        sb.Clear();
                        HandleLine(line, owner);
                    }
                }
                else if (c != '\0' && sb.Length < 8192)
                {
                    sb.Append(c);
                }
            }
        }
        if (sb.Length > 0) HandleLine(sb.ToString(), owner);
    }

    public EngineState Snapshot()
    {
        lock (_gate)
        {
            return State.Clone();
        }
    }

    public void LeaveRoom()
    {
        Stop();
        ResetState();
        State.Phase = "idle";
        Changed?.Invoke();
    }

    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        lock (_gate) State.Running = false;
        _peerNames.Stop();
        _statsTimer?.Dispose(); _statsTimer = null;
        _scanTimer?.Dispose(); _scanTimer = null;
        _demoTimer?.Dispose(); _demoTimer = null;
        var p = _proc;
        _proc = null;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch { }
            try { p.Dispose(); } catch { }
        }
        AdapterBranding.RemoveKernelAdapters();
        AdapterBranding.ResetRenameGate();
        State.Demo = false;
    }

    private void ResetState()
    {
        lock (_gate) ResetStateCore();
    }

    private void ResetStateCore()
    {
        State.Phase = "idle";
        State.Running = false;
        State.RoomReady = false;
        State.RoomId = State.InviteCode = State.MemberId = State.VirtualIp = null;
        State.Subnet = State.Topology = State.Relay = State.Adapter = null;
        State.NatMap = State.NatFilter = State.Punch = State.Error = null;
        State.IsHost = State.SignalOk = State.RelayOk = State.PunchOk = false;
        State.Mtu = _settings.Mtu;
        State.Candidates = 0;
        State.PacketsUp = State.PacketsDown = State.DirectPackets = State.RelayPackets = 0;
        State.PunchSent = State.PunchRecv = State.DirectPeers = State.TotalPeers = 0;
        State.UpBps = State.DownBps = 0;
        State.TotalUp = State.TotalDown = 0;
        State.Members = new List<PeerInfo>();
        State.DepartedMembers = new List<string>();
        _nic = null;
        _lastBytesSent = _lastBytesRecv = 0;
        lock (_gate) { _tail.Clear(); }
    }

    private void OnProcessExit(Process process)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_proc, process)) return;
            OnProcessExitCore();
        }
    }

    private void OnProcessExitCore()
    {
        var p = _proc;
        if (p is null) return;
        var code = -1;
        try { code = p.ExitCode; } catch { }
        AppendLog($"[alien] engine exited code={code}");
        if (State.Phase is "starting" or "connected")
        {
            State.Running = false;
            State.Phase = "error";
            State.Error ??= LastMeaningfulLine() ?? $"组网服务已退出（代码 {code}）。";
        }
        Changed?.Invoke();
    }

    private void HandleLine(string raw, Process owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_proc, owner)) return;
            HandleLineCore(raw);
        }
    }

    private void HandleLineCore(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0) return;
        var display = Sanitize(line);
        AppendLog(display);
        LogLine?.Invoke(display);

        if (line.Contains("[wintun ERR]")) return;

        var m = Regex.Match(line, @"房间\s+(r_\S+)\s+邀请码\s+([A-Za-z0-9]{4,12})");
        if (m.Success)
        {
            State.RoomId = m.Groups[1].Value;
            State.InviteCode = m.Groups[2].Value.ToUpperInvariant();
            State.RoomReady = true;
            Changed?.Invoke();
            return;
        }

        m = Regex.Match(line, @"拓扑\s+(\S+)\s+子网\s+(\S+)");
        if (m.Success)
        {
            State.Topology = m.Groups[1].Value;
            State.Subnet = m.Groups[2].Value;
            Changed?.Invoke();
            return;
        }

        m = Regex.Match(line, @"我是\s+(\S+)\s+虚拟IP\s+(\d+\.\d+\.\d+\.\d+)\s+房主=(\w+)");
        if (m.Success)
        {
            State.MemberId = m.Groups[1].Value;
            State.VirtualIp = m.Groups[2].Value;
            State.IsHost = m.Groups[3].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            EnsureSelfPeer();
            Changed?.Invoke();
            return;
        }

        m = Regex.Match(line, @"^中转\s+([0-9A-Fa-f:.]+:\d+)");
        if (m.Success)
        {
            State.Relay = m.Groups[1].Value;
            Changed?.Invoke();
            return;
        }

        m = Regex.Match(line, @"虚拟网卡就绪：(.+?)\s+(\d+\.\d+\.\d+\.\d+).*?MTU=(\d+)");
        if (m.Success)
        {
            State.Adapter = AdapterBranding.AppAdapterName;
            State.VirtualIp = m.Groups[2].Value;
            State.Mtu = int.Parse(m.Groups[3].Value);
            State.Running = true;
            State.Phase = "connected";
            State.Error = null;
            EnsureSelfPeer();
            StartTimers();
            _ = Task.Run(AdapterBranding.RenameKernelAdapter);
            Changed?.Invoke();
            return;
        }

        m = Regex.Match(line, @"netcheck 完成：NAT 映射=(\S+)\s+过滤=(\S+)，候选\s+(\d+)\s+个");
        if (m.Success)
        {
            State.NatMap = m.Groups[1].Value;
            State.NatFilter = m.Groups[2].Value;
            State.Candidates = int.Parse(m.Groups[3].Value);
            Changed?.Invoke();
            return;
        }

        if (line.Contains("信令已连接")) { State.SignalOk = true; Changed?.Invoke(); return; }
        if (line.Contains("中转握手成功")) { State.RelayOk = true; Changed?.Invoke(); return; }
        if (Regex.IsMatch(line, @"^\s*★?\s*打洞成功"))
        {
            State.PunchOk = true;
            State.Punch = "P2P 直连已打通";
            Changed?.Invoke();
            return;
        }
        if (Regex.IsMatch(line, @"^\s*★?\s*发现局域网主机"))
        {
            AppendLog("[alien] " + line.TrimStart(' ', '★'));
            return;
        }

        m = Regex.Match(line, @"成员加入\s+(\S+)\s+\((\d+\.\d+\.\d+\.\d+)\)");
        if (m.Success)
        {
            AddMember(m.Groups[2].Value, m.Groups[1].Value);
            return;
        }

        m = Regex.Match(line, @"成员离开.*?(\d+\.\d+\.\d+\.\d+)");
        if (m.Success)
        {
            RemoveMember(m.Groups[1].Value);
            return;
        }

        m = Regex.Match(line, @"↑(\d+)\s+↓(\d+)\s+包\s+\|\s+直连\s+(\d+)\s+/\s+中转\s+(\d+)\s+包\s+\|\s+打洞\s+发(\d+)\s+收(\d+)\s+\|\s+直连对端\s+(\d+)/(\d+)");
        if (m.Success)
        {
            State.PacketsUp = long.Parse(m.Groups[1].Value);
            State.PacketsDown = long.Parse(m.Groups[2].Value);
            State.DirectPackets = long.Parse(m.Groups[3].Value);
            State.RelayPackets = long.Parse(m.Groups[4].Value);
            State.PunchSent = long.Parse(m.Groups[5].Value);
            State.PunchRecv = long.Parse(m.Groups[6].Value);
            State.DirectPeers = long.Parse(m.Groups[7].Value);
            State.TotalPeers = long.Parse(m.Groups[8].Value);
            if (State.TotalPeers > 0) State.Punch ??= "正在打洞";
            Changed?.Invoke();
            return;
        }

        if (line.StartsWith("✗") || line.Contains("建房/进房失败") || line.Contains("Wintun 不可用"))
        {
            State.Error = line.TrimStart('✗', ' ');
            if (!State.Running) State.Phase = "error";
            Changed?.Invoke();
        }
    }

    private void StartTimers()
    {
        _peerNames.Start(State.Nickname ?? "Alien Link", State.VirtualIp);
        _statsTimer ??= new System.Threading.Timer(_ => SampleAdapter(), null, 500, 1000);
        _scanTimer ??= new System.Threading.Timer(_ => _ = ScanPeersAsync(), null, 1500, 6000);
    }

    private void SampleAdapter()
    {
        lock (_gate) SampleAdapterCore();
    }

    private void SampleAdapterCore()
    {
        if (!State.Running || State.Demo) return;
        try
        {
            _nic ??= NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == State.VirtualIp));
            if (_nic is null) return;
            var st = _nic.GetIPv4Statistics();
            var now = DateTime.UtcNow;
            var secs = Math.Max(0.2, (now - _lastSample).TotalSeconds);
            if (_lastBytesSent > 0 || _lastBytesRecv > 0)
            {
                var up = (st.BytesSent - _lastBytesSent) / secs;
                var down = (st.BytesReceived - _lastBytesRecv) / secs;
                State.UpBps = State.UpBps * 0.4 + Math.Max(0, up) * 0.6;
                State.DownBps = State.DownBps * 0.4 + Math.Max(0, down) * 0.6;
            }
            _lastBytesSent = st.BytesSent;
            _lastBytesRecv = st.BytesReceived;
            State.TotalUp = st.BytesSent;
            State.TotalDown = st.BytesReceived;
            _lastSample = now;
            Changed?.Invoke();
        }
        catch { }
    }

    private async Task ScanPeersAsync()
    {
        if (!State.Running || State.Demo || string.IsNullOrEmpty(State.VirtualIp)) return;
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        var generation = _generation;
        try
        {
            var lastDot = State.VirtualIp.LastIndexOf('.');
            if (lastDot < 0) return;
            var prefix = State.VirtualIp[..(lastDot + 1)];
            using var gate = new SemaphoreSlim(32);
            var tasks = new List<Task>();
            for (var i = 1; i <= 254 && generation == _generation; i++)
            {
                var ip = prefix + i;
                if (ip == State.VirtualIp) continue;
                await gate.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var rtt = -1;
                        try
                        {
                            using var ping = new Ping();
                            var reply = await ping.SendPingAsync(ip, 300);
                            if (reply.Status == IPStatus.Success) rtt = (int)reply.RoundtripTime;
                        }
                        catch { }

                        var name = await _peerNames.QueryAsync(ip, rtt >= 0 ? 250 : 200);
                        lock (_gate)
                            if (generation == _generation && (rtt >= 0 || !string.IsNullOrWhiteSpace(name)))
                                TouchPeer(ip, rtt, name);
                    }
                    finally { gate.Release(); }
                }));
            }
            await Task.WhenAll(tasks);
            lock (_gate)
            {
                if (generation != _generation) return;
                var cutoff = Now() - 60_000;
                State.Members.RemoveAll(p => !p.IsSelf && !p.Member && p.LastSeen < cutoff);
            }
            Changed?.Invoke();
        }
        finally { Interlocked.Exchange(ref _scanning, 0); }
    }

    private void EnsureSelfPeer()
    {
        if (string.IsNullOrEmpty(State.VirtualIp)) return;
        lock (_gate)
        {
            var self = State.Members.FirstOrDefault(p => p.Ip == State.VirtualIp);
            if (self is null)
            {
                self = new PeerInfo { Ip = State.VirtualIp };
                State.Members.Insert(0, self);
            }
            self.IsSelf = true;
            self.IsHost = State.IsHost;
            self.Name = State.Nickname ?? "我";
            self.Ping = 0;
            self.LastSeen = Now();
        }
    }

    private void TouchPeer(string ip, int rtt, string? name = null)
    {
        lock (_gate)
        {
            if (State.DepartedMembers.Contains(ip)) return;
            var p = State.Members.FirstOrDefault(x => x.Ip == ip);
            if (p is null)
            {
                p = new PeerInfo { Ip = ip, Name = "设备 " + ip[(ip.LastIndexOf('.') + 1)..], IsHost = !State.IsHost && ip.EndsWith(".2") };
                State.Members.Add(p);
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                p.Name = name;
                p.NameFromPeer = true;
            }
            else if (!p.NameFromPeer && !p.Member)
            {
                p.Name = "设备 " + ip[(ip.LastIndexOf('.') + 1)..];
            }
            p.Ping = rtt;
            p.LastSeen = Now();
            State.TotalPeers = Math.Max(State.TotalPeers, State.Members.Count - 1);
        }
    }

    private void AddMember(string ip, string name)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == State.VirtualIp) return;
        lock (_gate)
        {
            State.DepartedMembers.Remove(ip);
            var p = State.Members.FirstOrDefault(x => x.Ip == ip);
            if (p is null)
            {
                p = new PeerInfo { Ip = ip, IsHost = !State.IsHost && ip.EndsWith(".2") };
                State.Members.Add(p);
            }
            p.Member = true;
            if (!p.NameFromPeer)
                p.Name = string.IsNullOrWhiteSpace(name) ? "设备 " + ip[(ip.LastIndexOf('.') + 1)..] : name;
            p.LastSeen = Now();
            State.TotalPeers = Math.Max(State.TotalPeers, State.Members.Count - 1);
        }
        Changed?.Invoke();
    }

    private void RemoveMember(string ip)
    {
        lock (_gate)
        {
            State.Members.RemoveAll(p => !p.IsSelf && p.Ip == ip);
            if (!State.DepartedMembers.Contains(ip)) State.DepartedMembers.Add(ip);
            State.TotalPeers = Math.Max(0, State.Members.Count - 1);
        }
        Changed?.Invoke();
    }

    private void AppendLog(string line)
    {
        line = Sanitize(line);
        lock (_gate)
        {
            _logs.Add(line);
            if (_logs.Count > 500) _logs.RemoveRange(0, _logs.Count - 500);
            _tail.AppendLine(line);
            if (_tail.Length > 8000) _tail.Remove(0, _tail.Length - 4000);
        }
    }

    private string Sanitize(string line) =>
        (_settings.Token.Length == 0 ? line : line.Replace(_settings.Token, "[redacted]", StringComparison.Ordinal))
            .Replace("\u5916\u661f\u4ed4\u8054\u673a", AdapterBranding.AppAdapterName, StringComparison.Ordinal)
            .Replace("\u5916\u661f\u4ed4", AdapterBranding.AppAdapterName, StringComparison.Ordinal)
            .Replace("\u661f\u5916\u4e92\u8054", AdapterBranding.AppAdapterName, StringComparison.Ordinal);

    private string? LastMeaningfulLine()
    {
        lock (_gate)
        {
            for (var i = _logs.Count - 1; i >= 0; i--)
            {
                var l = _logs[i].Trim();
                if (l.Length == 0) continue;
                if (l.Contains("diagnos") || l.StartsWith("║") || l.StartsWith("╔") || l.StartsWith("╠") || l.StartsWith("╚")) continue;
                return l;
            }
        }
        return null;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void Add(ProcessStartInfo psi, string key, string value)
    {
        psi.ArgumentList.Add(key);
        psi.ArgumentList.Add(value);
    }

    public void Dispose()
    {
        Stop();
        _statsTimer?.Dispose();
        _scanTimer?.Dispose();
        _demoTimer?.Dispose();
    }
}

