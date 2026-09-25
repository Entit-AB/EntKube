using EntKube.Web.Data;
using EntKube.Web.Services.Tickets.Bridge;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Reading what a customer's Jira actually posts.
///
/// <para>An adapter renames fields and decides nothing, so what is tested here is the
/// awkwardness of the real payloads: Cloud and Data Center disagree about what a
/// description is, and a delivery with no id would make duplicates of everything.</para>
/// </summary>
public class JiraAdapterTests
{
    private static readonly TicketBridgeConnection Connection = new()
    {
        Instance = "entit.atlassian.net",
        System = TicketSystem.Jira,
    };

    private static InboundTicketReport Read(string payload)
    {
        InboundTicketRead read = new JiraAdapter().Read(payload, Connection);
        read.IsOk.Should().BeTrue(read.Error);
        return read.Report!;
    }

    [Fact]
    public void An_issue_is_read_into_a_report()
    {
        InboundTicketReport report = Read(
            """
            {
              "webhookEvent": "jira:issue_created",
              "issue": {
                "id": "10042",
                "key": "SUP-481",
                "fields": {
                  "summary": "Journalen svarar inte",
                  "description": "Vi kommer inte in i journalsystemet.",
                  "priority": { "name": "Highest" },
                  "created": "2026-09-22T08:14:31.000+0200",
                  "reporter": {
                    "displayName": "Karin Karlsson",
                    "emailAddress": "karin@entit.example"
                  },
                  "components": [ { "name": "Journalportalen" } ]
                }
              }
            }
            """);

        report.ExternalId.Should().Be("10042");
        report.ExternalKey.Should().Be("SUP-481");
        report.Kind.Should().Be(InboundTicketKind.Raised);
        report.Title.Should().Be("Journalen svarar inte");
        report.Description.Should().Be("Vi kommer inte in i journalsystemet.");
        report.RawPriority.Should().Be("Highest");
        report.RequestedBy.Should().Be("Karin Karlsson");
        report.RequestedByEmail.Should().Be("karin@entit.example");
        report.AppHint.Should().Be("Journalportalen");
        report.Url.Should().Be("https://entit.atlassian.net/browse/SUP-481");

        // The offset is +0200, so the moment is 06:14 UTC. Reading it as anything else
        // would move a reporting time by two hours on a clock §14.6 makes evidence.
        report.ReportedAt.Should().Be(new DateTime(2026, 9, 22, 6, 14, 31, DateTimeKind.Utc));
    }

    /// <summary>
    /// Jira Cloud sends the description as an Atlassian document rather than a string. A
    /// customer on Cloud should not be told their descriptions are unsupported.
    /// </summary>
    [Fact]
    public void A_cloud_description_is_read_out_of_the_document()
    {
        InboundTicketReport report = Read(
            """
            {
              "webhookEvent": "jira:issue_created",
              "issue": {
                "id": "10042",
                "fields": {
                  "summary": "Fel",
                  "description": {
                    "type": "doc",
                    "content": [
                      { "type": "paragraph", "content": [ { "type": "text", "text": "Första raden." } ] },
                      { "type": "paragraph", "content": [ { "type": "text", "text": "Andra raden." } ] }
                    ]
                  }
                }
              }
            }
            """);

        report.Description.Should().Be("Första raden.\nAndra raden.");
    }

    [Fact]
    public void A_comment_becomes_an_update_under_its_authors_name()
    {
        InboundTicketReport report = Read(
            """
            {
              "webhookEvent": "comment_created",
              "issue": { "id": "10042", "key": "SUP-481", "fields": { "summary": "Fel" } },
              "comment": {
                "author": { "displayName": "Karin Karlsson" },
                "body": "Det är fortfarande nere."
              }
            }
            """);

        report.Kind.Should().Be(InboundTicketKind.Updated);
        report.Note.Should().Be("Karin Karlsson: Det är fortfarande nere.");
    }

    /// <summary>
    /// <b>No id, no delivery.</b> Inventing one would make every resend a new ticket, and a
    /// service desk resends on every field change.
    /// </summary>
    [Fact]
    public void An_issue_with_no_id_is_refused()
    {
        InboundTicketRead read = new JiraAdapter().Read(
            """{ "webhookEvent": "jira:issue_created", "issue": { "key": "SUP-481" } }""",
            Connection);

        read.IsOk.Should().BeFalse();
        read.Error.Should().Contain("id");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"webhookEvent\": \"jira:issue_created\" }")]
    public void An_unreadable_delivery_is_an_answer_and_not_an_exception(string payload)
    {
        InboundTicketRead read = new JiraAdapter().Read(payload, Connection);

        read.IsOk.Should().BeFalse();
        read.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>A summary can be missing; the ticket's title cannot.</summary>
    [Fact]
    public void An_issue_with_no_summary_still_gets_a_title() =>
        Read("""{ "webhookEvent": "jira:issue_created", "issue": { "id": "1", "fields": {} } }""")
            .Title.Should().NotBeNullOrWhiteSpace();
}

/// <summary>
/// Reading what a customer's ServiceNow posts.
///
/// <para>There is no fixed shape: a Business Rule or a Flow action sends whatever REST
/// message somebody built, so the incident turns up at the top level or inside any of half
/// a dozen wrappers. What is reliable is the field names.</para>
/// </summary>
public class ServiceNowAdapterTests
{
    private static readonly TicketBridgeConnection Connection = new()
    {
        Instance = "entit.service-now.com",
        System = TicketSystem.ServiceNow,
    };

    private static InboundTicketReport Read(string payload)
    {
        InboundTicketRead read = new ServiceNowAdapter().Read(payload, Connection);
        read.IsOk.Should().BeTrue(read.Error);
        return read.Report!;
    }

    [Fact]
    public void An_incident_is_read_into_a_report()
    {
        InboundTicketReport report = Read(
            """
            {
              "sys_id": "a1b2c3d4e5f6",
              "number": "INC0012345",
              "short_description": "Journalen svarar inte",
              "description": "Ingen kommer in.",
              "priority": "1",
              "state": "2",
              "opened_at": "2026-09-22 06:14:31",
              "caller_id": { "display_value": "Karin Karlsson", "value": "abc" },
              "caller_email": "karin@entit.example",
              "cmdb_ci": { "display_value": "Journalportalen" }
            }
            """);

        report.ExternalId.Should().Be("a1b2c3d4e5f6");
        report.ExternalKey.Should().Be("INC0012345");
        report.Title.Should().Be("Journalen svarar inte");
        report.RawPriority.Should().Be("1");
        report.RequestedBy.Should().Be("Karin Karlsson");
        report.RequestedByEmail.Should().Be("karin@entit.example");
        report.AppHint.Should().Be("Journalportalen");
        report.ReportedAt.Should().Be(new DateTime(2026, 9, 22, 6, 14, 31, DateTimeKind.Utc));
        report.Url.Should().Contain("a1b2c3d4e5f6");
    }

    /// <summary>
    /// The templates people start from wrap the incident, and they do not agree on what
    /// in. Refusing the wrapped ones would mean refusing most real integrations.
    /// </summary>
    [Theory]
    [InlineData("record")]
    [InlineData("incident")]
    [InlineData("data")]
    [InlineData("payload")]
    public void An_incident_is_found_inside_a_wrapper(string wrapper) =>
        Read($$"""
            { "{{wrapper}}": { "sys_id": "abc", "short_description": "Fel" } }
            """)
            .ExternalId.Should().Be("abc");

    /// <summary>The Table API answers with a result array; the first row is the incident.</summary>
    [Fact]
    public void An_incident_is_found_inside_a_result_array() =>
        Read("""{ "result": [ { "sys_id": "abc", "short_description": "Fel" } ] }""")
            .ExternalId.Should().Be("abc");

    /// <summary>ServiceNow sends numbers as numbers roughly half the time.</summary>
    [Fact]
    public void A_priority_sent_as_a_number_is_still_read() =>
        Read("""{ "sys_id": "abc", "short_description": "Fel", "priority": 1 }""")
            .RawPriority.Should().Be("1");

    /// <summary>
    /// A reference field arrives either as a plain string or as an object, depending on how
    /// the REST message was built.
    /// </summary>
    [Fact]
    public void A_reference_field_is_read_either_way() =>
        Read("""{ "sys_id": "abc", "short_description": "Fel", "cmdb_ci": "Journalportalen" }""")
            .AppHint.Should().Be("Journalportalen");

    /// <summary>
    /// An integration built without sys_id still has a number, which is stable enough to
    /// recognise a resend by — and refusing those would refuse a working service desk.
    /// </summary>
    [Fact]
    public void The_incident_number_stands_in_for_a_missing_sys_id() =>
        Read("""{ "number": "INC0012345", "short_description": "Fel" }""")
            .ExternalId.Should().Be("INC0012345");

    [Fact]
    public void An_incident_with_neither_identifier_is_refused()
    {
        InboundTicketRead read = new ServiceNowAdapter().Read(
            """{ "short_description": "Fel" }""", Connection);

        read.IsOk.Should().BeFalse();
        read.Error.Should().Contain("resend");
    }

    [Theory]
    [InlineData("6")]
    [InlineData("7")]
    [InlineData("closed")]
    [InlineData("Resolved")]
    public void A_resolved_or_closed_state_is_read_as_a_closure(string state) =>
        Read($$"""{ "sys_id": "abc", "short_description": "Fel", "state": "{{state}}" }""")
            .Kind.Should().Be(InboundTicketKind.Closed);

    [Fact]
    public void Work_notes_arrive_as_an_update()
    {
        InboundTicketReport report = Read(
            """
            { "sys_id": "abc", "short_description": "Fel", "state": "2",
              "work_notes": "Väntar på leverantören." }
            """);

        report.Kind.Should().Be(InboundTicketKind.Updated);
        report.Note.Should().Be("Väntar på leverantören.");
    }

    [Fact]
    public void An_unreadable_delivery_is_an_answer_and_not_an_exception() =>
        new ServiceNowAdapter().Read("not json", Connection).IsOk.Should().BeFalse();
}
