using System.Resources;
using Mono.Cecil;

namespace PclLink.Patching;

internal sealed class EmbeddedAssemblyResolver : DefaultAssemblyResolver
{
    private readonly Dictionary<string, byte[]> _libraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AssemblyDefinition> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public EmbeddedAssemblyResolver(string path)
    {
        AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var directory in new[] { Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319"),
            Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319") })
            if (Directory.Exists(directory)) { AddSearchDirectory(directory); AddSearchDirectory(Path.Combine(directory, "Facades")); }
        // PCL stores its public dependency DLLs in its resource container. Cecil
        // needs their enum definitions to serialize optional parameter constants.
        // Resolve those libraries in memory; never extract credential fields.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PCLCS", "MeloongCore", "MeloongCore.Wpf",
            "Newtonsoft.Json", "Microsoft.Win32.Registry", "NAudio", "Ookii.Dialogs.Wpf", "Imazen.WebP",
            "CacheCow.Common", "CacheCow.Client", "CacheCow.Client.FileStore", "ThrottleDebounce", "System.Net.Http.Formatting" };
        using var a = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(path)));
        foreach (var resource in a.MainModule.Resources.OfType<EmbeddedResource>().Where(r => r.Name.EndsWith(".resources", StringComparison.Ordinal)))
        {
            using var reader = new ResourceReader(new MemoryStream(resource.GetResourceData()));
            var entries = reader.GetEnumerator();
            while (entries.MoveNext())
            {
                if (entries.Key is not string key || !allowed.Contains(key)) continue;
                reader.GetResourceData(key, out var type, out var data);
                // Read raw byte-array resources, never deserialize resource objects.
                if (type != "ResourceTypeCode.ByteArray" || data.Length < 4) continue;
                var length = BitConverter.ToInt32(data, 0);
                if (length < 0 || length > 32 * 1024 * 1024 || length != data.Length - 4) continue;
                _libraries[key] = data.AsSpan(4).ToArray();
            }
        }
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_loaded.TryGetValue(name.Name, out var cached)) return cached;
        if (_libraries.TryGetValue(name.Name, out var bytes))
        {
            var assembly = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes),
                new ReaderParameters { AssemblyResolver = this, InMemory = true, ReadSymbols = false });
            _loaded[name.Name] = assembly;
            return assembly;
        }
        parameters.AssemblyResolver = this;
        parameters.InMemory = true;
        parameters.ReadSymbols = false;
        return base.Resolve(name, parameters);
    }
}
