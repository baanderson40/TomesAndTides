using System.Reflection;
using Dalamud.Plugin;
using ECommons.Reflection;
using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

/// <summary>Read-only, fail-closed reflection over WahTools Feesh cleanup state.</summary>
public sealed class WahToolsFeeshStatusReader(IDalamudPluginInterface pluginInterface, IWorkflowLog log)
{
    private const string ModuleTypeName = "WahTools.Modules.Feesh.FeeshModule";
    private static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private long generation;
    private WeakReference<object>? lastModule;
    private string? lastUnavailableMessage;
    public WahToolsFeeshDiscoveryDiagnostic LastDiscovery { get; private set; } =
        new(DateTimeOffset.MinValue, false, false, null, null, null, null, "NotRead", "WahTools status has not been read.");

    public WahToolsFeeshStatus Read()
    {
        var now = DateTimeOffset.UtcNow;
        bool metadataFound = false;
        bool metadataLoaded = false;
        string? pluginVersion = null;
        bool? instanceResolved = null;
        bool? moduleFound = null;
        bool? moduleLoaded = null;
        var stage = "PluginMetadata";
        try
        {
            var metadata = pluginInterface.InstalledPlugins.FirstOrDefault(plugin =>
                string.Equals(plugin.InternalName, "WahTools", StringComparison.OrdinalIgnoreCase));
            metadataFound = metadata is not null;
            metadataLoaded = metadata?.IsLoaded == true;
            pluginVersion = metadata?.Version.ToString();
            if (metadata is null || !metadata.IsLoaded)
                return ReportUnavailable("PluginMetadata", "WahTools plugin metadata reports it is not loaded", now,
                    metadataFound, metadataLoaded, pluginVersion, instanceResolved, moduleFound, moduleLoaded);
            var discoveryFailure = $"Dalamud reports WahTools loaded (internal name '{metadata.InternalName}', version {metadata.Version}), but ECommons could not resolve its plugin instance. Check Dalamud/ECommons plugin-manager reflection compatibility.";
            stage = "PluginInstance";
            var resolved = DalamudReflector.TryGetDalamudPlugin("WahTools", out var plugin,
                    suppressErrors: string.Equals(lastUnavailableMessage, discoveryFailure, StringComparison.Ordinal),
                    ignoreCache: true);
            instanceResolved = resolved && plugin is not null;
            if (!instanceResolved.Value)
                return ReportUnavailable("PluginInstance", discoveryFailure, now,
                    metadataFound, metadataLoaded, pluginVersion, instanceResolved, moduleFound, moduleLoaded);
            stage = "FeeshModule";
            var modules = ReadMember(ReadMember(plugin, "Modules"), "Modules") as System.Collections.IEnumerable;
            var module = modules?.Cast<object?>().FirstOrDefault(item => item?.GetType().FullName == ModuleTypeName);
            moduleFound = module is not null;
            if (module is null)
                return ReportUnavailable("FeeshModule", "WahTools Feesh module unavailable", now,
                    metadataFound, metadataLoaded, pluginVersion, instanceResolved, moduleFound, moduleLoaded);
            if (lastModule is null || !lastModule.TryGetTarget(out var prior) || !ReferenceEquals(prior, module))
            {
                lastModule = new WeakReference<object>(module);
                generation++;
            }
            moduleLoaded = ReadRequiredBool(module, "IsLoaded");
            if (moduleLoaded != true)
                return ReportUnavailable("FeeshModule", "WahTools Feesh module is not loaded", now,
                    metadataFound, metadataLoaded, pluginVersion, instanceResolved, moduleFound, moduleLoaded);
            stage = "FeeshStatus";
            var trip = ReadMember(module, "trip");
            var ocean = ReadMember(module, "Ocean");
            var cleaner = ReadMember(module, "Cleaner");
            var desynther = ReadMember(module, "Desynther");
            var discarder = ReadMember(module, "Discarder");
            var buyer = ReadMember(module, "Buyer");
            var status = new WahToolsFeeshStatus(true, now, "WahTools Feesh status observed",
                WahToolsFeeshStatus.NormalizeTripRunning(trip is not null, ReadOptionalBool(trip, "IsRunning")), ReadRequiredBool(ocean, "OnBoat"),
                ReadOptionalBool(module, "Cleaning"), ReadOptionalBool(cleaner, "Running"),
                ReadOptionalBool(desynther, "Running"), ReadOptionalBool(discarder, "Running"),
                ReadOptionalBool(buyer, "Busy"), ReadOptionalString(trip, "State"),
                ReadOptionalString(trip, "StatusText"), ReadOptionalString(cleaner, "State"), generation)
            {
                TripInitialized = trip is not null,
                TripState = trip is null ? "Idle" : ReadOptionalString(trip, "State"),
                TripStatus = trip is null ? "Trip not initialized" : ReadOptionalString(trip, "StatusText"),
            };
            if (!status.HasCompleteCleanupState)
                return ReportUnavailable("FeeshStatus", "WahTools Feesh cleanup members are missing, null, or changed", now,
                    metadataFound, metadataLoaded, pluginVersion, instanceResolved, moduleFound, moduleLoaded);
            if (lastUnavailableMessage is not null)
            {
                log.Information("WahTools Feesh status reflection recovered.");
                lastUnavailableMessage = null;
            }
            LastDiscovery = new(now, metadataFound, metadataLoaded, pluginVersion, instanceResolved,
                moduleFound, moduleLoaded, "FeeshStatus", "WahTools Feesh status read successfully.");
            return status;
        }
        catch (Exception error)
        {
            var detail = $"WahTools status read failed: {error.GetBaseException().Message}";
            return ReportUnavailable(stage, detail, now, metadataFound, metadataLoaded,
                pluginVersion, instanceResolved, moduleFound, moduleLoaded, error);
        }
    }

    private static object? ReadMember(object? instance, string name)
    {
        if (instance is null) return null;
        var type = instance.GetType();
        var property = type.GetProperty(name, Members);
        if (property is not null) return property.GetValue(instance);
        var field = type.GetField(name, Members);
        return field is not null ? field.GetValue(instance) : throw new MissingMemberException(type.FullName, name);
    }

    private static bool? ReadOptionalBool(object? instance, string name) => ReadMember(instance, name) is bool value ? value : null;
    private static bool ReadRequiredBool(object? instance, string name) => ReadMember(instance, name) is bool value
        ? value
        : throw new MissingMemberException(instance?.GetType().FullName, name);
    private static string? ReadOptionalString(object? instance, string name) => ReadMember(instance, name)?.ToString();

    private WahToolsFeeshStatus ReportUnavailable(string stage, string detail, DateTimeOffset now,
        bool metadataFound, bool metadataLoaded, string? pluginVersion, bool? instanceResolved,
        bool? moduleFound, bool? moduleLoaded, Exception? error = null)
    {
        if (!string.Equals(lastUnavailableMessage, detail, StringComparison.Ordinal))
        {
            if (error is null)
                log.Warning("{Detail}", detail);
            else
                log.Warning(error, "{Detail}", detail);
            lastUnavailableMessage = detail;
        }
        LastDiscovery = new(now, metadataFound, metadataLoaded, pluginVersion, instanceResolved,
            moduleFound, moduleLoaded, stage, detail);
        return WahToolsFeeshStatus.Unknown(detail, now) with { Generation = generation };
    }
}

public sealed record WahToolsFeeshDiscoveryDiagnostic(
    DateTimeOffset ObservedUtc,
    bool MetadataFound,
    bool MetadataLoaded,
    string? PluginVersion,
    bool? PluginInstanceResolved,
    bool? FeeshModuleFound,
    bool? FeeshModuleLoaded,
    string Stage,
    string Detail);

public sealed record WahToolsFeeshStatus(bool Available, DateTimeOffset ObservedUtc, string Detail,
    bool? TripRunning, bool? OnBoat, bool? Cleaning, bool? CleanerRunning, bool? DesyntherRunning,
    bool? DiscarderRunning, bool? BuyerBusy, string? TripState, string? TripStatus,
    string? CleanerState, long Generation)
{
    public bool TripInitialized { get; init; }
    public bool HasCompleteCleanupState => TripRunning is not null && OnBoat is not null && Cleaning is not null &&
        CleanerRunning is not null && DesyntherRunning is not null && DiscarderRunning is not null && BuyerBusy is not null;
    public bool IsClean => Available && HasCompleteCleanupState && TripRunning == false && OnBoat == false &&
        Cleaning == false && CleanerRunning == false && DesyntherRunning == false && DiscarderRunning == false && BuyerBusy == false;
    public static WahToolsFeeshStatus Unknown(string detail, DateTimeOffset? now = null) =>
        new(false, now ?? DateTimeOffset.UtcNow, detail, null, null, null, null, null, null, null, null, null, null, 0);

    public static bool? NormalizeTripRunning(bool tripInitialized, bool? tripRunning) =>
        tripInitialized ? tripRunning : false;
}
