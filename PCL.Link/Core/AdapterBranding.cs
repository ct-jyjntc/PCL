namespace AlienLink;

public static class AdapterBranding
{
    public const string AppAdapterName = "PCL Link";
    // The kernel owns adapter creation/removal. Never rename or delete adapters
    // by a shared driver description: an independent Alien Link may be running.
    public static void RenameKernelAdapter() { }
    public static void ResetRenameGate() { }
    public static void RemoveKernelAdapters() { }
}
