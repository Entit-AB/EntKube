using System.Globalization;
using System.Text.Json;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Tickets.Bridge;

/// <summary>
/// Reads ServiceNow's delivery.
///
/// <para>ServiceNow has no fixed webhook shape: a Business Rule or a Flow Designer action
/// posts whatever REST message somebody built. What is reliable is the incident's own field
/// names, so this reads those wherever they sit — at the top level, or under
/// <c>record</c> / <c>incident</c> / <c>result</c>, which is what the templates people
/// start from tend to produce.</para>
///
/// <para><b>Priority arrives as a number and is not one of ours.</b> ServiceNow's 1–5 comes
/// from impact × urgency and its 3 is not §14.2's P3. It is passed through as the raw value
/// for the connection's own mapping to interpret, which is the only place that knows what
/// this customer's service desk means by it.</para>
/// </summary>
public class ServiceNowAdapter : IInboundTicketAdapter
{
    public TicketSystem System => TicketSystem.ServiceNow;

    /// <summary>Where an incident hides in the payloads these integrations produce.</summary>
    private static readonly string[] Wrappers = ["record", "incident", "result", "data", "payload"];

    /// <summary>The states ServiceNow numbers as resolved, closed and cancelled.</summary>
    private static readonly HashSet<string> ClosedStates = ["6", "7", "8", "resolved", "closed", "cancelled"];

    public InboundTicketRead Read(string payload, TicketBridgeConnection connection)
    {
        JsonElement root;

        try
        {
            root = JsonDocument.Parse(payload).RootElement;
        }
        catch (JsonException ex)
        {
            return InboundTicketRead.Failed($"The delivery was not JSON: {ex.Message}");
        }

        JsonElement incident = Unwrap(root);

        // sys_id is the stable one. number is what people quote, and is the fallback for an
        // integration that was built without sys_id — it is stable enough to dedupe on.
        string? id = Text(incident, "sys_id") ?? Text(incident, "number");

        if (string.IsNullOrWhiteSpace(id))
        {
            return InboundTicketRead.Failed(
                "The delivery carried neither sys_id nor number, so a resend could not be "
                + "told from a new incident.");
        }

        string? title = Text(incident, "short_description");
        string? state = Text(incident, "state") ?? Text(incident, "incident_state");
        string? comment = Text(incident, "comments") ?? Text(incident, "work_notes");

        InboundTicketKind kind =
            state is not null && ClosedStates.Contains(state.Trim().ToLowerInvariant())
                ? InboundTicketKind.Closed
                : string.IsNullOrWhiteSpace(comment)
                    ? InboundTicketKind.Raised
                    : InboundTicketKind.Updated;

        return InboundTicketRead.Ok(new InboundTicketReport
        {
            ExternalId = id,
            ExternalKey = Text(incident, "number"),
            Url = Link(connection, Text(incident, "sys_id")),
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(title) ? "(no short description)" : title,
            Description = Text(incident, "description") ?? "",
            RawPriority = Text(incident, "priority"),
            ReportedAt = Moment(incident, "opened_at") ?? Moment(incident, "sys_created_on"),
            RequestedBy = Text(incident, "caller_name")
                          ?? Person(incident, "caller_id")
                          ?? Text(incident, "opened_by"),
            RequestedByEmail = Text(incident, "caller_email"),
            // cmdb_ci is the configuration item — which system this is about, where the
            // customer's CMDB is kept up to date enough to say.
            AppHint = Person(incident, "cmdb_ci") ?? Text(incident, "business_service"),
            Note = comment,
        });
    }

    /// <summary>
    /// Digs the incident out of whatever the integration wrapped it in. Anything that
    /// already has a short_description is taken as the incident itself.
    /// </summary>
    private static JsonElement Unwrap(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        if (root.TryGetProperty("short_description", out _) || root.TryGetProperty("sys_id", out _))
        {
            return root;
        }

        foreach (string wrapper in Wrappers)
        {
            if (!root.TryGetProperty(wrapper, out JsonElement inner))
            {
                continue;
            }

            // A result array is what the Table API returns; the first row is the incident.
            if (inner.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement first in inner.EnumerateArray())
                {
                    return first;
                }

                continue;
            }

            if (inner.ValueKind == JsonValueKind.Object)
            {
                return Unwrap(inner);
            }
        }

        return root;
    }

    private static string? Link(TicketBridgeConnection connection, string? sysId) =>
        string.IsNullOrWhiteSpace(sysId) || string.IsNullOrWhiteSpace(connection.Instance)
            ? null
            : $"https://{connection.Instance.Trim().TrimEnd('/')}/nav_to.do?uri=incident.do%3Fsys_id%3D{sysId}";

    private static string? Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Blank(value.GetString()),
            // ServiceNow sends numbers as numbers about half the time.
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// A reference field, which arrives either as a plain string or as
    /// <c>{ value, display_value }</c> depending on how the REST message was built.
    /// </summary>
    private static string? Person(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object
            ? Text(value, "display_value") ?? Text(value, "value")
            : Blank(value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    private static DateTime? Moment(JsonElement parent, string name)
    {
        if (Text(parent, name) is not string text)
        {
            return null;
        }

        // ServiceNow's own format is "yyyy-MM-dd HH:mm:ss", in the instance's timezone —
        // read as UTC, because assuming otherwise would move a reporting time by hours in
        // the direction that invents SLA time.
        // The property named System shadows the namespace, so these are imported above
        // rather than qualified.
        if (DateTime.TryParse(
                text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTime moment))
        {
            return moment;
        }

        return null;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
