using System.Security.Claims;
using Bunit;
using EntKube.Web.Components.Layout;
using EntKube.Web.Data;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// Phase 9 (docs/helpdesk.md): the bell's sound — off until this browser switches it on, and one
/// chime when the count rises, none when it does not.
/// </summary>
public class NotificationBellRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly Guid tenant = Guid.NewGuid();
    private readonly BunitJSModuleInterop sound;

    public NotificationBellRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        SupportDutyService duty = new(factory);
        Services.AddSingleton(new TicketNotificationService(factory, duty, new OutOfOfficeService(factory, duty), new NoMail()));
        Services.AddSingleton<AuthenticationStateProvider>(new SignedIn());

        sound = JSInterop.SetupModule("./js/notify-sound.js");
        sound.Setup<bool>("enabled").SetResult(false);
        sound.Setup<bool>("chime").SetResult(true);
        sound.Setup<object>("setEnabled", _ => true).SetResult(null!);
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Unread()
    {
        db.UserNotifications.Add(new UserNotification
        {
            Id = Guid.NewGuid(), TenantId = tenant, UserId = "user-nils", Kind = NotificationKind.NewTicket, Text = "#1 New",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task The_sound_switch_is_off_until_this_browser_turns_it_on()
    {
        IRenderedComponent<NotificationBell> bell = Render<NotificationBell>();

        await bell.InvokeAsync(() => bell.Find("[data-test=notification-bell] button").Click());
        bell.Find("#bell-sound").HasAttribute("checked").Should().BeFalse();

        await bell.InvokeAsync(() => bell.Find("#bell-sound").Change(true));

        sound.Invocations["setEnabled"].Single().Arguments.Single().Should().Be(true);
    }

    [Fact]
    public async Task A_rising_count_chimes_once_and_a_steady_one_does_not()
    {
        IRenderedComponent<NotificationBell> bell = Render<NotificationBell>();

        Unread();
        await bell.InvokeAsync(() => bell.Instance.RefreshAsync());
        await bell.InvokeAsync(() => bell.Instance.RefreshAsync());

        sound.Invocations["chime"].Should().ContainSingle();
        bell.Find("[data-test=notification-bell] .badge").TextContent.Should().Be("1");
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "nils"), new Claim(ClaimTypes.NameIdentifier, "user-nils")], "test"))));
    }

    private sealed class NoMail : INotificationMailer
    {
        public Task<bool> SendAsync(Ticket ticket, string toEmail, string subject, string body, CancellationToken ct) => Task.FromResult(false);
    }
}
