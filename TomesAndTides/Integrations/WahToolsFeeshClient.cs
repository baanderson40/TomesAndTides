using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

public enum FeeshRequestResult { Accepted, Refused, Unavailable }

public interface IWahToolsFeeshClient
{
    bool IsAvailable { get; }
    WahToolsFeeshStatus ReadStatus();
    FeeshRequestResult RequestSingleVoyage(out string detail);
}

public sealed class WahToolsFeeshClient(IDalamudPluginInterface pluginInterface,
    WahToolsFeeshStatusReader statusReader, IWorkflowLog log) : IWahToolsFeeshClient
{
    private readonly ICallGateSubscriber<bool, bool> autoOcean = pluginInterface.GetIpcSubscriber<bool, bool>("WahTools.Feesh.AutoOcean");
    private readonly ICallGateSubscriber<bool> catchTheBoat = pluginInterface.GetIpcSubscriber<bool>("WahTools.Feesh.CatchTheBoat");

    public bool IsAvailable => pluginInterface.InstalledPlugins.Any(plugin =>
        string.Equals(plugin.InternalName, "WahTools", StringComparison.OrdinalIgnoreCase) && plugin.IsLoaded);

    public WahToolsFeeshStatus ReadStatus() => IsAvailable ? statusReader.Read() :
        WahToolsFeeshStatus.Unknown("WahTools is not loaded");

    public FeeshRequestResult RequestSingleVoyage(out string detail)
    {
        try
        {
            if (!autoOcean.InvokeFunc(false))
            {
                detail = "WahTools refused AutoOcean(false); no voyage request was sent.";
                return FeeshRequestResult.Refused;
            }
        }
        catch (Exception error)
        {
            detail = $"AutoOcean(false) outcome is ambiguous: {error.GetBaseException().Message}";
            log.Warning(error, "WahTools AutoOcean(false) outcome is ambiguous; CatchTheBoat will not be retried.");
            return FeeshRequestResult.Unavailable;
        }
        try
        {
            var accepted = catchTheBoat.InvokeFunc();
            detail = accepted ? "WahTools accepted CatchTheBoat; boarding still requires observation." : "WahTools refused CatchTheBoat.";
            return accepted ? FeeshRequestResult.Accepted : FeeshRequestResult.Refused;
        }
        catch (Exception error)
        {
            detail = $"CatchTheBoat outcome is ambiguous: {error.GetBaseException().Message}";
            log.Warning(error, "WahTools CatchTheBoat outcome is ambiguous; request will not be repeated.");
            return FeeshRequestResult.Unavailable;
        }
    }
}
