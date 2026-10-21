using Dalamud.Plugin.Services;
using TomesAndTides.Debugging;

namespace TomesAndTides.Integrations;

public sealed class DalamudWorkflowLog(IPluginLog log) : IWorkflowLog
{
    public void Information(string message, params object[] values) => log.Information(message, values);
    public void Warning(string message, params object[] values) => log.Warning(message, values);
    public void Warning(Exception exception, string message, params object[] values) => log.Warning(exception, message, values);
    public void Error(string message, params object[] values) => log.Error(message, values);
    public void Error(Exception exception, string message, params object[] values) => log.Error(exception, message, values);
}
