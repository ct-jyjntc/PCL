using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclLink.Patching;

public static class PatchEngine
{
    public const string MarkerName = "PCL.Link.Patch.Marker.v1.json";
    public const string UiName = "PCL.Link.UI.dll";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string HashFile(string path) => Hash(File.ReadAllBytes(path));
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public static AssemblyDefinition ReadAssembly(string path) => AssemblyDefinition.ReadAssembly(
        new MemoryStream(File.ReadAllBytes(path)), new ReaderParameters {
            InMemory = true, ReadSymbols = false, AssemblyResolver = new EmbeddedAssemblyResolver(path) });
    private static AssemblyDefinition Open(string path) => ReadAssembly(path);
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots) =>
        roots.SelectMany(t => new[] { t }.Concat(Types(t.NestedTypes)));
    private static TypeDefinition Type(AssemblyDefinition a, string name) =>
        a.MainModule.Types.SingleOrDefault(t => t.FullName == name) ?? throw new IOException("缺少接口：" + name);
    private static MethodDefinition Method(TypeDefinition type, string name, int parameters) =>
        type.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Count == parameters)
        ?? throw new IOException($"缺少接口：{type.Name}.{name}/{parameters}");

    public static string MethodHash(MethodDefinition method)
    {
        if (!method.HasBody) return Hash(Encoding.UTF8.GetBytes(method.FullName + "|no-body"));
        var body = method.Body;
        var indexes = body.Instructions.Select((i, n) => (i, n)).ToDictionary(x => x.i, x => x.n);
        string Index(Instruction? i)
        {
            if (i is null) return "end";
            return indexes.TryGetValue(i, out var index) ? index.ToString()
                : throw new IOException("方法包含悬空 IL 分支：" + method.FullName);
        }
        string Operand(object? value) => value switch
        {
            null => "",
            Instruction i => Index(i),
            Instruction[] targets => string.Join(",", targets.Select(Index)),
            MethodReference m => m.FullName,
            FieldReference f => f.FullName,
            TypeReference t => t.FullName,
            VariableDefinition v => v.Index + ":" + v.VariableType.FullName,
            ParameterDefinition p => p.Index + ":" + p.ParameterType.FullName,
            string text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
        };
        var text = new StringBuilder(method.FullName).Append('|').Append(body.InitLocals).Append('|');
        foreach (var variable in body.Variables) text.Append(variable.VariableType.FullName).Append(';');
        foreach (var i in body.Instructions) text.Append(i.OpCode.Name).Append(':').Append(Operand(i.Operand)).Append(';');
        foreach (var h in body.ExceptionHandlers)
            text.Append(h.HandlerType).Append(':').Append(h.CatchType?.FullName).Append(':')
                .Append(Index(h.TryStart)).Append(':').Append(Index(h.TryEnd)).Append(':')
                .Append(Index(h.HandlerStart)).Append(':').Append(Index(h.HandlerEnd)).Append(':').Append(Index(h.FilterStart));
        // Only opaque digests are exposed. Credential-bearing methods/resources
        // are never decompiled, exported, logged, or changed by this patch.
        return Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static Dictionary<string, MethodDefinition> ChangedMethods(AssemblyDefinition a)
    {
        var page = Type(a, "PCL.PageLinkMain");
        var methods = new[] { Method(page, ".ctor", 0), Method(page, "Join", 0), Method(page, "Join", 1),
            Method(page, "JoinInternal", 1), Method(page, "ValidateCodeFormat", 1), Method(page, "TryExit", 2),
            Method(Type(a, "PCL.PageLaunchRight"), ".ctor", 0),
            Method(Type(a, "PCL.FormMain"), "EndProgram", 1),
            Method(Type(a, "PCL.FormMain"), "EndProgramForce", 1) }.ToList();
        var initializer = page.Methods.SingleOrDefault(m => m.Name == ".cctor");
        if (initializer is not null) methods.Add(initializer);
        return methods.ToDictionary(m => m.FullName);
    }

    private static string[] ExternalReferences(AssemblyDefinition a) => Types(a.MainModule.Types)
        .Where(t => !t.FullName.StartsWith("PCL.PageLinkMain", StringComparison.Ordinal))
        .SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
        .Select(i => i.Operand).OfType<MemberReference>()
        .Where(m => m.DeclaringType?.FullName.StartsWith("PCL.PageLinkMain", StringComparison.Ordinal) == true)
        .Select(m => m.FullName).Distinct().Order().ToArray();

    public static void CreateProfile(string input, string output)
    {
        using var a = Open(input);
        CheckIdentity(a, input);
        var profile = new PatchProfile { Version = a.Name.Version.ToString(),
            Methods = ChangedMethods(a).ToDictionary(x => x.Key, x => MethodHash(x.Value)),
            ExternalReferences = ExternalReferences(a) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(profile, Json), new UTF8Encoding(false));
    }

    private static PatchProfile Profile()
    {
        var assembly = typeof(PatchEngine).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("pcl-2.99.0.1.json", StringComparison.Ordinal))
            ?? throw new IOException("补丁适配表缺失，请使用完整构建。");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<PatchProfile>(stream) ?? throw new IOException("补丁适配表损坏。");
    }

    private static void CheckIdentity(AssemblyDefinition a, string path)
    {
        if (a.Name.Name != "Plain Craft Launcher 2" || a.Name.Version != new Version(2, 99, 0, 1))
            throw new IOException("目前只适配 PCL 官方正式版 2.99.0.1，不会强行修改其他版本。");
        var branch = Type(a, "PCL.ModSecret").Fields.SingleOrDefault(f => f.Name == "VersionBranchMain");
        if (branch?.Constant as string != "Official") throw new IOException("目标不是支持的官方分支。");
        using var pe = new PEReader(File.OpenRead(path));
        if (a.Name.HasPublicKey || pe.PEHeaders.PEHeader?.CertificateTableDirectory.Size > 0)
            throw new IOException("目标包含数字签名，补丁拒绝破坏签名。");
    }

    private static void CheckProfile(AssemblyDefinition a)
    {
        var profile = Profile();
        var methods = ChangedMethods(a);
        if (a.Name.Version.ToString() != profile.Version || methods.Count != profile.Methods.Count ||
            methods.Any(x => !profile.Methods.TryGetValue(x.Key, out var hash) || hash != MethodHash(x.Value)) ||
            !ExternalReferences(a).SequenceEqual(profile.ExternalReferences))
            throw new IOException("联机接入点的指纹与适配表不符。可能是其他版本或不同补丁，已停止修改。");
    }

    private static PatchMarker? Marker(AssemblyDefinition a)
    {
        var resource = a.MainModule.Resources.OfType<EmbeddedResource>().SingleOrDefault(r => r.Name == MarkerName);
        if (resource is null) return null;
        var marker = JsonSerializer.Deserialize<PatchMarker>(resource.GetResourceData());
        if (marker is null || !((marker.Schema == 1 && marker.Build == "pcl-link-v1") || (marker.Schema == 2 && marker.Build == "pcl-combined-v2")) ||
            !IsHash(marker.OriginalHash) || (marker.Link && !IsHash(marker.UiHash)))
            throw new IOException("联机补丁标记损坏或不兼容。");
        return marker;
    }

    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    public static PatchInspection Inspect(string input, CombinedOptions? options = null)
    {
        options ??= new CombinedOptions();
        using var a = Open(input);
        var marker = Marker(a);
        try
        {
            CheckIdentity(a, input);
            var baseline = ValidatedBaseline(input, marker);
            using var baseAssembly = baseline == input ? null : Open(baseline);
            if (options.Link) CheckProfile(baseAssembly ?? a);
            var items = DownloadPatches(a, options, true).ToList();
            if (options.Link) items.Add(new("link", "启动页内置联机", marker?.Link == true ? "Already" : "Applied", "自动组网账号；游戏账号登录后启用组网"));
            return new(a.Name.Version.ToString(), marker is not null, true, HashFile(input),
                marker is null ? "已适配：可一次应用所选补丁。" : "已有补丁：保留已安装功能，可补上其他选项。") { Items = items.ToArray() };
        }
        catch (IOException ex) { return new(a.Name.Version.ToString(), marker is not null, false, HashFile(input), ex.Message); }
    }

    private static string Within(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "."))
            throw new IOException("补丁路径不安全。");
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("补丁路径越界。");
        return path;
    }

    private static Dictionary<string, byte[]> ReadPayload(string directory, out PayloadManifest manifest)
    {
        manifest = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllText(Path.Combine(directory, "payload.json")))
            ?? throw new IOException("缺少联机文件清单。");
        if (manifest.Schema != 1 || manifest.PclVersion != "2.99.0.1" || manifest.Files.Count > 64)
            throw new IOException("联机文件清单不兼容。");
        foreach (var required in new[] { UiName, "Link/PCL.Link.exe", "Link/kernel/ETLanCore.exe", "Link/kernel/wintun.dll" })
            if (!manifest.Files.ContainsKey(required)) throw new IOException("缺少联机文件：" + required);
        var files = new Dictionary<string, byte[]>();
        foreach (var entry in manifest.Files)
        {
            var relative = entry.Key.Replace('\\', '/');
            var allowed = relative == UiName || relative is "Link/PCL.Link.exe" or "Link/kernel/ETLanCore.exe" or
                "Link/kernel/wintun.dll" or "Link/kernel/PROVENANCE.md" ||
                (relative.StartsWith("Link/", StringComparison.Ordinal) && relative.Count(c => c == '/') == 1 && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (!allowed || !IsHash(entry.Value)) throw new IOException("联机文件清单包含非模块路径。");
            var bytes = File.ReadAllBytes(Within(directory, relative));
            if (Hash(bytes) != entry.Value) throw new IOException("联机文件校验失败：" + relative);
            files.Add(relative, bytes);
        }
        return files;
    }

    private static MethodReference BridgeMethod(ModuleDefinition module, AssemblyDefinition ui, string name, int count) =>
        module.ImportReference(Method(Type(ui, "PclLink.Ui.Bridge"), name, count));

    private static void Replace(MethodDefinition method, params Instruction[] instructions)
    {
        method.Body = new MethodBody(method) { InitLocals = false, MaxStackSize = 8 };
        foreach (var instruction in instructions) method.Body.Instructions.Add(instruction);
    }

    private static void ApplyHooks(AssemblyDefinition a, AssemblyDefinition ui)
    {
        var page = Type(a, "PCL.PageLinkMain");
        var module = a.MainModule;
        var baseConstructor = Method(Type(a, "PCL.MyPageRight"), ".ctor", 0);
        Replace(Method(page, ".ctor", 0), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, baseConstructor),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "Attach", 1)), Instruction.Create(OpCodes.Ret));
        var initializer = page.Methods.SingleOrDefault(m => m.Name == ".cctor");
        if (initializer is not null)
        {
            var state = page.Fields.Single(f => f.Name == "LinkState");
            Replace(initializer, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stsfld, state), Instruction.Create(OpCodes.Ret));
        }
        Replace(Method(page, "TryExit", 2), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Ldarg_2), Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "TryExit", 3)), Instruction.Create(OpCodes.Ret));
        Replace(Method(page, "Join", 0), Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "Join", 0)), Instruction.Create(OpCodes.Ret));
        Replace(Method(page, "Join", 1), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "Join", 1)), Instruction.Create(OpCodes.Ret));
        Replace(Method(page, "JoinInternal", 1), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "Join", 1)), Instruction.Create(OpCodes.Ret));
        Replace(Method(page, "ValidateCodeFormat", 1), Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "ValidateCodeFormat", 1)), Instruction.Create(OpCodes.Ret));
        var home = Method(Type(a, "PCL.PageLaunchRight"), ".ctor", 0);
        var homeIl = home.Body.GetILProcessor();
        foreach (var ret in home.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            ret.OpCode = OpCodes.Ldarg_0; ret.Operand = null;
            var call = Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "AttachHome", 1));
            homeIl.InsertAfter(ret, call); homeIl.InsertAfter(call, Instruction.Create(OpCodes.Ret));
        }
        var end = Method(Type(a, "PCL.FormMain"), "EndProgram", 1);
        var first = end.Body.Instructions[0];
        var endIl = end.Body.GetILProcessor();
        endIl.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_1));
        endIl.InsertBefore(first, Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "BeforeExit", 1)));
        endIl.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
        endIl.InsertBefore(first, Instruction.Create(OpCodes.Ret));
        ExpandBranches(end);
        var force = Method(Type(a, "PCL.FormMain"), "EndProgramForce", 1);
        force.Body.GetILProcessor().InsertBefore(force.Body.Instructions[0], Instruction.Create(OpCodes.Call, BridgeMethod(module, ui, "CommitExit", 0)));
        ExpandBranches(force);
    }

    private static void ExpandBranches(MethodDefinition method)
    {
        var pairs = new Dictionary<OpCode, OpCode> { [OpCodes.Br_S] = OpCodes.Br, [OpCodes.Brfalse_S] = OpCodes.Brfalse,
            [OpCodes.Brtrue_S] = OpCodes.Brtrue, [OpCodes.Beq_S] = OpCodes.Beq, [OpCodes.Bge_S] = OpCodes.Bge,
            [OpCodes.Bgt_S] = OpCodes.Bgt, [OpCodes.Ble_S] = OpCodes.Ble, [OpCodes.Blt_S] = OpCodes.Blt,
            [OpCodes.Bne_Un_S] = OpCodes.Bne_Un, [OpCodes.Bge_Un_S] = OpCodes.Bge_Un, [OpCodes.Bgt_Un_S] = OpCodes.Bgt_Un,
            [OpCodes.Ble_Un_S] = OpCodes.Ble_Un, [OpCodes.Blt_Un_S] = OpCodes.Blt_Un, [OpCodes.Leave_S] = OpCodes.Leave };
        foreach (var i in method.Body.Instructions) if (pairs.TryGetValue(i.OpCode, out var longCode)) i.OpCode = longCode;
    }

    public static int VerifyPreserved(string baseline, string patched)
    {
        using var before = Open(baseline); using var after = Open(patched);
        var marker = Marker(after) ?? throw new IOException("未找到联机补丁标记。");
        if (marker.OriginalHash != HashFile(baseline) || before.Name.FullName != after.Name.FullName)
            throw new IOException("补丁来源/程序集身份校验失败。");
        var changed = marker.Schema == 1 ? ChangedMethods(before).Keys.ToHashSet() : marker.ChangedMethods.Keys.ToHashSet();
        var originals = Types(before.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
        var result = Types(after.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
        var expectedKeys = originals.Keys.Concat(marker.AddedMethods.Keys).ToHashSet();
        if (!expectedKeys.SetEquals(result.Keys)) throw new IOException("非预期的类型/方法增删。");
        var count = 0;
        foreach (var method in originals)
            if (!changed.Contains(method.Key))
            {
                if (MethodHash(method.Value) != MethodHash(result[method.Key])) throw new IOException("非联机方法被改变：" + method.Key);
                count++;
            }
        if (marker.Schema == 2)
        {
            foreach (var entry in marker.ChangedMethods.Concat(marker.AddedMethods))
                if (!result.TryGetValue(entry.Key, out var method) || MethodHash(method) != entry.Value)
                    throw new IOException("所选补丁方法校验失败：" + entry.Key);
        }
        foreach (var resource in before.MainModule.Resources.OfType<EmbeddedResource>())
        {
            var counterpart = after.MainModule.Resources.OfType<EmbeddedResource>().SingleOrDefault(r => r.Name == resource.Name);
            if (counterpart is null || !resource.GetResourceData().SequenceEqual(counterpart.GetResourceData()))
                throw new IOException("原有资源被改变：" + resource.Name);
        }
        if (after.MainModule.Resources.Count != before.MainModule.Resources.Count + 1) throw new IOException("非预期的资源增删。");
        if (marker.Link) foreach (var name in new[] { ".ctor", "TryExit", "Join", "ValidateCodeFormat" })
            if (!Type(after, "PCL.PageLinkMain").Methods.Where(m => m.Name == name).All(m => m.Body.Instructions.Any(i =>
                    i.Operand is MethodReference r && r.DeclaringType.FullName == "PclLink.Ui.Bridge")))
                throw new IOException("联机接入点未完整替换。");
        return count;
    }

    private static PatchItem[] DownloadPatches(AssemblyDefinition assembly, CombinedOptions options, bool dry)
    {
        if (!options.Download && !options.ProtectUpdates) return [];
        using var context = Pcl2Patch.Ctx.FromAssembly(assembly);
        var result = options.Download
            ? Pcl2Patch.Patches.Run(context, new Pcl2Patch.PatchOptions { Strict = true, KeepAutoUpdate = !options.ProtectUpdates }, dry)
            : Pcl2Patch.Patches.ProtectUpdates(context, dry);
        if (result.Any(item => item.State == Pcl2Patch.PatchState.Failed))
            throw new IOException(string.Join("；", result.Where(item => item.State == Pcl2Patch.PatchState.Failed).Select(item => item.Title + "：" + item.Detail)));
        return result.Select(item => new PatchItem(item.Id, item.Title, item.State.ToString(), item.Detail)).ToArray();
    }

    private static bool AllowedDownloadChange(MethodDefinition method) => method.DeclaringType.FullName switch
    {
        "PCL.PageDownloadInstall" => method.Name == "LoaderInit",
        "PCL.ModModpack" => method.Name is "ModpackInstall" or "DispatchModpackInstall",
        "PCL.PageDownloadLeft" => method.Name == ".ctor",
        "PCL.PageLaunchLeft" => method.Name == "RefreshButtonsUI",
        "PCL.PageSelectRight" => true,
        "PCL.PageDownloadModify" => true,
        "PCL.PageDownloadResourceDetail" => method.Name == "Save_Click",
        "PCL.ModSecret" => method.Name == "UpdateStart",
        "PCL.Settings" => method.Name == ".cctor",
        _ => false
    };

    private static string ValidatedBaseline(string input, PatchMarker? marker)
    {
        if (marker is null) return input;
        var directory = Path.GetDirectoryName(Path.GetFullPath(input))!;
        var state = JsonSerializer.Deserialize<PatchState>(File.ReadAllText(StatePath(directory))) ?? throw new IOException("补丁回滚记录缺失。");
        if (state.OriginalHash != marker.OriginalHash || state.PatchedHash != HashFile(input))
            throw new IOException("所选 EXE 在打补丁后又被修改，已停止自动处理。");
        var backup = BackupPath(directory, marker.OriginalHash);
        if (!File.Exists(backup) || HashFile(backup) != marker.OriginalHash) throw new IOException("原始备份损坏或缺失。");
        return backup;
    }

    private static string StatePath(string directory) => Within(directory, "Link/patch-state.json");
    private static string BackupPath(string directory, string hash)
    {
        if (!IsHash(hash)) throw new IOException("备份标识不安全。");
        return Within(directory, "Link/Backups/" + hash + ".exe");
    }

    public static PatchResult Apply(string input, string target, string payload, CombinedOptions? options = null)
    {
        options ??= new CombinedOptions();
        if (!options.Link && !options.Download && !options.ProtectUpdates) throw new IOException("至少选择一项补丁。");
        input = Path.GetFullPath(input); target = Path.GetFullPath(target);
        var directory = Path.GetDirectoryName(target)!;
        var inputHash = HashFile(input);
        using var selected = Open(input);
        CheckIdentity(selected, input);
        var installed = Marker(selected);
        var baseline = ValidatedBaseline(input, installed);
        using var original = Open(baseline);
        var effective = new CombinedOptions { Link = options.Link || installed?.Link == true,
            Download = options.Download || installed?.Download == true,
            ProtectUpdates = options.ProtectUpdates || installed?.ProtectUpdates == true };
        if (effective.Link) CheckProfile(original);
        if (File.Exists(target) && !input.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            using var existing = Open(target);
            var existingMarker = Marker(existing);
            if (existingMarker?.OriginalHash == HashFile(baseline)) return Apply(target, target, payload, options);
            if (HashFile(target) != inputHash)
                throw new IOException("输出位置已有其他启动器，请选择空的副本目录，避免覆盖已有文件。");
        }
        var files = effective.Link ? ReadPayload(payload, out _) : new Dictionary<string, byte[]>();
        var uiHash = effective.Link ? Hash(files[UiName]) : "";
        if (installed is { Schema: 2 } && input.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            installed.Link == effective.Link && installed.Download == effective.Download && installed.ProtectUpdates == effective.ProtectUpdates &&
            (!effective.Link || installed.UiHash == uiHash))
        {
            foreach (var file in files)
                if (!File.Exists(Within(directory, file.Key)) || HashFile(Within(directory, file.Key)) != Hash(file.Value))
                    throw new IOException("已安装联机文件损坏或被修改：" + file.Key);
            return new(target, BackupPath(directory, installed.OriginalHash), inputHash, 0, true);
        }
        var originalBytes = File.ReadAllBytes(baseline);
        var originalHash = Hash(originalBytes);
        PatchState? previous = File.Exists(StatePath(directory)) ? JsonSerializer.Deserialize<PatchState>(File.ReadAllText(StatePath(directory))) : null;
        foreach (var file in files)
        {
            var destination = Within(directory, file.Key);
            if (!File.Exists(destination)) continue;
            var existing = HashFile(destination);
            if (existing != Hash(file.Value) && (previous?.Files.TryGetValue(file.Key, out var oldHash) != true || oldHash != existing))
                throw new IOException("输出目录里有同名的其他文件，不会覆盖：" + file.Key);
        }
        var before = Types(original.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, MethodHash);
        var items = DownloadPatches(original, effective, false).ToList();
        var downloadChanges = Types(original.MainModule.Types).SelectMany(t => t.Methods)
            .Where(m => !before.TryGetValue(m.FullName, out var hash) || hash != MethodHash(m)).ToArray();
        if (downloadChanges.Any(m => !AllowedDownloadChange(m))) throw new IOException("下载补丁改变了非预期方法，已停止写入。");
        if (effective.Link)
        {
            using var ui = AssemblyDefinition.ReadAssembly(new MemoryStream(files[UiName]));
            if (ui.Name.Name != "PCL.Link.UI") throw new IOException("联机界面 DLL 身份不符。");
            ApplyHooks(original, ui);
            items.Add(new("link", "启动页内置联机", "Applied", "自动组网账号；游戏登录后启用"));
        }
        var after = Types(original.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName, MethodHash);
        var newMarker = new PatchMarker { Schema = 2, Build = "pcl-combined-v2", Version = "2.99.0.1", OriginalHash = originalHash,
            UiHash = uiHash, Link = effective.Link, Download = effective.Download, ProtectUpdates = effective.ProtectUpdates,
            ChangedMethods = after.Where(m => before.TryGetValue(m.Key, out var hash) && hash != m.Value).ToDictionary(),
            AddedMethods = after.Where(m => !before.ContainsKey(m.Key)).ToDictionary() };
        original.MainModule.Resources.Add(new EmbeddedResource(MarkerName, ManifestResourceAttributes.Private,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newMarker))));
        Directory.CreateDirectory(directory);
        var staging = target + ".pcl-link-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            original.Write(staging, new WriterParameters { WriteSymbols = false });
            var preserved = VerifyPreserved(baseline, staging);
            using (var written = Open(staging)) DownloadPatches(written, effective, true);
            if (HashFile(input) != inputHash) throw new IOException("原文件在检查期间发生变化，已停止写入。");
            var backup = BackupPath(directory, originalHash);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (!File.Exists(backup)) File.WriteAllBytes(backup, originalBytes);
            if (HashFile(backup) != originalHash) throw new IOException("备份校验失败。");
            var state = new PatchState { OriginalHash = originalHash, PatchedHash = HashFile(staging), UiHash = uiHash,
                Files = files.ToDictionary(f => f.Key, f => Hash(f.Value)) };
            var writes = files.ToDictionary(f => Within(directory, f.Key), f => f.Value);
            writes[StatePath(directory)] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Json));
            using var deployment = new Deployment(writes);
            deployment.Write();
            if (File.Exists(target) && HashFile(target) != inputHash) throw new IOException("目标文件在写入前发生变化，已停止替换。");
            File.Move(staging, target, true);
            deployment.Commit();
            return new(target, backup, state.PatchedHash, preserved, false) { Items = items.ToArray() };
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public static string Restore(string target)
    {
        target = Path.GetFullPath(target);
        var directory = Path.GetDirectoryName(target)!;
        using var a = Open(target);
        var marker = Marker(a) ?? throw new IOException("这个文件没有安装联机补丁。");
        var state = JsonSerializer.Deserialize<PatchState>(File.ReadAllText(StatePath(directory))) ?? throw new IOException("回滚记录缺失。");
        if (state.OriginalHash != marker.OriginalHash || HashFile(target) != state.PatchedHash)
            throw new IOException("启动器在打补丁后又被修改，自动回滚可能覆盖其他修改，已停止。");
        var backup = BackupPath(directory, marker.OriginalHash);
        if (!File.Exists(backup) || HashFile(backup) != marker.OriginalHash) throw new IOException("原始备份缺失或校验失败。");
        var staging = target + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(backup, staging, false);
            File.Move(staging, target, true);
            state.PatchedHash = "";
            File.WriteAllText(StatePath(directory), JsonSerializer.Serialize(state, Json), new UTF8Encoding(false));
            return backup;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private sealed class Deployment(Dictionary<string, byte[]> files) : IDisposable
    {
        private readonly Dictionary<string, byte[]?> _before = new();
        private bool _committed;
        public void Write()
        {
            foreach (var file in files)
            {
                _before[file.Key] = File.Exists(file.Key) ? File.ReadAllBytes(file.Key) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(file.Key)!);
                AtomicWrite(file.Key, file.Value);
            }
        }
        public void Commit() => _committed = true;
        public void Dispose()
        {
            if (_committed) return;
            foreach (var original in _before.Reverse())
            {
                if (File.Exists(original.Key) && !File.ReadAllBytes(original.Key).SequenceEqual(files[original.Key])) continue;
                if (original.Value is null) { if (File.Exists(original.Key)) File.Delete(original.Key); }
                else AtomicWrite(original.Key, original.Value);
            }
        }
        private static void AtomicWrite(string path, byte[] bytes)
        {
            var temporary = path + ".write-" + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
