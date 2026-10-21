namespace TomesAndTides.Debugging;

public interface IWorkflowLog
{
    void Information(string message, params object[] values);
    void Warning(string message, params object[] values);
    void Warning(Exception exception, string message, params object[] values);
    void Error(string message, params object[] values);
    void Error(Exception exception, string message, params object[] values);
}
