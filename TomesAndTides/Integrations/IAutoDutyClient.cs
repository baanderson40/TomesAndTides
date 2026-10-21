using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace TomesAndTides.Integrations;

public interface IAutoDutyClient
{
    bool IsStopped();
    string GetConfig(string name);
    bool ContentHasPath(uint territoryId);
    void Run(uint territoryId, bool bareMode);
}

/// <summary>Typed AutoDuty IPC boundary; Run preserves configured loop count by passing zero.</summary>
public sealed class AutoDutyClient(IDalamudPluginInterface pluginInterface) : IAutoDutyClient
{
    public bool IsStopped() => pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc();
    public string GetConfig(string name) => pluginInterface.GetIpcSubscriber<string, string>("AutoDuty.GetConfig").InvokeFunc(name);
    public bool ContentHasPath(uint territoryId) => pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath").InvokeFunc(territoryId);
    public void Run(uint territoryId, bool bareMode) =>
        pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run").InvokeAction(territoryId, 0, bareMode);
}
