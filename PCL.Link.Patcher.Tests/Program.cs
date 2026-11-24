using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PclLink.Patching;

if (args.Length is < 2 or > 3) throw new ArgumentException("Expected official EXE, payload directory and optional unpatched EXE.");
var input = Path.GetFullPath(args[0]);
var payload = Path.GetFullPath(args[1]);
var inputHash = PatchEngine.HashFile(input);
var id = Guid.NewGuid().ToString("N");
var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PCL-Link-PatchTests-" + id));
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, ".owned-test"), id);
var passed = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++; Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); } catch (IOException) { Assert(true, name); return; }
    throw new Exception("FAIL: " + name);
}
string Target(string name) => Path.Combine(root, name, "Plain Craft Launcher 2.exe");
try
{
    Assert(PatchEngine.Inspect(input) is { Supported: true, Patched: false }, "Known official version is supported");
    var target = Target("copy");
    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(target)!, ".minecraft", "saves"));
    var save = Path.Combine(Path.GetDirectoryName(target)!, ".minecraft", "saves", "keep.txt");
    File.WriteAllText(save, "test save marker");
    var result = PatchEngine.Apply(input, target, payload);
    Assert(!result.Already && File.Exists(target), "Patched copy is created without overwriting the source");
    Assert(result.PreservedMethods > 1000, "All non-link methods pass opaque preservation checks");
    Assert(PatchEngine.VerifyPreserved(input, target) == result.PreservedMethods, "Written EXE passes read-back verification");
    Assert(PatchEngine.HashFile(result.Backup) == inputHash, "Dedicated backup is byte-identical to the input");
    Assert(File.ReadAllText(save) == "test save marker", "Game/save directory is untouched");
    using (var patched = AssemblyDefinition.ReadAssembly(target))
    {
        var secret = patched.MainModule.Types.Single(t => t.FullName == "PCL.ModSecret");
        Assert(secret.Fields.Single(f => f.Name == "VersionBranchMain").Constant as string == "Official", "Official branch identity is preserved");
        Assert(secret.Fields.Single(f => f.Name == "RegFolder").Constant as string == "PCL", "Official configuration namespace is preserved");
        var ctor = patched.MainModule.Types.Single(t => t.FullName == "PCL.PageLinkMain").Methods.Single(m => m.Name == ".ctor");
        Assert(ctor.Body.Instructions.Any(i => i.Operand is MethodReference m && m.FullName.Contains("PclLink.Ui.Bridge::Attach")), "Page construction uses the native add-on");
        Assert(!ctor.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "InitializeComponent"), "Legacy EasyTier UI/loader initialization is bypassed");
    }
    var patchedHash = PatchEngine.HashFile(target);
    Assert(PatchEngine.Apply(target, target, payload).Already && PatchEngine.HashFile(target) == patchedHash, "Repeated in-place patch is a no-op");
    Assert(PatchEngine.Apply(input, target, payload).Already && PatchEngine.HashFile(target) == patchedHash, "Repeated copy creation is a no-op");
    PatchEngine.Restore(target);
    Assert(PatchEngine.HashFile(target) == inputHash, "Rollback restores exact original bytes");
    Assert(File.Exists(Path.Combine(Path.GetDirectoryName(target)!, PatchEngine.UiName)), "Rollback keeps add-on files instead of deleting user data");
    Assert(File.ReadAllText(save) == "test save marker", "Rollback does not change save files");
    var all = new CombinedOptions { Link = true, Download = true, ProtectUpdates = true };
    var combined = PatchEngine.Apply(input, target, payload, all);
    Assert(new[] { "download", "modpack", "nav", "entries", "rowclick", "silent", "updatemode", "link" }
        .All(id => combined.Items.Any(item => item.Id == id)), "Combined patch applies download, modpack, update and link options in one write");
    Assert(PatchEngine.Inspect(target, all).Items.All(item => item.State is "Already" or "Skipped"), "Combined selection is idempotent after application");
    using (var combinedAssembly = PatchEngine.ReadAssembly(target))
    {
        var secret = combinedAssembly.MainModule.Types.Single(t => t.FullName == "PCL.ModSecret");
        var update = secret.Methods.Single(m => m.Name == "UpdateStart");
        Assert(update.Body.Instructions[1].OpCode == OpCodes.Brfalse && update.Body.Instructions[2].OpCode == OpCodes.Ret,
            "Silent automatic update protection is applied");
        var loader = combinedAssembly.MainModule.Types.Single(t => t.FullName == "PCL.PageDownloadInstall")
            .Methods.Single(m => m.Name == "LoaderInit");
        Assert(loader.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "DlClientListLoader"),
            "Official download loader is restored");
        var launch = combinedAssembly.MainModule.Types.Single(t => t.FullName == "PCL.PageLaunchRight")
            .Methods.Single(m => m.Name == ".ctor");
        Assert(launch.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "AttachHome"),
            "Networking widget attaches to the official launch homepage");
        var end = combinedAssembly.MainModule.Types.Single(t => t.FullName == "PCL.FormMain").Methods.Single(m => m.Name == "EndProgram");
        Assert(end.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "BeforeExit"),
            "Close warning is coordinated with the native page");
        var hidden = combinedAssembly.MainModule.Types.Single(t => t.FullName == "PCL.PageSetupUI").Methods.Single(m => m.Name == "HiddenRefresh");
        var navigation = hidden.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "get_BtnTitleSelect2");
        Assert(navigation.Next.OpCode == OpCodes.Ldc_I4_2,
            "Official navigation visibility remains untouched and keeps the unused link tab hidden");
    }
    PatchEngine.Restore(target);
    Assert(PatchEngine.HashFile(target) == inputHash, "Combined rollback restores exact original bytes");
    PatchEngine.Apply(input, target, payload);
    Assert(PatchEngine.Inspect(target).Patched, "Patch can be reapplied after rollback");
    var linkHash = PatchEngine.HashFile(target);
    PatchEngine.Apply(target, target, payload, all);
    Assert(PatchEngine.Inspect(target, all).Patched && PatchEngine.HashFile(target) != linkHash,
        "Missing download features can be added in place without first restoring");
    Assert(File.Exists(result.Backup) && PatchEngine.HashFile(result.Backup) == inputHash,
        "Adding features in place preserves the original first-run backup");
    File.AppendAllText(target, "post-patch modification");
    var modifiedHash = PatchEngine.HashFile(target);
    Reject(() => PatchEngine.Restore(target), "Rollback refuses to overwrite later modifications");
    Assert(PatchEngine.HashFile(target) == modifiedHash, "Rejected rollback leaves the file unchanged");
    var unsupported = Path.Combine(root, "unsupported.exe");
    using (var a = PatchEngine.ReadAssembly(input)) { a.Name.Version = new Version(2, 99, 0, 2); a.Write(unsupported); }
    Assert(!PatchEngine.Inspect(unsupported).Supported, "Unknown version is rejected");
    Reject(() => PatchEngine.Apply(unsupported, Target("unsupported"), payload), "Unknown version cannot be patched");
    var altered = Path.Combine(root, "altered.exe");
    using (var a = PatchEngine.ReadAssembly(input))
    {
        var m = a.MainModule.Types.Single(t => t.FullName == "PCL.PageLinkMain").Methods.Single(m => m.Name == "TryExit");
        m.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop)); a.Write(altered);
    }
    Assert(!PatchEngine.Inspect(altered).Supported, "Changed hook fingerprints are rejected");
    Reject(() => PatchEngine.Apply(altered, Target("altered"), payload), "Altered hook cannot be force-patched");
    var collision = Target("collision");
    Directory.CreateDirectory(Path.GetDirectoryName(collision)!);
    var foreign = Path.Combine(Path.GetDirectoryName(collision)!, PatchEngine.UiName);
    File.WriteAllText(foreign, "belongs to another plugin");
    Reject(() => PatchEngine.Apply(input, collision, payload), "Conflicting existing add-on file is not overwritten");
    Assert(File.ReadAllText(foreign) == "belongs to another plugin" && !File.Exists(collision), "Rejected deployment preserves pre-existing files");
    var malicious = Path.Combine(root, "malicious-payload"); Directory.CreateDirectory(malicious);
    var manifest = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllText(Path.Combine(payload, "payload.json")))!;
    manifest.Files["../escape.dll"] = new string('A', 64);
    File.WriteAllText(Path.Combine(malicious, "payload.json"), JsonSerializer.Serialize(manifest));
    Reject(() => PatchEngine.Apply(input, Target("escape"), malicious), "Payload traversal is rejected before writing");
    Assert(PatchEngine.HashFile(input) == inputHash, "Original official EXE remains unchanged throughout testing");
    if (args.Length == 3)
    {
        var original = Path.GetFullPath(args[2]);
        var originalHash = PatchEngine.HashFile(original);
        var freshTarget = Target("from-unpatched");
        var fresh = PatchEngine.Apply(original, freshTarget, payload,
            new CombinedOptions { Link = true, Download = true, ProtectUpdates = true });
        Assert(fresh.Items.All(item => item.State is "Applied" or "Skipped"),
            "A pristine official EXE receives every selected patch in one write");
        Assert(PatchEngine.VerifyPreserved(original, freshTarget) > 1000,
            "Pristine combined output has no dangling branches and preserves unrelated methods");
        Assert(PatchEngine.HashFile(original) == originalHash,
            "Pristine unpatched input remains unchanged");
    }
    Console.WriteLine($"All {passed} official patch tests passed. No credentials were extracted and no real game data was changed.");
}
finally
{
    var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
    if (Path.GetDirectoryName(root) == temp && Path.GetFileName(root) == "PCL-Link-PatchTests-" + id &&
        File.ReadAllText(Path.Combine(root, ".owned-test")) == id)
        Directory.Delete(root, true);
}
