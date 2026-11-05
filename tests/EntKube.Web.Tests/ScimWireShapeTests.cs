using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using EntKube.Web.Services.Scim;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The SCIM wire shape, pinned BEFORE anyone types these routes.
///
/// <para><b>Why this file exists ahead of the change it guards.</b> The seven SCIM operations are
/// the only part of the API that does not serialise an object: every payload is a hand-built
/// <see cref="JsonObject"/> with literal keys, flattened with <c>ToJsonString()</c> and written
/// through <c>Results.Content(json, "application/scim+json", …)</c>. That is why they do not
/// register in the <c>UndescribedSuccessBodies</c> ratchet, and it is also why they are the most
/// dangerous routes on the list to type: the work that drives that ratchet to 0
/// (<c>docs/decomposition.md</c> item 8) is transcription, and transcribing these particular keys
/// into records breaks them in ways that answer <c>200 OK</c>.</para>
///
/// <para>⚠️ <b>The hazard that motivated the file: capital-R <c>Resources</c>.</b> RFC 7644 §3.4.2
/// names the ListResponse member <c>Resources</c> — the one member in the whole SCIM specification
/// that is not lowerCamelCase. A record property <c>Resources</c> serialised with
/// <c>JsonSerializerDefaults.Web</c> arrives as <c>resources</c>, which is not a rename a reviewer
/// would even see, and every connector then reads an empty page: Entra concludes the directory it
/// is syncing into contains nobody, and starts creating duplicates of people who already exist.
/// <see cref="A_record_property_named_Resources_camelCases_and_that_is_the_whole_hazard"/> proves
/// the mechanism rather than reasoning about it.</para>
///
/// <para>⚠️ <b>What is NOT protected here, stated plainly rather than implied.</b> Three things in
/// this subsystem cannot be observed from a test without an HTTP host, and this file does not
/// pretend otherwise:</para>
/// <list type="bullet">
///   <item><description>the <b>content type</b> actually written on the wire. <c>Respond</c> is
///   private and <c>Results.Content</c>'s content type is not readable off the
///   <see cref="IResult"/> without executing it, so <see cref="ScimSourceShapeTests"/> pins it as
///   SOURCE TEXT — including the absence of <c>TypedResults</c>, which is the specific mechanism
///   that would silently turn <c>application/scim+json</c> into <c>application/json</c>;</description></item>
///   <item><description>the <b>ServiceProviderConfig body</b>, which is built inline in the
///   endpoint lambda in <c>ScimEndpoints.MapScim</c>. There is no seam to call, so it too is only
///   source-pinned. ⚠️ <b>A source scan proves the code was written, never that it runs</b> —
///   making this a real wire assertion means extracting a
///   <c>public static JsonObject ServiceProviderConfigBody()</c>, exactly as
///   <c>ErrorBody</c> was already extracted from <c>Respond</c> for the same reason;</description></item>
///   <item><description>the <b>HTTP status codes and routes</b>. <c>ScimResult.Status</c> is
///   asserted below where it is reachable, but nothing here proves the mapping reached the right
///   verb or that <c>201</c> left the server as <c>201</c>.</description></item>
/// </list>
///
/// <para>The ListResponse envelope itself IS reachable without HTTP, and is exercised for real
/// below: <c>ListAsync</c> needs an <c>IDbContextFactory&lt;IdentityDbContext&gt;</c> and a
/// <c>UserManager</c>, and <see cref="ScimListResponseWireShapeTests"/> supplies both over
/// relational SQLite the way <c>MfaResetTests</c> does. It would have been easy to "pin" the
/// envelope by building a JsonObject with the same literals in the test and asserting against it;
/// that asserts nothing at all, and is worse than no test, because it would stay green through the
/// rename.</para>
///
/// <para>Key ORDER is deliberately not pinned. For the public API records it is part of the
/// contract, because existing clients see the field order the anonymous objects emitted; SCIM
/// connectors parse strictly by name, so pinning order here would only produce failures that are
/// not defects.</para>
/// </summary>
public class ScimUserResourceWireShapeTests
{
    private static ApplicationUser User() => new()
    {
        Id = "user-1",
        UserName = "someone@example.com",
        Email = "someone@example.com",
    };

    /// <summary>Exactly what minimal APIs serialise with when nothing is configured.</summary>
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    private sealed record ListResponseAsARecord(
        IReadOnlyList<string> Schemas,
        int TotalResults,
        int StartIndex,
        int ItemsPerPage,
        IReadOnlyList<object> Resources);

    /// <summary>
    /// ⚠️ The premise of this whole file, demonstrated rather than asserted from memory. This is
    /// not a test of EntKube code: it is a test of what <c>JsonSerializerDefaults.Web</c> does to
    /// the one SCIM member that is not lowerCamelCase, so that nobody has to take the claim on
    /// trust while deciding whether typing these routes is safe. If this ever goes green the other
    /// way, the hazard is gone and the pins below can be relaxed — which is the only circumstance
    /// in which they should be.
    /// </summary>
    [Fact]
    public void A_record_property_named_Resources_camelCases_and_that_is_the_whole_hazard()
    {
        // Named arguments, as the house rule for these shapes requires: three adjacent ints is
        // exactly the signature a positional call transposes without the compiler noticing.
        string json = JsonSerializer.Serialize(
            new ListResponseAsARecord(
                Schemas: [ScimUserService.ListSchema],
                TotalResults: 1,
                StartIndex: 1,
                ItemsPerPage: 1,
                Resources: []),
            AsTheApiSerialises);

        json.Should().Contain("\"resources\":",
            "this is what a typed ListResponse would put on the wire");
        json.Should().NotContain("\"Resources\":",
            "and RFC 7644 §3.4.2 requires exactly this spelling, so every connector would read an "
            + "empty page while the endpoint answered 200 OK");
    }

    /// <summary>
    /// The user resource as every one of GET, POST, PUT and PATCH returns it — they all answer
    /// through <see cref="ScimUserService.ToScim"/>, so this single shape is the body of four
    /// operations.
    /// </summary>
    [Fact]
    public void The_user_resource_carries_exactly_these_members()
    {
        JsonObject resource = ScimUserService.ToScim(User());

        // ⚠️ The exact SET, not "contains". A typed record emits every member it declares,
        // including the ones left null — so a transcription that adds `externalId`, `name` or
        // `displayName` for completeness ships `"externalId":null`, and connectors that treat a
        // present-but-null attribute as "the directory cleared this" then clear it upstream.
        resource.Select(member => member.Key).Should().BeEquivalentTo(
            ["schemas", "id", "userName", "active", "emails", "meta"]);
    }

    /// <summary>
    /// ⚠️ <c>schemas</c> is an ARRAY in all three SCIM envelopes. A record declaring
    /// <c>string Schemas</c> — the obvious shape, since this service only ever emits one URN —
    /// serialises a bare string, and a connector doing a strict parse of a multi-valued member
    /// rejects the resource outright instead of reading one user wrong.
    /// </summary>
    [Fact]
    public void The_schemas_member_is_an_array_of_the_core_user_urn()
    {
        JsonObject resource = ScimUserService.ToScim(User());

        resource["schemas"]!.GetValueKind().Should().Be(JsonValueKind.Array);
        resource["schemas"]!.AsArray().Should().HaveCount(1);
        resource["schemas"]!.AsArray()[0]!.GetValue<string>()
            .Should().Be("urn:ietf:params:scim:schemas:core:2.0:User");
    }

    /// <summary>
    /// ⚠️ <c>active</c> is a JSON BOOLEAN, and it is the member deprovisioning is decided by. This
    /// service reads an INCOMING <c>"False"</c> string tolerantly (see <c>ScimUserService.ReadBool</c>,
    /// which exists because some Entra connector versions send one) — that tolerance must never
    /// become an excuse to EMIT one, because the directory compares what it reads here against what
    /// it last pushed and a <c>"true"</c> string is not <c>true</c>.
    /// </summary>
    [Fact]
    public void Active_is_a_boolean_not_a_string()
    {
        ScimUserService.ToScim(User())["active"]!.GetValueKind()
            .Should().Be(JsonValueKind.True);
    }

    /// <summary>
    /// ⚠️ <c>emails</c> is a multi-valued attribute of objects even though this service stores one
    /// address. The tempting typing is <c>string Email</c>, which flattens
    /// <c>"emails":[{"value":…,"primary":true}]</c> to <c>"email":"…"</c> — a member no SCIM client
    /// looks for, so the address silently stops arriving while provisioning keeps reporting success.
    /// <c>primary</c> matters too: a connector picking the primary entry from a list where nothing
    /// is marked primary picks nothing.
    /// </summary>
    [Fact]
    public void Emails_is_a_list_of_objects_with_a_primary_flag()
    {
        JsonObject resource = ScimUserService.ToScim(User());

        resource["emails"]!.GetValueKind().Should().Be(JsonValueKind.Array);
        JsonObject first = resource["emails"]!.AsArray()[0]!.AsObject();

        first.Select(member => member.Key).Should().BeEquivalentTo(["value", "primary"]);
        first["value"]!.GetValue<string>().Should().Be("someone@example.com");
        first["primary"]!.GetValueKind().Should().Be(JsonValueKind.True);
    }

    /// <summary>
    /// ⚠️ <c>meta</c> is a nested object, and <c>meta.location</c> is the URL a connector follows
    /// to re-read or patch the user. Typing this as two flat members (<c>resourceType</c>,
    /// <c>location</c>) keeps both names and still breaks every connector, because they read
    /// <c>meta.location</c> by path. The <c>/scim/v2/Users/{id}</c> prefix is pinned with it: this
    /// is the only place the service tells the directory where the resource lives, so a wrong
    /// prefix makes every follow-up request a 404 against a user that exists.
    /// </summary>
    [Fact]
    public void Meta_is_nested_and_locates_the_resource()
    {
        JsonObject meta = ScimUserService.ToScim(User())["meta"]!.AsObject();

        meta.Select(member => member.Key).Should().BeEquivalentTo(["resourceType", "location"]);
        meta["resourceType"]!.GetValue<string>().Should().Be("User");
        meta["location"]!.GetValue<string>().Should().Be("/scim/v2/Users/user-1");
    }

    /// <summary>
    /// ⚠️ The serialised text, not the node tree, because the text is what a connector receives.
    /// <c>userName</c> is the handle the directory matches people by — it is also this service's
    /// stand-in for <c>externalId</c> (see <c>ListAsync</c>) — and the camelCase policy happens to
    /// agree with it, which is exactly the kind of agreement that is reasoned about and got wrong
    /// for a name like <c>UserName</c>.
    /// </summary>
    [Fact]
    public void The_serialised_user_resource_spells_userName_this_way()
    {
        string json = ScimUserService.ToScim(User()).ToJsonString();

        json.Should().Contain("\"userName\":\"someone@example.com\"");
        json.Should().NotContain("\"username\":");
        json.Should().NotContain("\"UserName\":");
    }
}

/// <summary>
/// The ListResponse envelope — the shape with the capital-R member, and the only payload in this
/// subsystem that needs a database to produce.
///
/// <para>It is produced here for real: <c>ScimUserService.ListAsync</c> takes an
/// <c>IDbContextFactory&lt;IdentityDbContext&gt;</c> and a <c>UserManager</c>, and both are
/// available to a test over relational SQLite — <c>TestDbContextFactory</c> already serves
/// <c>IdentityDbContext</c>, and <c>MfaResetTests</c> already builds a real <c>UserManager</c> over
/// an in-memory connection. Users are seeded THROUGH that <c>UserManager</c> rather than inserted,
/// so the normalised columns Identity relies on are populated the way production populates
/// them.</para>
///
/// <para>⚠️ The <c>UserManager</c> is a constructor dependency only — <c>ListAsync</c> queries the
/// context directly and never touches it. It is built properly anyway, rather than passed as
/// <c>null!</c>, so that a future <c>ListAsync</c> which does reach for it fails with an assertion
/// instead of a NullReferenceException that reads like a broken test.</para>
/// </summary>
public class ScimListResponseWireShapeTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext context;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly ScimUserService scim;

    public ScimListResponseWireShapeTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();

        IdentityOptions idOptions = new();
        idOptions.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // matches Program.cs

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

        scim = new ScimUserService(
            new TestDbContextFactory(connection),
            userManager,
            NullLogger<ScimUserService>.Instance);
    }

    private async Task SeedAsync(params string[] userNames)
    {
        foreach (string userName in userNames)
        {
            ApplicationUser user = new() { UserName = userName, Email = userName };
            (await userManager.CreateAsync(user)).Succeeded.Should().BeTrue();
        }
    }

    private async Task<JsonObject> ListAsync(string? filter = null, int startIndex = 1, int count = 100)
    {
        ScimResult result = await scim.ListAsync(filter, startIndex, count, CancellationToken.None);

        result.Status.Should().Be(200);
        result.Error.Should().BeNull();

        // ⚠️ If a typing change makes ListAsync answer something other than a JsonObject body,
        // this cast is where it is noticed. That is intentional: the point of the file is that the
        // envelope cannot be retyped without a human reading these assertions.
        return result.Body!.AsObject();
    }

    /// <summary>
    /// ⚠️ <b>The assertion this file was written for.</b> RFC 7644 §3.4.2 spells the member
    /// <c>Resources</c>, and it is the single SCIM member that is not lowerCamelCase. Renaming it
    /// to <c>resources</c> — which is precisely what typing this envelope as a record does, with no
    /// visible rename anywhere in the diff — makes every connector read a page with no users in it
    /// behind a 200 OK. Both halves matter: the capital-R key must be present AND the lowercase
    /// spelling must be absent, because a shape carrying both would satisfy a one-sided check while
    /// still telling half the connectors the directory is empty.
    /// </summary>
    [Fact]
    public async Task The_list_envelope_names_Resources_with_a_capital_R()
    {
        await SeedAsync("a@example.com");

        string json = (await ListAsync()).ToJsonString();

        json.Should().Contain("\"Resources\":[",
            "RFC 7644 §3.4.2 names the member Resources, and connectors read it by that exact name");
        json.Should().NotContain("\"resources\"",
            "the camelCased spelling is what JsonSerializerDefaults.Web would produce from a "
            + "record property, and it returns an empty page to every SCIM client");
    }

    /// <summary>
    /// ⚠️ The exact member set. A typed envelope declaring the members SCIM also defines for
    /// ListResponse but this service does not emit — <c>schemas</c> aside, that is things like
    /// sort state — would ship them as nulls, and an envelope is the one place a connector is
    /// strictest, because it decides from these four numbers whether to ask for another page.
    /// </summary>
    [Fact]
    public async Task The_list_envelope_carries_exactly_these_members()
    {
        await SeedAsync("a@example.com");

        JsonObject envelope = await ListAsync();

        envelope.Select(member => member.Key).Should().BeEquivalentTo(
            ["schemas", "totalResults", "startIndex", "itemsPerPage", "Resources"]);
        envelope["schemas"]!.AsArray()[0]!.GetValue<string>()
            .Should().Be("urn:ietf:params:scim:api:messages:2.0:ListResponse");
    }

    /// <summary>
    /// ⚠️ The three counters are JSON NUMBERS. SCIM states them as integers, and the error envelope
    /// right next door states its <c>status</c> as a STRING (§3.12) — two neighbouring shapes
    /// disagreeing about how to spell a number is how someone "harmonises" them in one direction or
    /// the other. A quoted <c>"totalResults"</c> makes a strict connector abandon the sync.
    /// </summary>
    [Fact]
    public async Task The_counters_are_numbers()
    {
        await SeedAsync("a@example.com");

        JsonObject envelope = await ListAsync();

        envelope["totalResults"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        envelope["startIndex"]!.GetValueKind().Should().Be(JsonValueKind.Number);
        envelope["itemsPerPage"]!.GetValueKind().Should().Be(JsonValueKind.Number);
    }

    /// <summary>
    /// ⚠️ <c>totalResults</c> is the WHOLE result count and <c>itemsPerPage</c> is this page's —
    /// they are different numbers and they are adjacent ints, so a positional transcription that
    /// swaps them compiles and ships. Binding <c>totalResults</c> to the page length is the worse
    /// direction: the connector reads "1 of 1" and stops after the first page, so everyone past the
    /// first hundred users is never provisioned and nothing anywhere reports a failure.
    /// </summary>
    [Fact]
    public async Task The_total_counts_every_match_while_itemsPerPage_counts_the_page()
    {
        await SeedAsync("a@example.com", "b@example.com", "c@example.com");

        JsonObject envelope = await ListAsync(count: 2);

        envelope["totalResults"]!.GetValue<int>().Should().Be(3);
        envelope["itemsPerPage"]!.GetValue<int>().Should().Be(2);
        envelope["Resources"]!.AsArray().Should().HaveCount(2);
    }

    /// <summary>
    /// ⚠️ SCIM's <c>startIndex</c> is 1-based, unlike almost everything else, and the envelope
    /// echoes back the index it answered — a connector pages by adding <c>itemsPerPage</c> to what
    /// it reads here. Echoing the 0-based offset instead would make it re-request the page it just
    /// had and loop, or skip one user per page forever.
    /// </summary>
    [Fact]
    public async Task The_start_index_is_echoed_one_based()
    {
        await SeedAsync("a@example.com", "b@example.com");

        JsonObject envelope = await ListAsync(startIndex: 2);

        envelope["startIndex"]!.GetValue<int>().Should().Be(2);
        envelope["Resources"]!.AsArray().Should().HaveCount(1);
        envelope["Resources"]![0]!["userName"]!.GetValue<string>().Should().Be("b@example.com");
    }

    /// <summary>
    /// ⚠️ An empty page is an empty ARRAY, never <c>null</c> and never an omitted member. A
    /// connector iterates <c>Resources</c> without checking, so <c>"Resources":null</c> throws
    /// inside the connector and surfaces to the directory administrator as an unexplained sync
    /// failure. This is also the shape the "does this user exist yet" probe gets on every create.
    /// </summary>
    [Fact]
    public async Task An_empty_page_is_an_empty_array()
    {
        JsonObject envelope = await ListAsync();

        envelope.ContainsKey("Resources").Should().BeTrue();
        envelope["Resources"]!.GetValueKind().Should().Be(JsonValueKind.Array);
        envelope["Resources"]!.AsArray().Should().BeEmpty();
        envelope["totalResults"]!.GetValue<int>().Should().Be(0);
        envelope["itemsPerPage"]!.GetValue<int>().Should().Be(0);
    }

    /// <summary>
    /// ⚠️ Each element of <c>Resources</c> is a full user resource, not a trimmed summary. The
    /// envelope and the single-user GET must agree, because a connector reconciles what it listed
    /// against what it reads back — so the member set asserted in
    /// <c>ScimUserResourceWireShapeTests</c> is asserted again HERE, through the envelope, rather
    /// than assumed to travel.
    /// </summary>
    [Fact]
    public async Task Each_listed_resource_is_a_whole_user_resource()
    {
        await SeedAsync("a@example.com");

        JsonObject listed = (await ListAsync())["Resources"]![0]!.AsObject();

        listed.Select(member => member.Key).Should().BeEquivalentTo(
            ["schemas", "id", "userName", "active", "emails", "meta"]);
        listed["active"]!.GetValueKind().Should().Be(JsonValueKind.True);
        listed["meta"]!["location"]!.GetValue<string>()
            .Should().StartWith("/scim/v2/Users/");
    }

    public void Dispose()
    {
        userManager.Dispose();
        context.Dispose();
        connection.Dispose();
    }
}

/// <summary>
/// The error envelope, pinned as TEXT.
///
/// <para><c>ScimErrorBodyTests</c> already asserts the VALUES this builder puts in each member —
/// the keyword on a refusal, <c>uniqueness</c> on a conflict, <c>status</c> read as a string. These
/// assertions are a different thing and the overlap is deliberate: they pin the member SET, and
/// they read the serialised text, because the two ways a typing change breaks this envelope are
/// adding a member and changing how an existing one is spelled, and neither is visible to a test
/// that reads values out of nodes it was handed.</para>
/// </summary>
public class ScimErrorEnvelopeWireShapeTests
{
    /// <summary>
    /// ⚠️ <c>status</c> is a STRING (RFC 7644 §3.12) — quoted, in the text, which is the only place
    /// the quotes exist. It is the member most likely to be "tidied" into a number by someone
    /// reading the JSON rather than the spec, and the counters in the ListResponse next door
    /// genuinely are numbers, so the inconsistency looks like a mistake until you read §3.12. A
    /// connector doing a strict parse cannot read the error at all once it is a number, and reports
    /// the administrator an unexplained failure instead of the reason.
    /// </summary>
    [Fact]
    public void The_status_is_a_quoted_string_in_the_text()
    {
        string json = ScimEndpoints.ErrorBody(ScimResult.NotFound("No user with id 'x'.")).ToJsonString();

        json.Should().Contain("\"status\":\"404\"");
        json.Should().NotContain("\"status\":404");
    }

    /// <summary>
    /// ⚠️ <c>scimType</c> is ABSENT when there is no keyword, not present and null. This is the
    /// assertion a typed record is most likely to break, because emitting nulls is the DEFAULT:
    /// a record with a <c>string? ScimType</c> and no
    /// <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]</c> ships
    /// <c>"scimType":null</c>, and a connector that reads the member and reports it shows the
    /// directory administrator a blank error keyword where it used to show nothing — or, worse,
    /// treats the member's presence as a keyword it does not recognise. The member must not appear
    /// in the text at all.
    /// </summary>
    [Fact]
    public void A_failure_with_no_keyword_does_not_mention_scimType_at_all()
    {
        string json = ScimEndpoints.ErrorBody(
            ScimResult.BadRequest("userName is required.")).ToJsonString();

        json.Should().NotContain("scimType");
    }

    /// <summary>
    /// ⚠️ The exact member set on each of the two shapes this envelope has. SCIM defines other
    /// Error members this service does not emit, and a typed envelope declaring them for
    /// completeness would ship them as nulls on every single failure.
    /// </summary>
    [Fact]
    public void The_error_envelope_carries_exactly_these_members()
    {
        ScimEndpoints.ErrorBody(ScimResult.BadRequest("userName is required."))
            .Select(member => member.Key).Should().BeEquivalentTo(["schemas", "status", "detail"]);

        ScimEndpoints.ErrorBody(ScimResult.Conflict("already exists"))
            .Select(member => member.Key).Should().BeEquivalentTo(
                ["schemas", "status", "detail", "scimType"]);
    }

    /// <summary>
    /// ⚠️ <c>schemas</c> is an array here too, carrying the Error message URN and not the User one.
    /// A connector picks its parser from this value; handed the core User URN on a failure it tries
    /// to read a user resource, fails differently, and reports something unrelated to what went
    /// wrong.
    /// </summary>
    [Fact]
    public void The_error_envelope_declares_the_error_schema_as_an_array()
    {
        JsonObject body = ScimEndpoints.ErrorBody(ScimResult.NotFound("No user with id 'x'."));

        body["schemas"]!.GetValueKind().Should().Be(JsonValueKind.Array);
        body["schemas"]!.AsArray()[0]!.GetValue<string>()
            .Should().Be("urn:ietf:params:scim:api:messages:2.0:Error");
    }
}

/// <summary>
/// The two things that have no seam: the content type, and the ServiceProviderConfig body.
///
/// <para>⚠️ <b>These are SOURCE assertions, and a source scan can only ever prove the code was
/// written — it cannot prove it runs.</b> They are here because the alternative was no cover at
/// all: <c>Respond</c> is private, <c>Results.Content</c>'s content type cannot be read off the
/// returned <see cref="IResult"/> without executing it, and the ServiceProviderConfig body is a
/// <see cref="JsonObject"/> literal inside the endpoint lambda with nothing to call. They catch a
/// deliberate retyping, which is what this file is for; they would not catch a routing mistake, a
/// middleware that rewrites the content type, or any of the ways the document and the behaviour
/// drift apart. <c>ErrorBody</c> was extracted from <c>Respond</c> for exactly this reason and is
/// asserted properly above — extracting a <c>public static JsonObject
/// ServiceProviderConfigBody()</c> the same way would let half of this class be deleted and
/// replaced with real wire assertions.</para>
/// </summary>
/// <summary>
/// The SCIM wire shape as the SERVER actually writes it — status, media type and bytes.
///
/// <para>⚠️ <b>This replaces a source scan.</b> The first version of this class read
/// <c>ScimEndpoints.cs</c> and asserted that the string <c>"application/scim+json"</c> appeared in
/// it and that <c>TypedResults</c> did not. That is the pattern this repository has already been
/// burned by: a source scan passes on a call that never runs, which is how the API's
/// self-description shipped as a 500 with a green source test beside it. These assertions boot the
/// application, call the routes as the holder of <c>scim:provision</c>, and read what comes
/// back.</para>
///
/// <para>⚠️ <b>Why the content type is the fragile part.</b> The response-typing programme
/// (<c>ApiDocumentedResponsesTests.UndescribedSuccessBodies</c>) converts handlers to
/// <c>TypedResults.Ok(record)</c> so the document gains a schema. On these seven routes that would
/// also switch the media type to <c>application/json</c> — silently, with the body otherwise
/// intact — and <c>ScimEndpoints.cs</c>'s own comment records that some connectors reject it. So
/// the media type is asserted on every call below, not just the body.</para>
///
/// <para>The host's database is empty, which is exactly what makes the list case useful: an empty
/// page is where a shape is most likely to omit a member, and <c>Resources</c> must be present as
/// an empty array rather than absent or null. The POPULATED envelope — the counters, the
/// <c>startIndex</c> echo — is asserted by <see cref="ScimListResponseWireShapeTests"/> over its
/// own SQLite database, because seeding users into this shared host would be visible to every
/// other suite in the collection.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ScimOverHttpWireShapeTests(GeneratedOpenApiDocumentFixture fixture)
{
    private const string ScimJson = "application/scim+json";
    private const string Scope = "scim:provision";

    /// <summary>
    /// ⚠️ The headline, on the real wire. RFC 7644 §3.4.2 names this member <c>Resources</c> with a
    /// capital R — the one member in the specification that is not lowerCamelCase — so a typed
    /// record property would serialise it as <c>resources</c> and every connector would read an
    /// empty page while the endpoint answered 200.
    ///
    /// <para>Both spellings are checked. A shape that somehow carried both would satisfy a check
    /// for the presence of one.</para>
    /// </summary>
    [Fact]
    public async Task The_list_envelope_carries_capital_R_Resources()
    {
        (HttpStatusCode status, string? mediaType, string body) =
            await fixture.SendForResponseAsync("GET", "/scim/v2/Users", Scope);

        status.Should().Be(HttpStatusCode.OK);
        mediaType.Should().Be(ScimJson, "a connector that rejects application/json never gets this far");

        body.Should().Contain("\"Resources\"", "RFC 7644 §3.4.2 spells it with a capital R");
        body.Should().NotContain("\"resources\"", "which is what a typed record would have produced");

        JsonObject envelope = JsonNode.Parse(body)!.AsObject();

        envelope.Select(p => p.Key).Should().BeEquivalentTo(
            ["schemas", "totalResults", "startIndex", "itemsPerPage", "Resources"],
            "the exact member set, so a typed shape cannot add or drop one unnoticed");

        envelope["schemas"]!.AsArray()[0]!.GetValue<string>().Should().Be(
            "urn:ietf:params:scim:api:messages:2.0:ListResponse",
            "the literal URN, not the constant compared against itself");

        // ⚠️ An empty page is a present, empty ARRAY — not null, not absent. This host has no
        // users, which is the state in which a shape most easily omits a member.
        envelope["Resources"]!.AsArray().Should().BeEmpty();
        envelope["totalResults"]!.GetValue<int>().Should().Be(0);
        envelope["startIndex"]!.GetValue<int>().Should().Be(1, "SCIM paging is 1-based");
    }

    /// <summary>
    /// The discovery document, which a connector reads ONCE and then obeys.
    ///
    /// <para>⚠️ <c>patch.supported</c> must stay true. Entra deprovisions with
    /// <c>PATCH replace active=false</c>, and a connector told patch is unsupported does not send
    /// it — so flipping that flag disables deprovisioning across every directory at once, with
    /// every endpoint still working perfectly and nothing failing anywhere.</para>
    ///
    /// <para>⚠️ And each capability is a nested OBJECT carrying <c>supported</c>, not a bare
    /// boolean. Flattening <c>patch</c> to <c>true</c> is the obvious simplification and produces a
    /// document no connector can read.</para>
    /// </summary>
    [Fact]
    public async Task The_discovery_document_declares_the_capabilities_a_connector_obeys()
    {
        (HttpStatusCode status, string? mediaType, string body) =
            await fixture.SendForResponseAsync("GET", "/scim/v2/ServiceProviderConfig", Scope);

        status.Should().Be(HttpStatusCode.OK);
        mediaType.Should().Be(ScimJson);

        JsonObject config = JsonNode.Parse(body)!.AsObject();

        config.Select(p => p.Key).Should().BeEquivalentTo(
            ["schemas", "patch", "bulk", "filter", "changePassword", "sort", "etag",
             "authenticationSchemes"]);

        config["patch"]!.AsObject()["supported"]!.GetValue<bool>().Should().BeTrue(
            "a directory that reads patch:false never sends the operation it deprovisions with");

        config["filter"]!.AsObject()["supported"]!.GetValue<bool>().Should().BeTrue();
        config["filter"]!.AsObject()["maxResults"]!.GetValue<int>().Should().Be(
            200, "and it must match the clamp ListAsync applies, or a connector pages past the end");

        foreach (string capability in new[] { "patch", "bulk", "filter", "changePassword", "sort", "etag" })
        {
            config[capability]!.AsObject().Select(p => p.Key).Should().Contain(
                "supported", $"{capability} is an object carrying a flag, not a bare boolean");
        }

        config["authenticationSchemes"]!.AsArray()[0]!.AsObject()["type"]!.GetValue<string>()
            .Should().Be("oauthbearertoken");
    }

    /// <summary>
    /// The error envelope, on the wire, for a user id that does not exist.
    ///
    /// <para>⚠️ <b>Two traps, both invisible to a reviewer.</b> RFC 7644 §3.12 requires
    /// <c>status</c> to be a JSON <b>string</b>, so a record with an <c>int Status</c> would emit
    /// <c>404</c> instead of <c>"404"</c>; and <c>scimType</c> must be ABSENT when there is nothing
    /// to say, which a record emits as <c>"scimType": null</c> without
    /// <c>JsonIgnoreCondition.WhenWritingNull</c>.</para>
    ///
    /// <para>⚠️ It also proves the error body survives the middleware. A bodyless error used to be
    /// re-executed against a Razor page by <c>UseStatusCodePagesWithReExecute</c>, turning the
    /// API's 404 into a 405 or an antiforgery 400 — this one carries a body, which is what spared
    /// it, and asserting the media type here is what would notice if that ever changed.</para>
    /// </summary>
    [Fact]
    public async Task A_missing_user_answers_the_scim_error_envelope()
    {
        (HttpStatusCode status, string? mediaType, string body) =
            await fixture.SendForResponseAsync("GET", $"/scim/v2/Users/{Guid.NewGuid()}", Scope);

        status.Should().Be(HttpStatusCode.NotFound);
        mediaType.Should().Be(ScimJson, "a SCIM error is scim+json, not problem+json");

        JsonObject error = JsonNode.Parse(body)!.AsObject();

        error.Select(p => p.Key).Should().BeEquivalentTo(
            ["schemas", "status", "detail"],
            "and scimType is ABSENT, not null — a record would emit it as null by default");

        error["schemas"]!.AsArray()[0]!.GetValue<string>().Should().Be(
            "urn:ietf:params:scim:api:messages:2.0:Error");

        error["status"]!.GetValueKind().Should().Be(
            JsonValueKind.String, "RFC 7644 §3.12 requires a string, and an int Status would emit 404");
        error["status"]!.GetValue<string>().Should().Be("404");
    }
}
