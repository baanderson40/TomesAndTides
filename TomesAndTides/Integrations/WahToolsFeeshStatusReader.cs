using System.Reflection;
using ECommons.Reflection;
using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

/// <summary>Read-only, fail-closed reflection over WahTools Feesh cleanup state.</summary>
public sealed class WahToolsFeeshStatusReader(IWorkflowLog log)
{
    private const string ModuleTypeName = "WahTools.Modules.Feesh.FeeshModule";
    private static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private long generation;
    private WeakReference<object>? lastModule;

    public WahToolsFeeshStatus Read()
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin("WahTools", out var plugin, suppressErrors: true, ignoreCache: true) || plugin is null)
                return WahToolsFeeshStatus.Unknown("WahTools plugin instance unavailable", now);
            var modules = ReadMember(ReadMember(plugin, "Modules"), "Modules") as System.Collections.IEnumerable;
            var module = modules?.Cast<object?>().FirstOrDefault(item => item?.GetType().FullName == ModuleTypeName);
            if (module is null)
                return WahToolsFeeshStatus.Unknown("WahTools Feesh module unavailable", now);
            if (lastModule is null || !lastModule.TryGetTarget(out var prior) || !ReferenceEquals(prior, module))
            {
                lastModule = new WeakReference<object>(module);
                generation++;
            }
            var trip = ReadMember(module, "trip");
            var ocean = ReadMember(module, "Ocean");
            var cleaner = ReadMember(module, "Cleaner");
            var desynther = ReadMember(module, "Desynther");
            var discarder = ReadMember(module, "Discarder");
            var buyer = ReadMember(module, "Buyer");
            var status = new WahToolsFeeshStatus(true, now, "WahTools Feesh status observed",
                ReadOptionalBool(trip, "IsRunning"), ReadOptionalBool(ocean, "OnBoat"),
                ReadOptionalBool(module, "Cleaning"), ReadOptionalBool(cleaner, "Running"),
                ReadOptionalBool(desynther, "Running"), ReadOptionalBool(discarder, "Running"),
                ReadOptionalBool(buyer, "Busy"), ReadOptionalString(trip, "State"),
                ReadOptionalString(trip, "StatusText"), ReadOptionalString(cleaner, "State"), generation);
            if (!status.HasCompleteCleanupState)
                return WahToolsFeeshStatus.Unknown("WahTools Feesh cleanup members are missing or changed", now) with { Generation = generation };
            return status;
        }
        catch (Exception error)
        {
            log.Warning(error, "WahTools Feesh status reflection unavailable; Ocean Fishing handoff remains blocked.");
            return WahToolsFeeshStatus.Unknown($"WahTools status read failed: {error.GetBaseException().Message}", now) with { Generation = generation };
        }
    }

    private static object? ReadMember(object? instance, string name)
    {
        if (instance is null) return null;
        var type = instance.GetType();
        var property = type.GetProperty(name, Members);
        if (property is not null) return property.GetValue(instance);
        return type.GetField(name, Members)?.GetValue(instance);
    }

    private static bool? ReadOptionalBool(object? instance, string name) => ReadMember(instance, name) is bool value ? value : null;
    private static string? ReadOptionalString(object? instance, string name) => ReadMember(instance, name)?.ToString();
}

public sealed record WahToolsFeeshStatus(bool Available, DateTimeOffset ObservedUtc, string Detail,
    bool? TripRunning, bool? OnBoat, bool? Cleaning, bool? CleanerRunning, bool? DesyntherRunning,
    bool? DiscarderRunning, bool? BuyerBusy, string? TripState, string? TripStatus,
    string? CleanerState, long Generation)
{
    public bool HasCompleteCleanupState => TripRunning is not null && OnBoat is not null && Cleaning is not null &&
        CleanerRunning is not null && DesyntherRunning is not null && DiscarderRunning is not null && BuyerBusy is not null;
    public bool IsClean => Available && HasCompleteCleanupState && TripRunning == false && OnBoat == false &&
        Cleaning == false && CleanerRunning == false && DesyntherRunning == false && DiscarderRunning == false && BuyerBusy == false;
    public static WahToolsFeeshStatus Unknown(string detail, DateTimeOffset? now = null) =>
        new(false, now ?? DateTimeOffset.UtcNow, detail, null, null, null, null, null, null, null, null, null, null, 0);
}
