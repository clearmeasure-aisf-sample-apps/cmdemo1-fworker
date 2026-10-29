using System.Text.Json.Nodes;
using DbUp.Engine.Output;

namespace Fworker.Database;

/// <summary>
/// The log of DbUp, as in cmdemo1-workorders: only the information lines that name a ".sql" script, warnings and errors. Each line is one JSON object
/// {"gateway":"schema","event":...}. Errors carry the template of the message and the type of the exception, never its text (a SqlClient message can name the server).
/// </summary>
public sealed class QuietLog(Action<string>? sink) : IUpgradeLog
{
    void Write(string ev, string detail)
    {
        if (sink == null) return;
        try { sink(new JsonObject { ["gateway"] = "schema", ["event"] = ev, ["detail"] = detail }.ToJsonString()); } catch { /* a log never fails an upgrade */ }
    }

    static string Format(string format, object[] args)
    {
        try { return args.Length == 0 ? format : string.Format(format, args); } catch (FormatException) { return format; }
    }

    public void LogTrace(string format, params object[] args) { }
    public void LogDebug(string format, params object[] args) { }
    public void LogInformation(string format, params object[] args)
    {
        var line = Format(format, args);
        if (line.Contains(".sql", StringComparison.Ordinal)) Write("script", line);
    }
    public void LogWarning(string format, params object[] args) => Write("warning", Format(format, args));
    public void LogError(string format, params object[] args) => Write("error", format);
    public void LogError(Exception ex, string format, params object[] args) => Write("error", format + " " + ex.GetType().Name);
}
