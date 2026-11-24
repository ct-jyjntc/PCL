using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AlienLink;

public sealed record AuthResult(bool Ok, string? Token, long ExpiresAt, string? Username, string? Error);

public static class AuthClient
{
    private const string BaseUrl = "https://etlan.xyz.fun";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static Task<AuthResult> LoginAsync(string username, string password, CancellationToken token = default) =>
        PostAsync("/api/v1/auth/login", new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password
        }, token);

    public static Task<AuthResult> RegisterAsync(string username, string password, string nickname, CancellationToken token = default) =>
        PostAsync("/api/v1/auth/register", new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["nickname"] = nickname
        }, token);

    private static async Task<AuthResult> PostAsync(string path, Dictionary<string, string> body, CancellationToken token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            req.Headers.UserAgent.ParseAdd("PCL-Link/0.1");

            using var resp = await Http.SendAsync(req, token);
            var text = await resp.Content.ReadAsStringAsync();
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(text); } catch { }

            using (doc)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var msg = doc is not null && doc.RootElement.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : null;
                    var friendly = msg switch
                    {
                        "用户名或密码错误" => "账号或密码不对。",
                        "用户名已存在" => "账号已存在，直接登录。",
                        null or "" => "登录失败，请稍后重试。",
                        _ => msg
                    };
                    return new AuthResult(false, null, 0, null, friendly);
                }

                if (doc is null ||
                    !doc.RootElement.TryGetProperty("token", out var tokenEl) ||
                    string.IsNullOrWhiteSpace(tokenEl.GetString()))
                    return new AuthResult(false, null, 0, null, "登录服务返回了空 token。");

                var accessToken = tokenEl.GetString()!;
                var expires = doc.RootElement.TryGetProperty("expires_at", out var exp) && exp.TryGetInt64(out var e)
                    ? (e < 10_000_000_000 ? e * 1000 : e)
                    : 0;
                var username = doc.RootElement.TryGetProperty("user", out var user) &&
                               user.TryGetProperty("username", out var un)
                    ? un.GetString()
                    : null;
                return new AuthResult(true, accessToken, expires, username, null);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            return new AuthResult(false, null, 0, null, "连不上登录服务，请检查网络后重试。");
        }
    }
}
