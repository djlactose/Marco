using Marco.Core.Wmi;

namespace Marco.Inventory.Collectors;

internal static class WmiQueryHelpers
{
    public const string CimV2 = "root\\cimv2";

    /// <summary>Query and return the first object, or null if the class returned nothing.</summary>
    public static async Task<WmiObject?> QueryFirstAsync(
        this IWmiSession session, string wql, CancellationToken ct, string ns = CimV2)
    {
        var list = await session.QueryAsync(ns, wql, ct).ConfigureAwait(false);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Query, tolerating a NotSupported (missing class on old OS) as an empty result rather than an error.</summary>
    public static async Task<IReadOnlyList<WmiObject>> QueryTolerantAsync(
        this IWmiSession session, string wql, CancellationToken ct, string ns = CimV2)
    {
        try
        {
            return await session.QueryAsync(ns, wql, ct).ConfigureAwait(false);
        }
        catch (WmiException wex) when (wex.Kind == WmiFailureKind.NotSupported)
        {
            return Array.Empty<WmiObject>();
        }
    }
}
