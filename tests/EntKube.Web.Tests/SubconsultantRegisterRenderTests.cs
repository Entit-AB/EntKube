using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Support;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EntKube.Web.Tests;

/// <summary>
/// The §18 register, rendered.
///
/// <para>Every rule behind this screen was written and tested when the subsystem was —
/// tacit approval after ten working days, the two §17 undertakings, the check on whether
/// somebody may go near a customer's environment. None of it was reachable: there was no
/// way to register a person, no way to record that the customer had been told, and the
/// list §18 says must be available on request could only ever be empty.</para>
///
/// <para>So these tests are about the door rather than the rules.</para>
/// </summary>
public class SubconsultantRegisterRenderTests : BunitContext, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly Guid tenantId = Guid.NewGuid();

    public SubconsultantRegisterRenderTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);

        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(factory);
        Services.AddSingleton(new OnCallService(factory));
        Services.AddSingleton(new ToastService());
    }

    public new void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private IRenderedComponent<SubconsultantRegister> RenderRegister() =>
        Render<SubconsultantRegister>(p => p.Add(c => c.TenantId, tenantId));

    private Subconsultant Add(
        string name,
        DateTime? notified = null,
        DateTime? approved = null,
        DateTime? objected = null,
        bool bound = true)
    {
        Subconsultant person = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Company = "Konsult AB",
            NotifiedAt = notified,
            ApprovedAt = approved,
            ObjectedAt = objected,
            ConfidentialitySignedAt = bound ? DateTime.UtcNow.AddYears(-1) : null,
            DataProcessingBoundAt = bound ? DateTime.UtcNow.AddYears(-1) : null,
        };

        db.Subconsultants.Add(person);
        db.SaveChanges();

        return person;
    }

    // ---- The list §18 says must exist ---------------------------------------------------

    /// <summary>
    /// An empty register is a legitimate answer to "who else works on this" — as long as
    /// it is true, which it could not be while there was no way to add anybody.
    /// </summary>
    [Fact]
    public void An_empty_register_says_so() =>
        RenderRegister().Markup.Should().Contain("Nobody registered");

    [Fact]
    public void Somebody_registered_appears_with_their_company()
    {
        Add("Anna Berg");

        string markup = RenderRegister().Markup;

        markup.Should().Contain("Anna Berg").And.Contain("Konsult AB");
    }

    // ---- What §18 actually asks ----------------------------------------------------------

    /// <summary>
    /// Approval is tacit, and the date it becomes so is the useful thing to show — it is
    /// counted in working days on the §9 calendar, which nobody works out in their head.
    /// </summary>
    [Fact]
    public void Somebody_awaiting_the_customer_shows_when_they_count_as_approved()
    {
        Add("Anna Berg", notified: DateTime.UtcNow);

        string markup = RenderRegister().Markup;

        markup.Should().Contain("awaiting the customer");
        markup.Should().Contain("approved by default on");
    }

    [Fact]
    public void Somebody_the_customer_approved_says_so()
    {
        Add("Anna Berg", notified: DateTime.UtcNow.AddDays(-1), approved: DateTime.UtcNow);

        RenderRegister().Markup.Should().Contain("approved");
    }

    /// <summary>
    /// §18 says approval may not be unreasonably withheld, so an objection is worth
    /// recording rather than merely acting on.
    /// </summary>
    [Fact]
    public void An_objection_is_shown_as_one()
    {
        Add("Anna Berg", notified: DateTime.UtcNow.AddDays(-1), objected: DateTime.UtcNow);

        RenderRegister().Markup.Should().Contain("objected to");
    }

    // ---- The gate that matters -------------------------------------------------------------

    /// <summary>
    /// <b>The point of the register.</b> §18 wants the §17 undertakings in place
    /// <em>before</em> anybody touches a customer's environment, and the screen says which
    /// specific thing is missing rather than merely refusing.
    /// </summary>
    [Fact]
    public void Somebody_without_the_terms_in_place_is_named_and_the_reason_given()
    {
        Add("Anna Berg", notified: DateTime.UtcNow.AddDays(-30), bound: false);

        string markup = RenderRegister().Markup;

        markup.Should().Contain("Not yet allowed into a customer's environments");
        markup.Should().Contain("no confidentiality undertaking");
        markup.Should().Contain("not bound by §17 data-processing terms");
    }

    /// <summary>
    /// Bound and approved is the only combination that passes, and it is shown as a plain
    /// yes so nobody has to reason about three columns.
    /// </summary>
    [Fact]
    public void Somebody_bound_and_approved_may_work()
    {
        Add("Anna Berg", notified: DateTime.UtcNow.AddDays(-30), approved: DateTime.UtcNow.AddDays(-20));

        IRenderedComponent<SubconsultantRegister> register = RenderRegister();

        register.Markup.Should().NotContain("Not yet allowed into a customer's environments");
        register.Markup.Should().Contain(">yes<");
    }

    // ---- Putting somebody in -----------------------------------------------------------------

    /// <summary>
    /// The whole gap: the rules existed and nothing could reach them. Adding somebody has
    /// to work from this screen or the register stays theoretical.
    /// </summary>
    [Fact]
    public async Task Somebody_can_be_added_from_the_screen()
    {
        IRenderedComponent<SubconsultantRegister> register = RenderRegister();

        await register.InvokeAsync(() => register.FindAll("button")
            .First(b => b.TextContent.Trim().StartsWith("Add", StringComparison.Ordinal)).Click());

        register.FindAll("input").First().Change("Anna Berg");

        await register.InvokeAsync(() => register.FindAll("button")
            .First(b => b.TextContent.Trim() == "Save").Click());

        db.ChangeTracker.Clear();
        db.Subconsultants.Should().ContainSingle().Which.Name.Should().Be("Anna Berg");
    }

    /// <summary>
    /// Recording that the customer was told is what starts §18's ten working days, so it
    /// is a button rather than a date somebody has to know to fill in.
    /// </summary>
    [Fact]
    public async Task Telling_the_customer_starts_the_objection_period()
    {
        Subconsultant person = Add("Anna Berg");

        IRenderedComponent<SubconsultantRegister> register = RenderRegister();

        await register.InvokeAsync(() => register.FindAll("button")
            .First(b => b.TextContent.Contains("Notified the customer", StringComparison.Ordinal))
            .Click());

        db.ChangeTracker.Clear();

        Subconsultant saved = db.Subconsultants.Single(s => s.Id == person.Id);

        saved.NotifiedAt.Should().NotBeNull();
        OnCallService.ApprovalStatus(saved, DateTime.UtcNow)
            .Should().Be(SubconsultantApproval.AwaitingResponse);
        OnCallService.ObjectionDeadline(saved).Should().NotBeNull();
    }
}
