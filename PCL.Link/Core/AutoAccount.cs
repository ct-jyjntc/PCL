using System.Security.Cryptography;

namespace AlienLink;

public interface IAccountAuth
{
    Task<AuthResult> LoginAsync(string account, string password, CancellationToken token);
    Task<AuthResult> RegisterAsync(string account, string password, string nickname, CancellationToken token);
}

public sealed class PlatformAccountAuth : IAccountAuth
{
    public Task<AuthResult> LoginAsync(string account, string password, CancellationToken token) => AuthClient.LoginAsync(account, password, token);
    public Task<AuthResult> RegisterAsync(string account, string password, string nickname, CancellationToken token) => AuthClient.RegisterAsync(account, password, nickname, token);
}

public sealed class AutoAccount(AppSettings settings, IAccountAuth auth)
{
    public static string CleanName(string? name) => new((name ?? "").Where(c => !char.IsControl(c)).Take(32).ToArray());

    public async Task EnsureAsync(string nickname, CancellationToken token, bool force = false)
    {
        nickname = CleanName(nickname);
        if (nickname.Length > 0) settings.Nickname = nickname;
        if (!force && settings.LoggedIn) { settings.Save(); return; }
        if (!settings.AutoAccount || settings.AutoPassword.Length == 0)
        {
            var prefix = new string(Environment.UserName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(12).ToArray());
            if (prefix.Length == 0) prefix = "device";
            settings.Account = "pcl_" + prefix + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            settings.AutoPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            settings.AutoAccount = true;
            settings.Registered = false;
            // Save generated credentials before registration so a network failure
            // or crash never creates a fresh account on every launch.
            settings.Save();
        }
        AuthResult result;
        if (!settings.Registered)
        {
            result = await auth.RegisterAsync(settings.Account, settings.AutoPassword, settings.Nickname, token);
            // Registration may have succeeded before the reply was interrupted.
            // Reuse the same account/password instead of creating another account.
            if (!result.Ok) result = await auth.LoginAsync(settings.Account, settings.AutoPassword, token);
        }
        else result = await auth.LoginAsync(settings.Account, settings.AutoPassword, token);
        if (!result.Ok || string.IsNullOrEmpty(result.Token)) throw new IOException(result.Error ?? "组网账号暂不可用，请稍后重试。");
        settings.Registered = true;
        settings.Token = result.Token;
        settings.TokenExpiresAt = result.ExpiresAt;
        settings.Account = result.Username ?? settings.Account;
        settings.Save();
    }
}
