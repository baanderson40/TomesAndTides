using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ECommons;

namespace TomesAndTides.Integrations;

/// <summary>Typed Lifestream IPC adapter. Calls are issued only by the framework-thread handoff controller.</summary>
public sealed class LifestreamClient(IDalamudPluginInterface pluginInterface)
{
    private readonly ICallGateSubscriber<bool> isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    private readonly ICallGateSubscriber<string, string, ErrorCode> changeCharacter =
        pluginInterface.GetIpcSubscriber<string, string, ErrorCode>("Lifestream.ChangeCharacter");

    public bool TryGetBusy(out bool busy)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch
        {
            busy = false;
            return false;
        }
    }

    public bool TryChangeCharacter(string name, string world, out string result)
    {
        try
        {
            var response = changeCharacter.InvokeFunc(name, world);
            result = response.ToString();
            return response == ErrorCode.Success;
        }
        catch (Exception error)
        {
            result = $"Lifestream IPC failed: {error.GetBaseException().Message}";
            return false;
        }
    }
}
