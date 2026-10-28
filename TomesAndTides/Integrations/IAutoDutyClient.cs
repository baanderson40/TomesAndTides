using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace TomesAndTides.Integrations;

public interface IAutoDutyClient
{
    bool IsStopped();
    string GetConfig(string name);
    void SetConfig(string name, string value);
    bool ContentHasPath(uint territoryId);
    void Run(uint territoryId, int loops, bool bareMode);
}

/// <summary>Typed AutoDuty IPC boundary. TT writes only the selected duty mode and requests an explicit one-run dispatch.</summary>
public sealed class AutoDutyClient(IDalamudPluginInterface pluginInterface) : IAutoDutyClient
{
    public bool IsStopped() => pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc();
    public string GetConfig(string name) => pluginInterface.GetIpcSubscriber<string, string>("AutoDuty.GetConfig").InvokeFunc(name);
    public void SetConfig(string name, string value) =>
        pluginInterface.GetIpcSubscriber<string, object, object>("AutoDuty.SetConfig").InvokeAction(name, value);
    public bool ContentHasPath(uint territoryId) => pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath").InvokeFunc(territoryId);
    public void Run(uint territoryId, int loops, bool bareMode) =>
        pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run").InvokeAction(territoryId, loops, bareMode);
}
