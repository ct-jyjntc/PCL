using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlienLink;

public sealed class AppSettings
{
    public string Nickname { get; set; } = "";
    public string Account { get; set; } = "";
    [JsonIgnore] public string Token { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public long TokenExpiresAt { get; set; }
    public bool AutoAccount { get; set; }
    public bool Registered { get; set; }
    [JsonIgnore] public string AutoPassword { get; set; } = "";
    public string ProtectedAutoPassword { get; set; } = "";
    [JsonIgnore] private string? _directory;
    [JsonIgnore] public int GameId => 0;
    [JsonIgnore] public int Mtu => 1200;
    [JsonIgnore] public int Prefix => 24;
    [JsonIgnore] public string Server => "https://etlan.xyz.fun";
    [JsonIgnore] public string CorePath => Path.Combine(AppContext.BaseDirectory, "kernel", "ETLanCore.exe");
    [JsonIgnore] public string WintunPath => Path.Combine(AppContext.BaseDirectory, "kernel", "wintun.dll");
    [JsonIgnore] public bool LoggedIn => Token.Length > 0 &&
        (TokenExpiresAt <= 0 || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < TokenExpiresAt);

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL", "Link");
    private string FilePath => Path.Combine(_directory ?? DataDirectory, "account.json");

    public static AppSettings Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? DataDirectory, "account.json");
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            settings._directory = directory;
            if (settings.ProtectedToken.Length > 0)
                settings.Token = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(settings.ProtectedToken), null, DataProtectionScope.CurrentUser));
            if (settings.ProtectedAutoPassword.Length > 0)
                settings.AutoPassword = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(settings.ProtectedAutoPassword), null, DataProtectionScope.CurrentUser));
            return settings;
        }
        catch { return new() { _directory = directory }; }
    }

    public void Save()
    {
        ProtectedToken = Token.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.CurrentUser));
        ProtectedAutoPassword = AutoPassword.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(AutoPassword), null, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(_directory ?? DataDirectory);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }

    public void Logout()
    {
        Token = Account = ProtectedToken = "";
        AutoPassword = ProtectedAutoPassword = "";
        AutoAccount = Registered = false;
        TokenExpiresAt = 0;
        Save();
    }
}
