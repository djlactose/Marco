using Marco.Core.Inventory;
using Marco.Core.Model;
using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

/// <summary>Windows services (state, start mode, run-as account, binary path) plus the startup items Windows
/// itself reports (Win32_StartupCommand: Run keys and Startup folders). The "automatic but not running" count
/// on the machine is derived from the list.</summary>
public sealed class ServicesCollector : IInventoryCollector
{
    public string Name => "Services";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var rows = await context.Wmi.QueryAsync(WmiQueryHelpers.CimV2,
            "SELECT Name, DisplayName, State, StartMode, StartName, PathName, ProcessId FROM Win32_Service", ct);
        var services = new List<ServiceEntry>();
        foreach (var r in rows)
        {
            var name = r.GetString("Name")?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            services.Add(new ServiceEntry
            {
                Name = name,
                DisplayName = r.GetString("DisplayName")?.Trim() ?? name,
                State = r.GetString("State"),
                StartMode = r.GetString("StartMode"),
                Account = r.GetString("StartName"),
                Path = r.GetString("PathName"),
                ProcessId = r.GetInt("ProcessId") is { } pid and > 0 ? pid : null,
            });
        }
        services.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        machine.Services = services;

        // Startup items are a bonus — a provider hiccup here must not lose the service list.
        ct.ThrowIfCancellationRequested();
        var startup = await context.Wmi.QueryOptionalAsync(
            "SELECT Name, Command, Location, User FROM Win32_StartupCommand", ct);
        var items = new List<StartupEntry>();
        foreach (var s in startup)
        {
            items.Add(new StartupEntry
            {
                Name = s.GetString("Name")?.Trim(),
                Command = s.GetString("Command")?.Trim(),
                Location = s.GetString("Location")?.Trim(),
                User = s.GetString("User")?.Trim(),
            });
        }
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        machine.StartupItems = items;

        machine.RefreshCounts();
    }
}

/// <summary>Scheduled tasks outside the \Microsoft\ tree — the ones an admin (or an installer) created — with
/// the principal they run as. Off by default in the checklist: the Task Scheduler provider is comparatively slow
/// and most of the hundreds of tasks on a box are Microsoft's own.</summary>
public sealed class ScheduledTasksCollector : IInventoryCollector
{
    public string Name => "ScheduledTasks";

    public async Task CollectAsync(InventoryContext context, Machine machine, CancellationToken ct)
    {
        var rows = await context.Wmi.QueryAsync(WmiQueryHelpers.TaskScheduler,
            "SELECT TaskName, TaskPath, State, Author, Date, Principal FROM MSFT_ScheduledTask", ct);
        var tasks = new List<ScheduledTaskEntry>();
        foreach (var r in rows)
        {
            var path = r.GetString("TaskPath") ?? "\\";
            if (IsMicrosoftPath(path)) continue;
            var principal = r["Principal"] as WmiObject;
            tasks.Add(new ScheduledTaskEntry
            {
                Name = r.GetString("TaskName"),
                Path = path,
                State = DescribeState(r.GetInt("State")),
                RunAs = principal?.GetString("UserId") ?? principal?.GetString("GroupId"),
                Author = r.GetString("Author"),
                Created = r.GetDateTime("Date"),
            });
        }
        tasks.Sort((a, b) => string.Compare(a.Path + a.Name, b.Path + b.Name, StringComparison.OrdinalIgnoreCase));
        machine.ScheduledTasks = tasks;
    }

    public static bool IsMicrosoftPath(string path)
        => path.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase)
        || path.Equals("\\Microsoft", StringComparison.OrdinalIgnoreCase);

    public static string? DescribeState(int? state) => state switch
    {
        0 => "Unknown",
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => null,
    };
}
