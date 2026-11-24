namespace PclLink.Patching;

public sealed class PatchProfile
{
    public string Version { get; set; } = "";
    public Dictionary<string, string> Methods { get; set; } = new();
    public string[] ExternalReferences { get; set; } = [];
}

public sealed class PayloadManifest
{
    public int Schema { get; set; } = 1;
    public string PclVersion { get; set; } = "2.99.0.1";
    public Dictionary<string, string> Files { get; set; } = new();
}

public sealed class PatchMarker
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = "";
    public string OriginalHash { get; set; } = "";
    public string UiHash { get; set; } = "";
    public string Build { get; set; } = "pcl-link-v1";
    public bool Link { get; set; } = true;
    public bool Download { get; set; }
    public bool ProtectUpdates { get; set; }
    public Dictionary<string, string> ChangedMethods { get; set; } = new();
    public Dictionary<string, string> AddedMethods { get; set; } = new();
}

public sealed class PatchState
{
    public string OriginalHash { get; set; } = "";
    public string PatchedHash { get; set; } = "";
    public string UiHash { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new();
}

public sealed class CombinedOptions
{
    public bool Link { get; set; } = true;
    public bool Download { get; set; }
    public bool ProtectUpdates { get; set; }
}
public sealed record PatchItem(string Id, string Title, string State, string Detail);
public sealed record PatchInspection(string Version, bool Patched, bool Supported, string Hash, string Detail)
{
    public PatchItem[] Items { get; init; } = [];
}
public sealed record PatchResult(string Target, string Backup, string Hash, int PreservedMethods, bool Already)
{
    public PatchItem[] Items { get; init; } = [];
}
