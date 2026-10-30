using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fworker.Integrations.Alerts;

// W8.2: an Azure Monitor alert as an incident trigger of the ingress inbox. Pure: bytes in, one event object out; the route (gateway/Endpoints/AlertsEndpoints.cs) authenticates, resolves the
// subject and stores the event, and the IncidentSaga (W8.1) consumes it. Only the common alert schema is read (the action group that calls the factory sets useCommonAlertSchema: true);
// the alert's own context (alertContext) is not carried: it is large, differs by signal type and the triage session reads Application Insights itself.

/// <summary>The result of reading one delivery: the event for the inbox, or why the body is not an alert this gateway can use.</summary>
public sealed record AlertParse(bool Ok, string Reason, JsonObject? Event, string? AlertId = null);

public static class AzureMonitorAlerts
{
    public const string Provider = "azure-monitor", FiredEvent = "incident.alert", ResolvedEvent = "incident.alert.resolved", InboxSource = "azure-monitor";
    public const string CommonSchema = "azureMonitorCommonAlertSchema";
    public const int MaxTargets = 10, MaxText = 500, MaxId = 400;

    static readonly Regex Name = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The deployable or environment name as the factory spells it (lower case, letters, digits, dot, dash, underscore), or null when it is not one.</summary>
    public static string? Subject(string? value) => value?.Trim().ToLowerInvariant() is { } v && Name.IsMatch(v) ? v : null;

    /// <summary>
    /// Reads the delivery. The event is "incident.alert" while the alert is Fired and "incident.alert.resolved" when it is Resolved; its ref is the alert id and the condition, so a delivery Azure sends again
    /// (it retries on a 5xx) has the ref of the first and the inbox drops it (the delivery layer of IngressKeys), while the resolution of the same alert is a delivery of its own.
    /// </summary>
    public static AlertParse Parse(ReadOnlySpan<byte> body, string deployable, string environment, DateTimeOffset receivedAt)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body, nodeOptions: null, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return new(false, "not-json", null); }
        if (root is not JsonObject o) return new(false, "not-json", null);
        if (!string.Equals(Text(o["schemaId"], 100), CommonSchema, StringComparison.Ordinal)) return new(false, "unsupported-schema", null);
        if (o["data"]?["essentials"] is not JsonObject e) return new(false, "no-essentials", null);
        var alertId = Text(e["alertId"], MaxId);
        if (alertId == "") return new(false, "no-alert-id", null);
        var rule = Text(e["alertRule"], 200);
        if (rule == "") return new(false, "no-alert-rule", null);
        var condition = Text(e["monitorCondition"], 20);
        var fired = string.Equals(condition, "Fired", StringComparison.OrdinalIgnoreCase);
        if (!fired && !string.Equals(condition, "Resolved", StringComparison.OrdinalIgnoreCase)) return new(false, "unknown-condition", null, alertId);
        condition = fired ? "Fired" : "Resolved";
        var severity = Text(e["severity"], 10);
        var targets = new JsonArray();
        if (e["alertTargetIDs"] is JsonArray ids) foreach (var id in ids.Take(MaxTargets)) if (Text(id, 300) is { Length: > 0 } t) targets.Add(t);
        var ev = new JsonObject
        {
            ["provider"] = Provider,
            ["event"] = fired ? FiredEvent : ResolvedEvent,
            ["ref"] = $"{alertId}|{condition}",
            ["deployable"] = deployable,
            ["environment"] = environment,
            ["alertId"] = alertId,
            ["rule"] = rule,
            ["severity"] = severity,
            ["severityLevel"] = Level(severity),
            ["signal"] = Text(e["signalType"], 40),
            ["service"] = Text(e["monitoringService"], 60),
            ["condition"] = condition,
            ["firedAt"] = Time(e["firedDateTime"]),
            ["resolvedAt"] = Time(e["resolvedDateTime"]),
            ["description"] = Text(e["description"], MaxText),
            ["targets"] = targets,
            ["receivedAt"] = receivedAt.ToString("o"),
        };
        return new(true, "ok", ev, alertId);
    }

    /// <summary>Sev0 (critical) to Sev4 (verbose) as 0 to 4; -1 for a spelling Azure does not use.</summary>
    public static int Level(string severity) => severity.Length == 4 && severity.StartsWith("Sev", StringComparison.OrdinalIgnoreCase) && severity[3] is >= '0' and <= '4' ? severity[3] - '0' : -1;

    static string Text(JsonNode? n, int max)
    {
        var s = n is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : "";
        return s.Length <= max ? s : s[..max];
    }

    // a time Azure wrote, in ISO form, or "" (an unfired or unresolved alert carries the default time or none)
    static string Time(JsonNode? n) => Text(n, 40) is { Length: > 0 } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) && t.Year > 1 ? t.ToUniversalTime().ToString("o") : "";
}
