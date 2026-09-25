using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Support;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether the jour §10.1.1 sells is actually staffed, and whether the people covering it
/// are allowed to be there under §18.
///
/// <para>S4's window fee is 95 000 kr a month for availability rather than output, so a
/// hole in the roster is the customer paying for something that is not there. Finding it
/// when an incident lands in it is the expensive way.</para>
/// </summary>
public class OnCallCoverageTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private static DateTime Wed(int hour) => Swedish(2026, 9, 23, hour);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly OnCallService onCall;

    private readonly Guid tenantId = Guid.NewGuid();

    public OnCallCoverageTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.SaveChanges();

        onCall = new OnCallService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddSchedule(SupportWindow? covers, params (DateTime From, DateTime To)[] shifts)
    {
        OnCallSchedule schedule = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"Jour {Guid.NewGuid():N}"[..12],
            Covers = covers,
        };

        foreach ((DateTime from, DateTime to) in shifts)
        {
            schedule.Shifts.Add(new OnCallShift
            {
                Id = Guid.NewGuid(),
                ScheduleId = schedule.Id,
                AssigneeName = "Jourhavande",
                StartsAt = from,
                EndsAt = to,
            });
        }

        db.OnCallSchedules.Add(schedule);
        db.SaveChanges();

        return schedule.Id;
    }

    // ---- Coverage ------------------------------------------------------------------------

    [Fact]
    public async Task A_fully_covered_window_has_no_gaps()
    {
        Guid schedule = AddSchedule(SupportWindow.S1, (Tue(8), Tue(17)));

        List<CoverageGap> gaps = await onCall.GetCoverageGapsAsync(schedule, Tue(0), Wed(0));

        gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unstaffed_afternoon_is_a_gap()
    {
        Guid schedule = AddSchedule(SupportWindow.S1, (Tue(8), Tue(12)));

        List<CoverageGap> gaps = await onCall.GetCoverageGapsAsync(schedule, Tue(0), Wed(0));

        gaps.Should().ContainSingle();
        gaps[0].From.Should().Be(Tue(12));
        gaps[0].To.Should().Be(Tue(17));
        gaps[0].Duration.Should().Be(TimeSpan.FromHours(5));
    }

    [Fact]
    public async Task A_hole_in_the_middle_is_a_gap()
    {
        Guid schedule = AddSchedule(SupportWindow.S1, (Tue(8), Tue(11)), (Tue(13), Tue(17)));

        List<CoverageGap> gaps = await onCall.GetCoverageGapsAsync(schedule, Tue(0), Wed(0));

        gaps.Should().ContainSingle();
        gaps[0].From.Should().Be(Tue(11));
        gaps[0].To.Should().Be(Tue(13));
    }

    /// <summary>
    /// Time outside the window was never promised, so a shift ending at 17:00 leaves no gap
    /// under S1 however much of the evening is unstaffed.
    /// </summary>
    [Fact]
    public async Task Time_outside_the_window_is_not_a_gap()
    {
        Guid schedule = AddSchedule(SupportWindow.S1, (Tue(8), Tue(17)));

        List<CoverageGap> gaps = await onCall.GetCoverageGapsAsync(schedule, Tue(0), Swedish(2026, 9, 22, 23));

        gaps.Should().BeEmpty();
    }

    /// <summary>
    /// The same roster against S4 leaves the whole night uncovered, which is exactly the
    /// difference the customer is paying 95 000 kr a month for.
    /// </summary>
    [Fact]
    public async Task The_same_roster_under_S4_leaves_the_night_open()
    {
        Guid schedule = AddSchedule(SupportWindow.S4, (Tue(8), Tue(17)));

        List<CoverageGap> gaps = await onCall.GetCoverageGapsAsync(schedule, Tue(0), Wed(0));

        gaps.Should().HaveCount(2);
        gaps[0].From.Should().Be(Tue(0));
        gaps[0].To.Should().Be(Tue(8));
        gaps[1].From.Should().Be(Tue(17));
        gaps[1].To.Should().Be(Wed(0));
    }

    [Fact]
    public async Task Overlapping_shifts_cover_between_them()
    {
        Guid schedule = AddSchedule(SupportWindow.S1, (Tue(8), Tue(13)), (Tue(12), Tue(17)));

        List<CoverageGap> gaps = await onCall.GetCoverageGapsAsync(schedule, Tue(0), Wed(0));

        gaps.Should().BeEmpty();
    }

    /// <summary>
    /// A weekend under S1 is closed, so nothing is owed and nothing is missing. Under S3 it
    /// is open every day, and an empty weekend shows up.
    /// </summary>
    [Fact]
    public async Task A_closed_weekend_owes_nothing_but_an_open_one_does()
    {
        Guid underS1 = AddSchedule(SupportWindow.S1);
        Guid underS3 = AddSchedule(SupportWindow.S3);

        DateTime saturday = Swedish(2026, 9, 26, 0);
        DateTime sunday = Swedish(2026, 9, 27, 0);

        (await onCall.GetCoverageGapsAsync(underS1, saturday, sunday)).Should().BeEmpty();

        List<CoverageGap> s3 = await onCall.GetCoverageGapsAsync(underS3, saturday, sunday);
        s3.Should().ContainSingle();
        s3[0].Duration.Should().Be(TimeSpan.FromHours(17));
    }

    /// <summary>A schedule not tied to a window has nothing to be measured against.</summary>
    [Fact]
    public async Task A_schedule_with_no_window_reports_nothing()
    {
        Guid schedule = AddSchedule(covers: null);

        List<CoverageGap> gaps = await onCall.GetCoverageGapsAsync(schedule, Tue(0), Wed(0));

        gaps.Should().BeEmpty();
    }

    // ---- Who is on call -------------------------------------------------------------------

    [Fact]
    public async Task On_call_now_finds_the_covering_shift()
    {
        AddSchedule(SupportWindow.S1, (Tue(8), Tue(17)));

        List<OnCallShift> onNow = await onCall.WhoIsOnCallAsync(tenantId, Tue(12));

        onNow.Should().ContainSingle();
        onNow[0].AssigneeName.Should().Be("Jourhavande");
    }

    [Fact]
    public async Task A_disabled_schedule_is_not_on_call()
    {
        Guid scheduleId = AddSchedule(SupportWindow.S1, (Tue(8), Tue(17)));
        OnCallSchedule schedule = db.OnCallSchedules.Single(s => s.Id == scheduleId);
        schedule.IsEnabled = false;
        await db.SaveChangesAsync();

        List<OnCallShift> onNow = await onCall.WhoIsOnCallAsync(tenantId, Tue(12));

        onNow.Should().BeEmpty();
    }

    // ---- The §18 register -------------------------------------------------------------------

    /// <summary>
    /// §18 gives the customer ten working days to object, after which the subconsultant is
    /// approved. Ten working days from a Tuesday is the Tuesday a fortnight later.
    /// </summary>
    [Fact]
    public void The_objection_deadline_is_ten_working_days_after_the_notice()
    {
        Subconsultant consultant = new()
        {
            TenantId = tenantId, Name = "Partner AB", NotifiedAt = Tue(9),
        };

        OnCallService.ObjectionDeadline(consultant)
            .Should().Be(Swedish(2026, 10, 6, 17));
    }

    [Fact]
    public void Silence_past_the_deadline_is_approval()
    {
        Subconsultant consultant = new()
        {
            TenantId = tenantId, Name = "Partner AB", NotifiedAt = Tue(9),
        };

        OnCallService.ApprovalStatus(consultant, Swedish(2026, 10, 7, 9))
            .Should().Be(SubconsultantApproval.Approved);
    }

    [Fact]
    public void Before_the_deadline_they_are_still_awaiting_a_response()
    {
        Subconsultant consultant = new()
        {
            TenantId = tenantId, Name = "Partner AB", NotifiedAt = Tue(9),
        };

        OnCallService.ApprovalStatus(consultant, Swedish(2026, 9, 25, 9))
            .Should().Be(SubconsultantApproval.AwaitingResponse);
    }

    [Fact]
    public void An_objection_stands_however_long_ago_the_notice_was()
    {
        Subconsultant consultant = new()
        {
            TenantId = tenantId, Name = "Partner AB",
            NotifiedAt = Tue(9), ObjectedAt = Wed(9),
        };

        OnCallService.ApprovalStatus(consultant, Swedish(2026, 12, 1, 9))
            .Should().Be(SubconsultantApproval.Objected);
    }

    [Fact]
    public void Somebody_never_notified_is_not_approved_by_the_passage_of_time()
    {
        Subconsultant consultant = new() { TenantId = tenantId, Name = "Partner AB" };

        OnCallService.ApprovalStatus(consultant, Swedish(2027, 1, 1, 9))
            .Should().Be(SubconsultantApproval.NotNotified);
        OnCallService.ObjectionDeadline(consultant).Should().BeNull();
    }

    /// <summary>
    /// §18 puts confidentiality and data-processing terms <em>before</em> access, and §24
    /// requires the individual undertaking. Approval alone is not enough.
    /// </summary>
    [Fact]
    public void Access_needs_approval_and_both_undertakings()
    {
        Subconsultant approvedOnly = new()
        {
            TenantId = tenantId, Name = "Partner AB", ApprovedAt = Tue(9),
        };

        Subconsultant ready = new()
        {
            TenantId = tenantId, Name = "Partner AB",
            ApprovedAt = Tue(9),
            ConfidentialitySignedAt = Tue(9),
            DataProcessingBoundAt = Tue(9),
        };

        OnCallService.MayAccessCustomerEnvironments(approvedOnly, Wed(9)).Should().BeFalse();
        OnCallService.MayAccessCustomerEnvironments(ready, Wed(9)).Should().BeTrue();
    }

    [Fact]
    public void An_inactive_subconsultant_may_not_access_anything()
    {
        Subconsultant retired = new()
        {
            TenantId = tenantId, Name = "Partner AB",
            ApprovedAt = Tue(9),
            ConfidentialitySignedAt = Tue(9),
            DataProcessingBoundAt = Tue(9),
            IsActive = false,
        };

        OnCallService.MayAccessCustomerEnvironments(retired, Wed(9)).Should().BeFalse();
    }

    [Fact]
    public async Task The_register_is_readable_as_the_agreement_requires()
    {
        await onCall.SaveSubconsultantAsync(new Subconsultant
        {
            TenantId = tenantId, Name = "Anna Andersson", Company = "Partner AB",
        });

        List<Subconsultant> register = await onCall.GetSubconsultantsAsync(tenantId);

        register.Should().ContainSingle().Which.Company.Should().Be("Partner AB");
    }

    // ---- Putting somebody on the roster -------------------------------------------------

    /// <summary>
    /// The roster carried a name and an address, while the entity's own comment observed
    /// that at three in the morning an address is not a way to reach a person. It was
    /// right, and there was no way to record anything else — AddShiftAsync had no
    /// parameter for a phone number.
    /// </summary>
    [Fact]
    public async Task A_shift_can_carry_a_way_to_actually_reach_somebody()
    {
        Guid scheduleId = await AddSchedule();

        await onCall.AddShiftAsync(
            scheduleId, "Alice Smith", "alice@entit.se",
            Tue(8), Tue(20), notes: null,
            assigneePhone: "+46 70 123 45 67",
            assigneeTeamsHandle: "alice@entit.se");

        OnCallShift shift = db.OnCallShifts.Single();

        shift.AssigneePhone.Should().Be("+46 70 123 45 67");
        shift.AssigneeTeamsHandle.Should().Be("alice@entit.se");
    }

    /// <summary>
    /// §18 allows subconsultants for on-call duty, and SubconsultantId existed to say
    /// which one — with nothing able to set it, because the register had no door and the
    /// shift had no field.
    /// </summary>
    [Fact]
    public async Task A_shift_can_name_the_subconsultant_covering_it()
    {
        Guid scheduleId = await AddSchedule();
        Subconsultant person = await Registered(cleared: true);

        await onCall.AddShiftAsync(
            scheduleId, person.Name, person.Email,
            Tue(8), Tue(20), notes: null,
            affiliation: OnCallAffiliation.Subconsultant,
            subconsultantId: person.Id);

        OnCallShift shift = db.OnCallShifts.Single();

        shift.SubconsultantId.Should().Be(person.Id);
        shift.Affiliation.Should().Be(OnCallAffiliation.Subconsultant);
    }

    /// <summary>
    /// <b>The gate.</b> §18 wants the §17 undertakings in place and the customer's approval
    /// <em>before</em> somebody goes near the environment, and on-call duty is going near
    /// it. Refused rather than warned about, because a roster is consulted at three in the
    /// morning by somebody who will not be re-reading the register.
    /// </summary>
    [Fact]
    public async Task Somebody_the_agreement_does_not_yet_allow_cannot_be_rostered()
    {
        Guid scheduleId = await AddSchedule();
        Subconsultant person = await Registered(cleared: false);

        Func<Task> roster = () => onCall.AddShiftAsync(
            scheduleId, person.Name, person.Email,
            Tue(8), Tue(20), notes: null,
            affiliation: OnCallAffiliation.Subconsultant,
            subconsultantId: person.Id);

        await roster.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*§18*");

        db.OnCallShifts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_subconsultant_who_is_not_registered_at_all_is_refused()
    {
        Guid scheduleId = await AddSchedule();

        Func<Task> roster = () => onCall.AddShiftAsync(
            scheduleId, "Somebody", null, Tue(8), Tue(20), notes: null,
            affiliation: OnCallAffiliation.Subconsultant,
            subconsultantId: Guid.NewGuid());

        await roster.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Our own staff need no §18 clearance, and the gate must not ask for one.</summary>
    [Fact]
    public async Task Our_own_staff_are_rostered_without_a_register_entry()
    {
        Guid scheduleId = await AddSchedule();

        await onCall.AddShiftAsync(
            scheduleId, "Alice Smith", "alice@entit.se", Tue(8), Tue(20), notes: null);

        db.OnCallShifts.Single().Affiliation.Should().Be(OnCallAffiliation.Employee);
    }

    /// <summary>
    /// The field and the row that displays it both existed from the start, and nothing
    /// could write to it. A handover note is the one thing the next person on call
    /// actually needs and the one thing a rota cannot infer.
    /// </summary>
    [Fact]
    public async Task A_shift_can_be_handed_over_with_a_note()
    {
        Guid scheduleId = await AddSchedule();

        await onCall.AddShiftAsync(
            scheduleId, "Alice Smith", "alice@entit.se", Tue(8), Tue(20), notes: null);

        Guid shiftId = db.OnCallShifts.Single().Id;

        await onCall.RecordHandoverAsync(shiftId, "  Ticket #41 still live; supplier rung at 02:00.  ");

        db.ChangeTracker.Clear();

        db.OnCallShifts.Single().HandoverNotes
            .Should().Be("Ticket #41 still live; supplier rung at 02:00.");
    }

    /// <summary>Clearing it is a real act: the thing that was running hot no longer is.</summary>
    [Fact]
    public async Task A_handover_note_can_be_cleared()
    {
        Guid scheduleId = await AddSchedule();

        await onCall.AddShiftAsync(
            scheduleId, "Alice Smith", null, Tue(8), Tue(20), notes: null);

        Guid shiftId = db.OnCallShifts.Single().Id;

        await onCall.RecordHandoverAsync(shiftId, "Something.");
        await onCall.RecordHandoverAsync(shiftId, "   ");

        db.ChangeTracker.Clear();

        db.OnCallShifts.Single().HandoverNotes.Should().BeNull();
    }

    /// <summary>A shift that has gone must not take the call down with it.</summary>
    [Fact]
    public async Task Handing_over_a_shift_that_is_not_there_does_nothing()
    {
        Func<Task> handover = () => onCall.RecordHandoverAsync(Guid.NewGuid(), "Anything.");

        await handover.Should().NotThrowAsync();
    }

    private async Task<Guid> AddSchedule()
    {
        Guid id = Guid.NewGuid();

        db.OnCallSchedules.Add(new OnCallSchedule
        {
            Id = id, TenantId = tenantId, Name = "Primary", IsEnabled = true,
        });
        await db.SaveChangesAsync();

        return id;
    }

    private async Task<Subconsultant> Registered(bool cleared)
    {
        Subconsultant person = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Anna Berg",
            Email = "anna@partner.example",
            ConfidentialitySignedAt = cleared ? Tue(0).AddYears(-1) : null,
            DataProcessingBoundAt = cleared ? Tue(0).AddYears(-1) : null,
            NotifiedAt = cleared ? Tue(0).AddMonths(-2) : null,
            ApprovedAt = cleared ? Tue(0).AddMonths(-2) : null,
        };

        db.Subconsultants.Add(person);
        await db.SaveChangesAsync();

        return person;
    }
}
