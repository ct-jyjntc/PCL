using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AlienLink;

namespace PclLink;

public static class Program
{
    public static string PipeName(string directory, string userSid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd('\\').ToLowerInvariant() + "|" + userSid));
        return "PCL.Link." + Convert.ToHexString(hash)[..24];
    }

    public static async Task Main()
    {
        using var identity = WindowsIdentity.GetCurrent();
        // One background service per Windows user, shared by every patched
        // launcher copy so they cannot race account creation or virtual adapters.
        var name = PipeName(AppSettings.DataDirectory, identity.User!.Value);
        using var mutex = new Mutex(true, @"Local\" + name, out var first);
        if (!first) return;
        using var service = new LinkService();
        await service.RunAsync(name);
    }
}

public sealed class LinkService : IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly EngineRunner _engine;
    private readonly LanDiscovery _discovery;
    private readonly AutoAccount _account;
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _discoveryGate = new();
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _operationStop;
    private string? _operationName;
    private string? _error;
    private bool _detached;
    private long _lastContact = Stopwatch.GetTimestamp();

    public LinkService()
    {
        _engine = new EngineRunner(_settings);
        _discovery = new LanDiscovery(_engine.Snapshot);
        _account = new AutoAccount(_settings, new PlatformAccountAuth());
    }

    public async Task RunAsync(string pipeName)
    {
        var monitor = MonitorAsync(_stop.Token);
        while (!_stop.IsCancellationRequested)
        {
            using var pipe = CreateUserPipe(pipeName);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                _lastContact = Stopwatch.GetTimestamp();
                var request = await ReadMessageAsync(pipe, requestTimeout.Token);
                object reply;
                try
                {
                    using var document = JsonDocument.Parse(request);
                    reply = await HandleAsync(document.RootElement);
                }
                catch (Exception ex) { reply = new { ok = false, error = ex.Message }; }
                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, _json) + "\n");
                await pipe.WriteAsync(data, requestTimeout.Token);
                await pipe.FlushAsync(requestTimeout.Token);
                _lastContact = Stopwatch.GetTimestamp();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
        _operationStop?.Cancel();
        try { await _operation; } catch { }
        try { await monitor; } catch (OperationCanceledException) { }
    }

    public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 8192)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Incomplete request.");
            if (one[0] == '\n') return Encoding.UTF8.GetString(buffer.ToArray());
            buffer.WriteByte(one[0]);
        }
        throw new IOException("Request exceeds the size limit.");
    }

    public static NamedPipeServerStream CreateUserPipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        // CurrentUserOnly also checks elevation on Windows, which would reject
        // the ordinary PCL process. Use an explicit single-user ACL instead.
        // No explicit integrity SACL means medium integrity on Windows; adding
        // an SACL here would require SeSecurityPrivilege just to create a pipe.
        security.SetSecurityDescriptorSddlForm($"O:{identity.User!.Value}D:P(A;;GA;;;{identity.User.Value})");
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task<object> HandleAsync(JsonElement request)
    {
        var command = request.GetProperty("command").GetString();
        switch (command)
        {
            case "auto":
                if (!_operation.IsCompleted) return Busy();
                var displayName = AutoAccount.CleanName(Text(request, "nickname", 32));
                Begin("准备联机", async token =>
                {
                    await _account.EnsureAsync(displayName, token);
                    _engine.UpdateNickname(_settings.Nickname);
                });
                return new { ok = true };
            case "player":
                var player = AutoAccount.CleanName(Text(request, "name", 32));
                if (player.Length == 0) return new { ok = false, error = "玩家名不能为空。" };
                _settings.Nickname = player;
                _settings.Save();
                _engine.UpdateNickname(player);
                return new { ok = true };
            case "state":
                _detached = false;
                return Status();
            case "login":
            case "register":
                if (!_operation.IsCompleted) return Busy();
                var username = Text(request, "account", 32);
                var password = Text(request, "password", 256);
                var nickname = Text(request, "nickname", 32);
                if (string.IsNullOrWhiteSpace(username) || password.Length < 6)
                    return new { ok = false, error = "请输入账号和至少 6 位的密码。" };
                Begin("正在登录", async token =>
                {
                    var result = command == "register"
                        ? await AuthClient.RegisterAsync(username, password, nickname, token)
                        : await AuthClient.LoginAsync(username, password, token);
                    if (!result.Ok) throw new IOException(result.Error);
                    _settings.Account = result.Username ?? username;
                    _settings.Token = result.Token!;
                    _settings.TokenExpiresAt = result.ExpiresAt;
                    _settings.Nickname = string.IsNullOrWhiteSpace(nickname) ? _settings.Account : nickname;
                    _settings.Save();
                    _engine.UpdateNickname(_settings.Nickname);
                });
                return new { ok = true };
            case "create":
            case "join":
                var gamePlayer = AutoAccount.CleanName(Text(request, "playerName", 32));
                if (gamePlayer.Length == 0) return new { ok = false, error = "请先登录游戏账号或填写有效的离线玩家名。" };
                if (!HostEnv.IsAdmin) return new { ok = false, error = "请允许 Windows 管理员授权后再组网。" };
                if (!_operation.IsCompleted) return Busy();
                var code = command == "join" ? Text(request, "code", 6).ToUpperInvariant() : null;
                if (command == "join" && (code is null || !System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9]{6}$")))
                    return new { ok = false, error = "网络 ID 应为 6 位字母或数字。" };
                Begin(command == "create" ? "正在创建网络" : "正在加入网络", async token =>
                {
                    await _account.EnsureAsync(gamePlayer, token);
                    _engine.UpdateNickname(_settings.Nickname);
                    var result = await _engine.StartAsync(command == "create", code, token);
                    if (!result.ok) throw new IOException(result.error);
                });
                return new { ok = true };
            case "leave":
            case "logout":
                _operationStop?.Cancel();
                await _operation;
                lock (_discoveryGate) _discovery.Dispose();
                _engine.LeaveRoom();
                if (command == "logout") _settings.Logout();
                _error = null;
                return new { ok = true };
            case "detach":
                _detached = true;
                return new { ok = true };
            case "exit":
                // Leave the pipe alive long enough to deliver the acknowledgement.
                _ = Task.Run(async () => { await Task.Delay(200); _stop.Cancel(); });
                return new { ok = true };
            default:
                return new { ok = false, error = "Unknown command." };
        }
    }

    private static string Text(JsonElement request, string key, int limit)
    {
        var text = request.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        if (text.Length > limit) throw new IOException("输入过长。");
        return text;
    }

    private static object Busy() => new { ok = false, error = "上一个操作还未完成，请稍等。" };

    private void Begin(string name, Func<CancellationToken, Task> action)
    {
        _error = null;
        _operationStop?.Dispose();
        _operationStop = new CancellationTokenSource();
        var token = _operationStop.Token;
        _operationName = name;
        _operation = Task.Run(async () =>
        {
            try { await action(token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _error = ex.Message; }
            finally { _operationName = null; }
        });
    }

    private object Status()
    {
        lock (_discoveryGate)
            return new { ok = true, discoveryVersion = 2, loggedIn = _settings.LoggedIn, account = _settings.Account,
                nickname = _settings.Nickname, admin = HostEnv.IsAdmin, busy = !_operation.IsCompleted,
                operation = _operationName, error = _error, state = _engine.Snapshot(),
                rooms = _discovery.Rooms, discoveryError = _discovery.Error };
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // If PCL crashes or is force-closed, do not leave an elevated orphan
            // running unless the user explicitly chose to keep their network.
            if (!_detached && Stopwatch.GetElapsedTime(_lastContact).TotalSeconds > 30)
            { _stop.Cancel(); break; }
            lock (_discoveryGate) _discovery.Reconcile();
            await Task.Delay(1000, token);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_discoveryGate) _discovery.Dispose();
        _engine.Dispose();
        _operationStop?.Dispose();
        _stop.Dispose();
    }
}
