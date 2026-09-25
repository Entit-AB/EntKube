using System.Text.Json;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Tickets.Bridge;

/// <summary>
/// Reads Jira's webhook payload.
///
/// <para>Jira posts <c>{ webhookEvent, issue: { id, key, self, fields: {...} } }</c>, and
/// for a comment adds a <c>comment</c> object beside the issue. The fields worth anything
/// are <c>summary</c>, <c>description</c>, <c>priority.name</c>, <c>created</c>, the
/// reporter and — where the project uses them — <c>components</c>.</para>
///
/// <para><b>Description is the awkward one.</b> Jira Cloud sends Atlassian Document Format,
/// a nested document rather than text, while Data Center still sends a string. Both are
/// read, because a customer on either should not be told their descriptions are
/// unsupported.</para>
/// </summary>
public class JiraAdapter : IInboundTicketAdapter
{
    public TicketSystem System => TicketSystem.Jira;

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

        if (!root.TryGetProperty("issue", out JsonElement issue))
        {
            return InboundTicketRead.Failed("The delivery carried no issue.");
        }

        string? id = Text(issue, "id");

        if (string.IsNullOrWhiteSpace(id))
        {
            return InboundTicketRead.Failed("The issue had no id to recognise it by later.");
        }

        JsonElement fields = issue.TryGetProperty("fields", out JsonElement f) ? f : default;
        string @event = Text(root, "webhookEvent") ?? "";

        // A comment carries its own author and text; a field change does not, and is
        // recorded as the change it is.
        bool hasComment = root.TryGetProperty("comment", out JsonElement comment)
                          && comment.ValueKind == JsonValueKind.Object;

        InboundTicketKind kind = @event.Contains("deleted", StringComparison.OrdinalIgnoreCase)
            ? InboundTicketKind.Closed
            : @event.EndsWith("created", StringComparison.OrdinalIgnoreCase) && !hasComment
                ? InboundTicketKind.Raised
                : InboundTicketKind.Updated;

        string? title = Text(fields, "summary");

        return InboundTicketRead.Ok(new InboundTicketReport
        {
            ExternalId = id,
            ExternalKey = Text(issue, "key"),
            Url = Browse(connection, Text(issue, "key")),
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(title) ? "(no summary)" : title,
            Description = Document(fields, "description"),
            RawPriority = Nested(fields, "priority", "name"),
            ReportedAt = Moment(fields, "created"),
            RequestedBy = Nested(fields, "reporter", "displayName"),
            RequestedByEmail = Nested(fields, "reporter", "emailAddress"),
            AppHint = FirstComponent(fields),
            Note = hasComment
                ? $"{Nested(comment, "author", "displayName") ?? "Somebody"}: "
                  + Document(comment, "body")
                : null,
        });
    }

    /// <summary>The issue's own page, which is what somebody actually wants from a link.</summary>
    private static string? Browse(TicketBridgeConnection connection, string? key) =>
        string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(connection.Instance)
            ? null
            : $"https://{connection.Instance.Trim().TrimEnd('/')}/browse/{key}";

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Nested(JsonElement parent, string name, string child) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement value)
            ? Text(value, child)
            : null;

    private static DateTime? Moment(JsonElement parent, string name) =>
        Text(parent, name) is string text
        && DateTimeOffset.TryParse(text, out DateTimeOffset moment)
            ? moment.UtcDateTime
            : null;

    /// <summary>The first component, where a project uses them to say which system it is.</summary>
    private static string? FirstComponent(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object
            || !fields.TryGetProperty("components", out JsonElement components)
            || components.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement component in components.EnumerateArray())
        {
            if (Text(component, "name") is string name && !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// A field that is a plain string on Data Center and an Atlassian Document on Cloud.
    /// The document's text is gathered depth-first, which is enough to read it — this is a
    /// ticket description, not a rendering problem.
    /// </summary>
    private static string Document(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement value))
        {
            return "";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        List<string> parts = [];
        Gather(value, parts);
        return string.Join("\n", parts).Trim();
    }

    private static void Gather(JsonElement node, List<string> into)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (Text(node, "text") is string text && text.Length > 0)
                {
                    into.Add(text);
                }

                if (node.TryGetProperty("content", out JsonElement content))
                {
                    Gather(content, into);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement child in node.EnumerateArray())
                {
                    Gather(child, into);
                }

                break;
        }
    }
}
