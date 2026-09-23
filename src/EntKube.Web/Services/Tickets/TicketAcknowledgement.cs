using System.Globalization;
using System.Text;
using EntKube.Web.Data;
using EntKube.Web.Services.Support;

namespace EntKube.Web.Services.Tickets;

/// <summary>What we send back to somebody who has just reported a fault.</summary>
/// <param name="Subject">Carries the reference, so a reply can be threaded by it alone.</param>
/// <param name="Body">Plain text. Read on a phone in a corridor more often than not.</param>
public readonly record struct Acknowledgement(string Subject, string Body);

/// <summary>
/// The receipt for a reported fault: we have it, this is its number, this is when you will
/// hear from us.
///
/// <para><b>Why this is sent without anybody pressing a button</b>, when nothing else in
/// this subsystem is. Everything else the machine could say to a customer is a judgement —
/// what priority this is, whether it is resolved, whether it is billable — and §14.3 and
/// §14.4 make those written acts by a person. A receipt is not a judgement. It is a fact
/// about the past: a message arrived, at this time, and is now numbered. §14.6 makes those
/// timestamps the record between the parties, which is an argument for telling the customer
/// what we recorded rather than for keeping it.</para>
///
/// <para><b>What it must not say.</b> Not that the priority is confirmed. Until the first
/// assessment §14.3 gives the customer's own assessment precedence, and a receipt that
/// announces "Priority: P3" reads as our decision — which would either bind us to something
/// nobody assessed or look like a downgrade of what they told us. So the priority is
/// described as reported, in those words, with the confirmation named as a separate thing
/// still to come.</para>
///
/// <para>Pure, so the wording can be argued over in a test rather than in a mailbox.</para>
/// </summary>
public static class TicketAcknowledgement
{
    /// <summary>
    /// The reference as it appears in a subject line. Put there so a reply threads even
    /// from a client that strips In-Reply-To, or from somebody forwarding by hand.
    /// </summary>
    public static string Reference(int number) =>
        string.Create(CultureInfo.InvariantCulture, $"[#{number}]");

    /// <summary>
    /// Builds the receipt.
    /// </summary>
    /// <param name="ticket">The ticket as it stands the moment it was created.</param>
    /// <param name="window">The support window its application is covered by.</param>
    /// <param name="appName">The application, when one was recognised.</param>
    /// <param name="responseDue">
    /// When §14.4's response target expires, counted inside the window. Null when the
    /// priority carries no response target.
    /// </param>
    /// <param name="portalUrl">Where they can see it, when the installation has a portal.</param>
    public static Acknowledgement For(
        Ticket ticket,
        SupportWindow window,
        string? appName,
        DateTime? responseDue,
        string? portalUrl = null)
    {
        StringBuilder body = new();

        body.Append("We have received your report and opened ticket ")
            .Append(Reference(ticket.Number))
            .AppendLine(".")
            .AppendLine();

        body.Append("  Reference:   ").Append(Reference(ticket.Number)).AppendLine();
        body.Append("  Reported:    ").AppendLine(Stockholm(ticket.ReportedAt));

        if (!string.IsNullOrWhiteSpace(appName))
        {
            body.Append("  Application: ").AppendLine(appName);
        }

        body.Append("  Subject:     ").AppendLine(ticket.Title);

        // As reported, never as decided. See the class remarks.
        body.Append("  Priority:    ")
            .Append(Describe(ticket.Priority))
            .AppendLine(" — as reported, not yet confirmed");

        body.Append("  Cover:       ").AppendLine(Describe(window));

        body.AppendLine();

        if (responseDue is DateTime due)
        {
            body.Append("We will come back to you by ")
                .Append(Stockholm(due))
                .AppendLine(" at the latest.");
            body.AppendLine(
                "That is counted inside the support hours above, so time outside them does "
                + "not count against it.");
        }
        else
        {
            body.AppendLine(
                "We will come back to you with an assessment. This priority carries no fixed "
                + "response time.");
        }

        body.AppendLine()
            .AppendLine(
                "Our first assessment will confirm the priority in writing, with the reason. "
                + "If you disagree with it, say so on this ticket and we will take it up.")
            .AppendLine();

        body.AppendLine("Replying to this message adds your reply to the same ticket — please "
            + "keep the reference in the subject.");

        if (!string.IsNullOrWhiteSpace(portalUrl))
        {
            body.AppendLine().Append("You can follow it here: ").AppendLine(portalUrl);
        }

        return new Acknowledgement(
            $"{Reference(ticket.Number)} {ticket.Title}",
            body.ToString());
    }

    /// <summary>
    /// The priority in words rather than as a code. "P2" means nothing to the person who
    /// reported a fault, and a receipt is for them.
    /// </summary>
    public static string Describe(TicketPriority priority) => priority switch
    {
        TicketPriority.P1 => "P1, a critical fault",
        TicketPriority.P2 => "P2, a serious fault",
        TicketPriority.P3 => "P3, a fault with a workaround",
        TicketPriority.P4 => "P4, a minor fault or a question",
        _ => priority.ToString(),
    };

    /// <summary>The support hours, stated rather than named — "S1" means nothing either.</summary>
    public static string Describe(SupportWindow window) => window switch
    {
        SupportWindow.S1 => "working days 08:00–17:00",
        SupportWindow.S2 => "working days 05:00–22:00",
        SupportWindow.S3 => "every day 05:00–22:00",
        SupportWindow.S4 => "around the clock",
        _ => window.ToString(),
    };

    /// <summary>
    /// An instant as the reader's clock shows it. Everything is stored in UTC and the
    /// people reading this are in Stockholm; a receipt quoting UTC would have them
    /// arithmetic-ing their own deadline.
    /// </summary>
    private static string Stockholm(DateTime instant) =>
        BusinessCalendar.ToLocal(instant).ToString("dddd d MMMM HH:mm", CultureInfo.InvariantCulture);
}
