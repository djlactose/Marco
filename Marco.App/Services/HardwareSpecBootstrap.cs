using System.IO;
using Marco.Core.Hardware;
using Marco.Core.Storage;

namespace Marco.App.Services;

/// <summary>Installs the bundled hardware spec table plus the operator override file as the process-wide table,
/// writing a commented starter override file on first run so the format is discoverable beside the data.</summary>
public static class HardwareSpecBootstrap
{
    public static HardwareSpecTable Install(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.HardwareSpecsFile))
                File.WriteAllText(paths.HardwareSpecsFile, HardwareSpecTable.OverrideTemplate);
        }
        catch { /* read-only install location: the bundled table still works */ }

        var table = HardwareSpecTable.LoadWithOverride(paths.HardwareSpecsFile);
        HardwareSpecTable.Current = table;
        return table;
    }
}
