using System.Text.Json;

namespace PclLink.Patching;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new PatchWindow());
            return 0;
        }
        try
        {
            object result = args[0] switch
            {
                "inspect" when args.Length >= 2 => PatchEngine.Inspect(args[1], Options(args.Skip(2))),
                "profile" when args.Length == 3 => CreateProfile(args[1], args[2]),
                "patch" when args.Length >= 4 => PatchEngine.Apply(args[1], args[2], args[3], Options(args.Skip(4))),
                "verify" when args.Length == 3 => new { PreservedMethods = PatchEngine.VerifyPreserved(args[1], args[2]) },
                "restore" when args.Length == 2 => new { Backup = PatchEngine.Restore(args[1]) },
                _ => throw new ArgumentException("Usage: inspect EXE [--download] [--no-link] [--protect-updates] | " +
                    "patch INPUT OUTPUT PAYLOAD [options] | verify ORIGINAL PATCHED | restore EXE")
            };
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return result is PatchInspection { Supported: false } ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Environment.GetEnvironmentVariable("PCL_LINK_DEBUG") == "1" ? ex.ToString() : ex.Message);
            return 1;
        }
    }

    private static CombinedOptions Options(IEnumerable<string> flags)
    {
        var result = new CombinedOptions();
        foreach (var flag in flags)
            switch (flag)
            {
                case "--download": result.Download = true; break;
                case "--no-link": result.Link = false; break;
                case "--protect-updates": result.ProtectUpdates = true; break;
                default: throw new ArgumentException("Unknown option: " + flag);
            }
        return result;
    }

    private static object CreateProfile(string input, string output)
    {
        PatchEngine.CreateProfile(input, output);
        return new { Profile = output };
    }
}
