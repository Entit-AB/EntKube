using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Admin "Reset &amp; disable MFA" must return a user to a clean, no-MFA state regardless of what
/// half-finished state they are in. The motivating bug: merely opening the "Enable authenticator"
/// page writes an authenticator-key token (ResetAuthenticatorKeyAsync) BEFORE the user verifies a
/// code, while TwoFactorEnabled only flips on success. That leaves the user's own 2FA page reading
/// "configured" (it keys off the key token) while the admin flag reads "off" — a stuck mismatch.
///
/// Exercised against a real UserManager over relational SQLite so the actual Identity token store
/// is used — this proves the internal token coordinates are right and that removal makes
/// GetAuthenticatorKeyAsync return null.
/// </summary>
public class MfaResetTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly UserManagementService svc;

    public MfaResetTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        context = new ApplicationDbContext(options);
        context.Database.EnsureCreated();

        IdentityOptions idOptions = new();
        idOptions.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // matches Program.cs; enables passkeys

        userManager = new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser>(context),
            Options.Create(idOptions),
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<UserManager<ApplicationUser>>.Instance);

        RoleManager<IdentityRole> roleManager = new(
            new RoleStore<IdentityRole>(context),
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);

        Mock<IPresenceTracker> presence = new();
        presence.Setup(p => p.IsOnlineAsync(It.IsAny<string>())).ReturnsAsync(false);
        presence.Setup(p => p.GetOnlineUsersAsync()).ReturnsAsync(new HashSet<string>());

        svc = new UserManagementService(
            userManager, roleManager, new TestDbContextFactory(connection), presence.Object);
    }

    [Fact]
    public async Task ResetAndDisableMfa_ClearsHalfFinishedAuthenticatorSetup()
    {
        ApplicationUser user = new() { UserName = "u@x.com", Email = "u@x.com" };
        (await userManager.CreateAsync(user)).Succeeded.Should().BeTrue();

        // Reproduce the abandoned setup: the key token gets written, but 2FA is never turned on.
        await userManager.ResetAuthenticatorKeyAsync(user);
        (await userManager.GetAuthenticatorKeyAsync(user)).Should().NotBeNull();
        (await userManager.GetTwoFactorEnabledAsync(user)).Should().BeFalse();

        (bool ok, string? error, int removed) = await svc.ResetAndDisableMfaAsync(user.Id);
        ok.Should().BeTrue();
        error.Should().BeNull();
        removed.Should().Be(0);

        // The leftover key is gone → the user's own page returns to a clean "add authenticator" state.
        (await userManager.GetAuthenticatorKeyAsync(user)).Should().BeNull();
        (await userManager.GetTwoFactorEnabledAsync(user)).Should().BeFalse();
    }

    [Fact]
    public void MfaSetupIncomplete_FlagsTheHalfFinishedState()
    {
        ApplicationUser u = new() { UserName = "x", Email = "x" };
        // Key present but 2FA off → the confusing "admin says off, user page says configured" state.
        new UserSecurityInfo(u, IsOnline: false, TwoFactorEnabled: false, PasskeyCount: 0, HasAuthenticator: true)
            .MfaSetupIncomplete.Should().BeTrue();
        // Fully enabled → not "incomplete".
        new UserSecurityInfo(u, false, TwoFactorEnabled: true, 0, HasAuthenticator: true)
            .MfaSetupIncomplete.Should().BeFalse();
        // Nothing configured → not "incomplete".
        new UserSecurityInfo(u, false, false, 0, HasAuthenticator: false)
            .MfaSetupIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task ResetAndDisableMfa_ClearsFullyEnabledTwoFactorAndRecoveryCodes()
    {
        ApplicationUser user = new() { UserName = "e@x.com", Email = "e@x.com" };
        await userManager.CreateAsync(user);
        await userManager.ResetAuthenticatorKeyAsync(user);
        await userManager.SetTwoFactorEnabledAsync(user, true);
        await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 5);
        (await userManager.CountRecoveryCodesAsync(user)).Should().Be(5);

        (bool ok, _, _) = await svc.ResetAndDisableMfaAsync(user.Id);
        ok.Should().BeTrue();

        (await userManager.GetTwoFactorEnabledAsync(user)).Should().BeFalse();
        (await userManager.GetAuthenticatorKeyAsync(user)).Should().BeNull();
        (await userManager.CountRecoveryCodesAsync(user)).Should().Be(0);
    }

    [Fact]
    public async Task ResetAndDisableMfa_IsIdempotent_WhenNothingConfigured()
    {
        ApplicationUser user = new() { UserName = "n@x.com", Email = "n@x.com" };
        await userManager.CreateAsync(user);

        (bool ok, string? error, int removed) = await svc.ResetAndDisableMfaAsync(user.Id);
        ok.Should().BeTrue();
        error.Should().BeNull();
        removed.Should().Be(0);
        (await userManager.GetAuthenticatorKeyAsync(user)).Should().BeNull();
    }

    [Fact]
    public async Task ResetAndDisableMfa_ReturnsFalse_WhenUserMissing()
    {
        (bool ok, string? error, int _) = await svc.ResetAndDisableMfaAsync(Guid.NewGuid().ToString());
        ok.Should().BeFalse();
        error.Should().Be("User not found.");
    }

    public void Dispose()
    {
        userManager.Dispose();
        context.Dispose();
        connection.Dispose();
    }
}
