using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// A notifier for tests that are not about notification.
///
/// <para><see cref="TicketService"/> takes one because telling somebody has to be part of
/// opening a ticket rather than something three call sites remember — so tests that only
/// care about clocks or pricing need a stand-in. This one is real, and silent for the
/// reason it would be silent in production with nothing configured: there is no SMTP host,
/// so there is nowhere to send.</para>
///
/// <para>Deliberately not a mock. Using the real class means a test suite full of these
/// still exercises the path up to the point where mail would leave, which is where the
/// interesting failures are — a null reference building the message, say.</para>
/// </summary>
internal static class SilentTicketNotifier
{
    public static TicketNotifier For(TestDbContextFactory factory)
    {
        IConfiguration nothingConfigured =
            new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        return new TicketNotifier(
            factory,
            new SmtpSettingsResolver(factory, nothingConfigured),
            new OnCallService(factory),
            NullLogger<TicketNotifier>.Instance);
    }
}
