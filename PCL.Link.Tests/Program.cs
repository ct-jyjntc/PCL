using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AlienLink;
using PclLink;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.AccessControl;

var passed = 0;
void Assert(bool condition, string title)
{
    if (!condition) throw new Exception("FAIL: " + title);
    Console.WriteLine("PASS: " + title); passed++;
}

Assert(LanDiscovery.TryParseAnnouncement("[MOTD]Friends' world[/MOTD][AD]54321[/AD]", out var motd, out var port)
    && motd == "Friends' world" && port == 54321, "Minecraft announcement parser");
foreach (var invalid in new[] { "[MOTD]x[/MOTD][AD]0[/AD]", "[MOTD]x[/MOTD][AD]65536[/AD]",
    "[MOTD]x[/MOTD][AD]10.0.0.1:123[/AD]", new string('x', 2048), "[MOTD]x[/MOTD][AD]123[/AD]injected" })
    Assert(!LanDiscovery.TryParseAnnouncement(invalid, out _, out _), "Rejected malformed announcement");
Assert(LanDiscovery.CleanMotd("x\n[AD]123[/AD]") == "xAD123/AD", "MOTD cannot inject protocol tags");
var settings = new AppSettings { Token = "private-token-not-for-IPC", Account = "test-account" };
Assert(!JsonSerializer.Serialize(settings).Contains(settings.Token), "Token excluded from JSON");
var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.Token), null, DataProtectionScope.CurrentUser);
Assert(Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser)) == settings.Token,
    "Current-user encrypted token round trip");
var cloneState = new EngineState { Members = [new PeerInfo { Ip = "10.0.0.2", Name = "before" }] };
var clone = cloneState.Clone(); cloneState.Members[0].Name = "after";
Assert(clone.Members[0].Name == "before", "Engine snapshots do not share mutable peers");
using (var roster = new EngineRunner(new AppSettings()))
{
    roster.State.VirtualIp = "10.50.20.2";
    var parseLine = typeof(EngineRunner).GetMethod("HandleLineCore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    parseLine.Invoke(roster, ["成员加入 m_friend (10.50.20.3)"]);
    for (var sample = 0; sample < 60; sample++)
        parseLine.Invoke(roster, ["↑0 ↓0 包 | 直连 0 / 中转 0 包 | 打洞 发0 收0 | 直连对端 0/0"]);
    Assert(roster.Snapshot().Members.Any(member => member.Ip == "10.50.20.3"),
        "Zero direct-connection statistics never erase a joined member");
    parseLine.Invoke(roster, ["成员离开 (10.50.20.3)"]);
    Assert(roster.Snapshot().Members.Count == 0 && roster.Snapshot().DepartedMembers.Contains("10.50.20.3"),
        "Explicit member departure removes the row and records departure separately");
    var departedSnapshot = roster.Snapshot();
    parseLine.Invoke(roster, ["成员加入 m_friend (10.50.20.3)"]);
    Assert(!roster.Snapshot().DepartedMembers.Contains("10.50.20.3") && departedSnapshot.DepartedMembers.Contains("10.50.20.3"),
        "Explicit rejoin clears departure without mutating earlier snapshots");
}
var accountDirectory = Path.Combine(Path.GetTempPath(), "PCL-Link-AccountTest-" + Guid.NewGuid().ToString("N"));
try
{
    var autoSettings = AppSettings.Load(accountDirectory);
    var auth = new FakeAccountAuth();
    await new AutoAccount(autoSettings, auth).EnsureAsync("WindowsUser", CancellationToken.None);
    Assert(auth.Registers == 1 && auth.Logins == 0 && autoSettings.Registered && autoSettings.LoggedIn,
        "First launch automatically creates and authenticates a unique networking account");
    Assert(autoSettings.Account.StartsWith("pcl_") && autoSettings.Account.Length <= 32 && autoSettings.Nickname == "WindowsUser",
        "Generated account fits platform limits and starts with the Windows device name");
    var accountFile = File.ReadAllText(Path.Combine(accountDirectory, "account.json"));
    Assert(!accountFile.Contains(autoSettings.AutoPassword) && !accountFile.Contains(autoSettings.Token),
        "Auto-account password and authentication token are not stored as plaintext");
    var loaded = AppSettings.Load(accountDirectory);
    Assert(loaded.LoggedIn && loaded.AutoPassword == autoSettings.AutoPassword && loaded.Registered,
        "Valid remembered session restores without a manual networking login");
    auth.Logins = 0;
    await new AutoAccount(loaded, auth).EnsureAsync("ChangedPlayer", CancellationToken.None);
    Assert(auth.Logins == 0 && loaded.Nickname == "ChangedPlayer", "Valid session survives launch and adopts the game player name");
    loaded.TokenExpiresAt = 1; loaded.Save(); auth.Logins = 0;
    await new AutoAccount(loaded, auth).EnsureAsync("ChangedPlayer", CancellationToken.None);
    Assert(auth.Logins == 1 && auth.Registers == 1, "Expired session silently logs back in with the saved account");
    // Exercise fallback with an isolated path only.
    var fallbackDir = Path.Combine(accountDirectory, "fallback");
    var fallbackSettings = AppSettings.Load(fallbackDir);
    fallbackSettings.AutoAccount = true; fallbackSettings.Account = "pcl_existing"; fallbackSettings.AutoPassword = "secret";
    fallbackSettings.Registered = false; fallbackSettings.Save();
    var retryAuth = new FakeAccountAuth { FailRegister = true, Logins = 0 };
    await new AutoAccount(AppSettings.Load(fallbackDir), retryAuth).EnsureAsync("Player", CancellationToken.None);
    Assert(retryAuth.Registers == 1 && retryAuth.Logins == 1, "Interrupted registration falls back to logging into the same saved account");
    var failDir = Path.Combine(accountDirectory, "failure");
    var failSettings = AppSettings.Load(failDir);
    failSettings.AutoAccount = true; failSettings.Account = "pcl_stable"; failSettings.AutoPassword = "secret";
    failSettings.Nickname = "Stable"; failSettings.Save();
    var failAuth = new FakeAccountAuth { FailRegister = true, FailLogin = true };
    try { await new AutoAccount(AppSettings.Load(failDir), failAuth).EnsureAsync("Player", CancellationToken.None); throw new Exception("failure expected"); }
    catch (IOException) { }
    Assert(AppSettings.Load(failDir).Account == "pcl_stable", "Network failure never silently creates a fresh account");
}
finally
{
    if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, true);
}
try
{
    await LinkService.ReadMessageAsync(new MemoryStream(new byte[9000]), CancellationToken.None);
    throw new Exception("Oversized IPC request was accepted");
}
catch (IOException) { Assert(true, "Bounded IPC request reader"); }

var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
using (var proxy = new LoopbackProxy("127.0.0.1", targetPort))
using (var client = new TcpClient())
{
    await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
    using var accepted = await target.AcceptTcpClientAsync();
    using var timeout = new CancellationTokenSource(5000);
    var payload = RandomNumberGenerator.GetBytes(200_000);
    var write = client.GetStream().WriteAsync(payload, timeout.Token);
    var received = new byte[payload.Length];
    await accepted.GetStream().ReadExactlyAsync(received, timeout.Token); await write;
    Assert(payload.SequenceEqual(received), "Proxy forwards game data to remote host");
    await accepted.GetStream().WriteAsync("reply"u8.ToArray(), timeout.Token);
    var reply = new byte[5]; await client.GetStream().ReadExactlyAsync(reply, timeout.Token);
    Assert(Encoding.UTF8.GetString(reply) == "reply", "Proxy forwards remote data back to game");
    var reservedPort = proxy.Port;
    proxy.SetEnabled(false);
    Assert(await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0, "Member departure closes existing proxy connections");
    proxy.SetEnabled(true);
    using var reconnected = new TcpClient();
    await reconnected.ConnectAsync(IPAddress.Loopback, proxy.Port);
    using var nextAccepted = await target.AcceptTcpClientAsync(timeout.Token);
    await reconnected.GetStream().WriteAsync("again"u8.ToArray(), timeout.Token);
    var again = new byte[5];
    await nextAccepted.GetStream().ReadExactlyAsync(again, timeout.Token);
    Assert(proxy.Port == reservedPort && Encoding.UTF8.GetString(again) == "again", "Suspended proxy resumes on its reserved port");
    proxy.Dispose();
    Assert(await reconnected.GetStream().ReadAsync(new byte[1], timeout.Token) == 0, "Leaving closes existing proxy connections");
}
target.Stop();

UdpClient Listener(int listenPort = 0)
{
    var udp = new UdpClient(AddressFamily.InterNetwork);
    udp.ExclusiveAddressUse = false;
    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    udp.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
    return udp;
}
using var game1 = Listener();
var announcementPort = ((IPEndPoint)game1.Client.LocalEndPoint!).Port;
using var game2 = Listener(announcementPort);
using var fakePeer = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.3"), LanDiscovery.ExchangePort));
var state = new EngineState { Running = true, RoomReady = true, VirtualIp = "127.0.0.2",
    Members = [new PeerInfo { Ip = "127.0.0.3" }] };
var discoveryClock = new TestClock();
using var discovery = new LanDiscovery(() => state.Clone(), discoveryClock, announcementPort); discovery.Reconcile();
Assert(discovery.Error is null, "Discovery listeners start without Minecraft installed");
var notice = JsonSerializer.SerializeToUtf8Bytes(new LanNotice("PCL-LAN/1", "Remote test world", targetPort));
await fakePeer.SendAsync(notice, new IPEndPoint(IPAddress.Parse("127.0.0.2"), LanDiscovery.ExchangePort));
using var discoveryTimeout = new CancellationTokenSource(6000);
async Task<UdpReceiveResult> WorldAnnouncement(UdpClient udp)
{
    while (true)
    {
        var packet = await udp.ReceiveAsync(discoveryTimeout.Token);
        if (Encoding.UTF8.GetString(packet.Buffer).Contains("Remote test world")) return packet;
    }
}
var first = await WorldAnnouncement(game1); var second = await WorldAnnouncement(game2);
Assert(first.RemoteEndPoint.Address.Equals(IPAddress.Loopback) && second.RemoteEndPoint.Address.Equals(IPAddress.Loopback),
    "LAN replay reaches multiple game listeners with a loopback source");
Assert(discovery.Rooms.Any(r => !r.IsLocal && r.Address == $"127.0.0.3:{targetPort}" && r.LocalPort > 0),
    "Known peer world receives a local connection proxy");
var stablePort = discovery.Rooms.Single(room => !room.IsLocal).LocalPort;
Assert(!discovery.Rooms.Any(r => r.IsLocal), "Replayed worlds are not re-announced as local worlds");
using (var unknown = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.4"), LanDiscovery.ExchangePort)))
{
    await unknown.SendAsync(notice, new IPEndPoint(IPAddress.Parse("127.0.0.2"), LanDiscovery.ExchangePort));
    await Task.Delay(200);
    Assert(discovery.Rooms.Length == 1, "Unknown virtual peer cannot create a game proxy");
}
state.Members.Clear(); await Task.Delay(1800);
Assert(discovery.Rooms.Length == 1 && discovery.Rooms[0].LocalPort == stablePort,
    "A transient empty peer snapshot keeps the world and its original proxy port");
for (var cycle = 0; cycle < 5; cycle++)
{
    await fakePeer.SendAsync(notice, new IPEndPoint(IPAddress.Parse("127.0.0.2"), LanDiscovery.ExchangePort));
    await Task.Delay(50);
    Assert(discovery.Rooms.Length == 1 && discovery.Rooms[0].LocalPort == stablePort,
        "Repeated peer refresh reuses exactly one world and one local port");
}
state.DepartedMembers.Add("127.0.0.3"); await Task.Delay(1800);
Assert(discovery.Rooms.Length == 0, "Explicit departure hides the world without discarding its port reservation");
using (var replay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) { EnableBroadcast = true })
{
    var echoed = Encoding.UTF8.GetBytes($"[MOTD]Remote test world{LanDiscovery.ReplaySuffix}{LanDiscovery.ReplaySuffix}[/MOTD][AD]65001[/AD]");
    await replay.SendAsync(echoed, new IPEndPoint(IPAddress.Parse("127.255.255.255"), announcementPort));
    await Task.Delay(200);
    Assert(discovery.Rooms.Length == 0 && !discovery.Rooms.Any(room => room.IsLocal),
        "Delayed or foreign replay packets never become a locally hosted world");
}
state.Members.Add(new PeerInfo { Ip = "127.0.0.3" });
state.DepartedMembers.Clear();
await fakePeer.SendAsync(notice, new IPEndPoint(IPAddress.Parse("127.0.0.2"), LanDiscovery.ExchangePort));
await Task.Delay(300);
Assert(discovery.Rooms.Length == 1 && discovery.Rooms[0].LocalPort == stablePort, "Returning peer reuses the original world proxy port");
var loopNotice = JsonSerializer.SerializeToUtf8Bytes(new LanNotice("PCL-LAN/1", "Remote test world" + LanDiscovery.ReplaySuffix, 65001));
await fakePeer.SendAsync(loopNotice, new IPEndPoint(IPAddress.Parse("127.0.0.2"), LanDiscovery.ExchangePort));
await Task.Delay(200);
Assert(discovery.Rooms.Length == 1, "A peer cannot feed a forwarded announcement back into discovery");
discoveryClock.Advance(LanDiscovery.AnnouncementLifetime + TimeSpan.FromSeconds(2));
await Task.Delay(1800);
Assert(discovery.Rooms.Length == 0, "World announcements expire after the host stops advertising");
await fakePeer.SendAsync(notice, new IPEndPoint(IPAddress.Parse("127.0.0.2"), LanDiscovery.ExchangePort));
await Task.Delay(300);
Assert(discovery.Rooms.Length == 1 && discovery.Rooms[0].LocalPort == stablePort,
    "Resumed advertisements do not add another localhost endpoint to Minecraft");
state.Members.Clear(); await Task.Delay(1800);
discoveryClock.Advance(LanDiscovery.PeerGracePeriod + TimeSpan.FromSeconds(2));
await Task.Delay(1800);
Assert(discovery.Rooms.Length == 0, "Peers missing beyond the grace period stop world replay");
state.RoomId = "a-different-network";
discovery.Reconcile();
Assert(discovery.Rooms.Length == 0, "Changing rooms with the same virtual IP resets discovery");
state.Running = false; discovery.Reconcile();
Assert(discovery.Rooms.Length == 0, "Disconnect clears discovery state");

using var identity = WindowsIdentity.GetCurrent();
using (var secured = LinkService.CreateUserPipe("PCL.Link.AclTest." + Guid.NewGuid().ToString("N")))
{
    var rules = secured.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
    Assert(rules.Count == 1 && rules[0] is PipeAccessRule rule && rule.IdentityReference.Equals(identity.User),
        "Helper pipe grants access only to the same Windows user");
}
var pipeName = "PCL.Link.Test." + Guid.NewGuid().ToString("N");
using var service = new LinkService();
var serving = service.RunAsync(pipeName);
async Task<JsonDocument> Request(string json)
{
    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(2000);
    await pipe.WriteAsync(Encoding.UTF8.GetBytes(json + "\n")); await pipe.FlushAsync();
    using var timeout = new CancellationTokenSource(3000);
    return JsonDocument.Parse(await LinkService.ReadMessageAsync(pipe, timeout.Token));
}
using (var status = await Request("{\"command\":\"state\"}"))
    Assert(status.RootElement.GetProperty("ok").GetBoolean() && !status.RootElement.TryGetProperty("token", out _),
        "Named pipe status responds without credential disclosure");
using (var invalid = await Request("{\"command\":\"unknown\"}"))
    Assert(!invalid.RootElement.GetProperty("ok").GetBoolean(), "Unknown helper command is rejected");
using (await Request("{\"command\":\"exit\"}")) { }
await serving.WaitAsync(TimeSpan.FromSeconds(4));
Assert(true, "Helper exit is acknowledged before shutdown");
if (args.Length > 0)
{
    using var assembly = AssemblyDefinition.ReadAssembly(Path.GetFullPath(args[0]));
    var types = assembly.MainModule.Types;
    var secret = types.Single(t => t.FullName == "PCL.ModSecret");
    Assert(secret.Fields.Single(f => f.Name == "RestrictMcDownload").Constant is false, "Download restriction is disabled in built EXE");
    var loader = types.Single(t => t.FullName == "PCL.PageDownloadInstall").Methods.Single(m => m.Name == "LoaderInit");
    Assert(loader.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "DlClientListLoader"),
        "Download page uses the real Minecraft version loader");
    Assert(!loader.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string text && text.Contains("关闭下载")),
        "Blocked download loader is absent from built EXE");
    var pack = types.Single(t => t.FullName == "PCL.ModModpack").Methods.Single(m => m.Name == "ModpackInstall" && m.Parameters.Count == 3);
    foreach (var name in new[] { "InstallPackCurseForge", "InstallPackHMCL", "InstallPackMMC", "InstallPackMCBBS",
        "InstallPackModrinth", "InstallPackLauncherPack", "InstallPackCompress" })
        Assert(pack.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == name), "Restored installer: " + name);
    var detail = types.Single(t => t.FullName == "PCL.PageDownloadResourceDetail");
    Assert(detail.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
        .Any(i => i.Operand is MethodReference m && m.Name == "Install_Click"), "Resource version row exposes installation");
    Assert(types.Single(t => t.FullName == "PCL.PageLinkMain").Methods.Any(m => m.Name == "RenderState"),
        "Native integrated link page is present in built EXE");
}
Console.WriteLine($"All {passed} tests passed. No external API calls or virtual adapter changes were made.");

sealed class FakeAccountAuth : IAccountAuth
{
    public int Registers, Logins;
    public bool FailRegister, FailLogin;
    public Task<AuthResult> RegisterAsync(string account, string password, string nickname, CancellationToken token)
    {
        Registers++;
        return Task.FromResult(FailRegister ? new AuthResult(false, null, 0, null, "offline") :
            new AuthResult(true, "token-register", long.MaxValue, account, null));
    }
    public Task<AuthResult> LoginAsync(string account, string password, CancellationToken token)
    {
        Logins++;
        return Task.FromResult(FailLogin ? new AuthResult(false, null, 0, null, "offline") :
            new AuthResult(true, "token-login", long.MaxValue, account, null));
    }
}

sealed class TestClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}
