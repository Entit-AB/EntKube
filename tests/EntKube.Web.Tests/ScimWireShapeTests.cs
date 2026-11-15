using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using Microsoft.AspNetCore.Http;
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
/// A SCIM body as the server writes it.
///
/// <para>⚠️ Through <see cref="ScimJsonResult.Serialize"/> — the serializer the endpoints
/// themselves use — not a <see cref="JsonSerializerOptions"/> of the test's own. These records
/// carry <c>[JsonPropertyName]</c> and <c>[JsonIgnore]</c> for members whose spelling and
/// presence a connector depends on, and a test with its own options would keep passing through a
/// change to the real ones.</para>
///
/// <para>This replaces reading keys off a hand-built <c>JsonObject</c>. Every hazard these
/// assertions exist for — the capital-R <c>Resources</c>, <c>status</c> as a string, an absent
/// <c>scimType</c> — lives in SERIALISATION, so that is what they now read.</para>
/// </summary>
internal static class ScimAsWritten
{
    public static string Text(object body) => ScimJsonResult.Serialize(body);

    public static JsonObject Object(object body) => JsonNode.Parse(Text(body))!.AsObject();
}

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
        JsonObject resource = ScimAsWritten.Object(ScimUserService.ToScim(User()));

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
        JsonObject resource = ScimAsWritten.Object(ScimUserService.ToScim(User()));

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
        ScimAsWritten.Object(ScimUserService.ToScim(User()))["active"]!.GetValueKind()
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
        JsonObject resource = ScimAsWritten.Object(ScimUserService.ToScim(User()));

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
        JsonObject meta = ScimAsWritten.Object(ScimUserService.ToScim(User()))["meta"]!.AsObject();

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
        string json = ScimAsWritten.Text(ScimUserService.ToScim(User()));

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
        return ScimAsWritten.Object(result.Body!);
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
        string json = ScimAsWritten.Text(
            ScimEndpoints.ErrorBody(ScimResult.NotFound("No user with id 'x'.")));

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
        string json = ScimAsWritten.Text(ScimEndpoints.ErrorBody(
            ScimResult.BadRequest("userName is required.")));

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
        ScimAsWritten.Object(ScimEndpoints.ErrorBody(ScimResult.BadRequest("userName is required.")))
            .Select(member => member.Key).Should().BeEquivalentTo(["schemas", "status", "detail"]);

        ScimAsWritten.Object(ScimEndpoints.ErrorBody(ScimResult.Conflict("already exists")))
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
        JsonObject body = ScimAsWritten.Object(
            ScimEndpoints.ErrorBody(ScimResult.NotFound("No user with id 'x'.")));

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

/// <summary>
/// What the SCIM results write, executed directly.
///
/// <para>⚠️ <b>The success statuses are here rather than over HTTP on purpose.</b> A real 201 or
/// 204 means creating and deleting a user, and the shared API host's database is read by
/// <see cref="ScimOverHttpWireShapeTests.The_list_envelope_carries_capital_R_Resources"/>, which
/// asserts it is empty. Seeding a user into it would make that assertion pass or fail on test
/// ORDER. Executing the result against its own <see cref="DefaultHttpContext"/> reads the same
/// three things — status, media type, bytes — without a database at all.</para>
/// </summary>
public class ScimResultWireShapeTests
{
    private const string ScimJson = "application/scim+json";

    private static async Task<(int Status, string? ContentType, string Body)> Written(IResult result)
    {
        DefaultHttpContext http = new();
        using MemoryStream buffer = new();
        http.Response.Body = buffer;

        await result.ExecuteAsync(http);

        buffer.Position = 0;
        using StreamReader reader = new(buffer);
        return (http.Response.StatusCode, http.Response.ContentType, await reader.ReadToEndAsync());
    }

    private static ScimUserResponse AUser() => ScimUserResponse.From(new ApplicationUser
    {
        Id = "user-1", UserName = "someone@example.com", Email = "someone@example.com",
    });

    /// <summary>
    /// ⚠️ The headline for this change. Every other route on the API gained its schema by
    /// returning <c>TypedResults.Ok(record)</c>, which answers <c>application/json</c> — and
    /// <c>ScimEndpoints</c>'s own comment records that some connectors reject it. The media type
    /// is the thing typing these routes most easily destroys, and it destroys it silently, with
    /// the body otherwise intact and the status still 200.
    /// </summary>
    [Fact]
    public async Task A_success_is_written_as_scim_json()
    {
        (int status, string? contentType, string body) =
            await Written(ScimOk<ScimUserResponse>.Success(AUser()));

        status.Should().Be(200);
        contentType.Should().Be(ScimJson, "not application/json, which TypedResults.Ok would write");
        body.Should().Contain("\"userName\":\"someone@example.com\"");
    }

    /// <summary>
    /// A creation answers 201, not 200. The two are separate result types precisely because
    /// <c>PopulateMetadata</c> is static and cannot read a status off an instance — collapsing
    /// them would document every creation as a 200 and a generated client would never model the
    /// 201 it actually receives.
    /// </summary>
    [Fact]
    public async Task A_creation_is_written_as_201_scim_json()
    {
        (int status, string? contentType, _) =
            await Written(ScimCreated<ScimUserResponse>.Success(AUser()));

        status.Should().Be(201);
        contentType.Should().Be(ScimJson);
    }

    /// <summary>
    /// ⚠️ A 204 writes NOTHING, and declares no content type. The result this replaced rendered an
    /// absent body as the two bytes <c>{}</c>; on a 204 that is a malformed response rather than
    /// an empty one, and some clients fail the whole deprovisioning run on it.
    /// </summary>
    [Fact]
    public async Task A_deletion_writes_no_body_and_no_content_type()
    {
        (int status, string? contentType, string body) = await Written(ScimNoContent.Success());

        status.Should().Be(204);
        body.Should().BeEmpty();
        contentType.Should().BeNull("a 204 has no content to describe the type of");
    }

    /// <summary>
    /// A failure travels through the same result as its operation's success, so it must still be
    /// SCIM's error envelope at SCIM's media type — and must carry the failure's own status, not
    /// the one the result type declares.
    /// </summary>
    [Fact]
    public async Task A_failure_keeps_the_scim_envelope_and_its_own_status()
    {
        (int status, string? contentType, string body) = await Written(
            ScimCreated<ScimUserResponse>.Failure(
                409, ScimEndpoints.ErrorBody(ScimResult.Conflict("already exists"))));

        status.Should().Be(409, "not the 201 this result type declares as its success");
        contentType.Should().Be(ScimJson, "a SCIM error is scim+json, not problem+json");

        JsonObject error = JsonNode.Parse(body)!.AsObject();
        error["status"]!.GetValueKind().Should().Be(JsonValueKind.String);
        error["status"]!.GetValue<string>().Should().Be("409");
        error["scimType"]!.GetValue<string>().Should().Be("uniqueness");
    }
}

/// <summary>
/// What the generated document SAYS about SCIM, read out of the real document.
///
/// <para>⚠️ <b>Why this exists beside <c>ApiDocumentedResponsesTests</c>.</b> That ratchet counts
/// operations answering a 2xx with no body schema, and it is now 0 — but it asks only whether a
/// schema is there, never what media type it is declared under. A handler switched to
/// <c>TypedResults.Ok(record)</c>, or to <c>TypedResults.Json(record, contentType: …)</c>, would
/// satisfy the ratchet while the document said <c>application/json</c>. In the first case the
/// wire changes too and the connectors break; in the second the wire stays right and the
/// DOCUMENT lies, so a generated client sends the wrong <c>Accept</c> and nothing in this
/// repository would have noticed. Both are caught here.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ScimDocumentedResponseTests(GeneratedOpenApiDocumentFixture fixture)
{
    private const string ScimJson = "application/scim+json";

    private static IEnumerable<(string Method, string Path, string Code, Microsoft.OpenApi.IOpenApiResponse Response)>
        ScimResponses(Microsoft.OpenApi.OpenApiDocument document)
    {
        foreach ((string path, Microsoft.OpenApi.IOpenApiPathItem item) in document.Paths ?? [])
        {
            if (!path.StartsWith("/scim/v2", StringComparison.Ordinal)) continue;

            foreach ((HttpMethod verb, Microsoft.OpenApi.OpenApiOperation operation) in item.Operations ?? [])
            {
                foreach ((string code, Microsoft.OpenApi.IOpenApiResponse response) in
                    operation.Responses ?? [])
                {
                    yield return (verb.Method.ToUpperInvariant(), path, code, response);
                }
            }
        }
    }

    /// <summary>
    /// ⚠️ The guard the ratchet cannot give. Every SCIM success is declared under
    /// <c>application/scim+json</c> and nothing else — a declaration of <c>application/json</c>
    /// beside a wire of <c>application/scim+json</c> is a document that contradicts a tested fact,
    /// which is exactly how the API's self-description shipped as a 500.
    /// </summary>
    [Fact]
    public void Every_scim_success_is_declared_as_scim_json()
    {
        fixture.GenerationFailure.Should().BeNull();

        List<(string Method, string Path, string Code, Microsoft.OpenApi.IOpenApiResponse Response)> successes =
            [.. ScimResponses(fixture.Document!)
                .Where(r => r.Code.StartsWith("2", StringComparison.Ordinal))];

        successes.Should().NotBeEmpty("otherwise this says nothing about SCIM at all");

        List<string> wrong = [.. successes
            .Where(r => r.Response.Content is { Count: > 0 }
                && !r.Response.Content.Keys.SequenceEqual([ScimJson]))
            .Select(r => $"{r.Method} {r.Path} {r.Code}: "
                + string.Join("+", r.Response.Content!.Keys))];

        wrong.Should().BeEmpty(
            "a connector that rejects application/json must not be told to send it: "
            + string.Join("; ", wrong));
    }

    /// <summary>
    /// The seven operations are all described, and each under the status it actually answers.
    ///
    /// <para>⚠️ A creation documented as 200 is a generated client that never models its own 201,
    /// and a deletion documented as 200-with-no-schema is the undescribed body this whole
    /// programme was about. Both would pass a count of "operations with a 2xx".</para>
    /// </summary>
    [Fact]
    public void The_creation_answers_201_and_the_deletion_204()
    {
        fixture.GenerationFailure.Should().BeNull();

        Dictionary<(string, string), string[]> successes = ScimResponses(fixture.Document!)
            .Where(r => r.Code.StartsWith("2", StringComparison.Ordinal))
            .GroupBy(r => (r.Method, r.Path))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Code).Order().ToArray());

        successes.Should().HaveCount(7, "SCIM is seven operations: " + string.Join(
            ", ", successes.Keys.Select(k => $"{k.Item1} {k.Item2}")));

        successes[("POST", "/scim/v2/Users")].Should().Equal(["201"]);
        successes[("DELETE", "/scim/v2/Users/{id}")].Should().Equal(["204"]);

        foreach ((string method, string path) in new[]
        {
            ("GET", "/scim/v2/ServiceProviderConfig"), ("GET", "/scim/v2/Users"),
            ("GET", "/scim/v2/Users/{id}"), ("PUT", "/scim/v2/Users/{id}"),
            ("PATCH", "/scim/v2/Users/{id}"),
        })
        {
            successes[(method, path)].Should().Equal(["200"], $"{method} {path} answers 200");
        }
    }

    /// <summary>
    /// A SCIM route's own refusals are declared as its own envelope — and the two the token
    /// filter produces are not.
    ///
    /// <para>⚠️ <b>This is the distinction that makes the document honest.</b> A SCIM route
    /// answers <c>400</c>, <c>404</c> and <c>409</c> with <see cref="ScimErrorResponse"/> at
    /// <c>application/scim+json</c>, but its <c>401</c> and <c>403</c> come from
    /// <c>ApiTokenAuthFilter</c>, which returns <c>Results.Problem</c> — so for those two it
    /// really does answer <c>application/problem+json</c>. Declaring every refusal the same way
    /// would be wrong in one direction or the other whichever way it was done, and
    /// <c>ProducesProblem</c> on a SCIM route is the easy mistake: it compiles, it looks right,
    /// and it tells a generated client to parse a <c>ProblemDetails</c> out of a SCIM
    /// envelope.</para>
    /// </summary>
    [Fact]
    public void A_scim_refusal_is_its_own_envelope_and_the_tokens_are_not()
    {
        fixture.GenerationFailure.Should().BeNull();

        List<(string Method, string Path, string Code, Microsoft.OpenApi.IOpenApiResponse Response)> refusals =
            [.. ScimResponses(fixture.Document!)
                .Where(r => !r.Code.StartsWith("2", StringComparison.Ordinal))];

        refusals.Should().NotBeEmpty("otherwise SCIM declares no refusals at all");

        List<string> wrong = [];

        foreach ((string method, string path, string code, Microsoft.OpenApi.IOpenApiResponse response) in refusals)
        {
            string[] media = [.. response.Content?.Keys ?? []];

            string[] want = code is "401" or "403"
                ? ["application/problem+json"]
                : [ScimJson];

            if (!media.SequenceEqual(want))
            {
                wrong.Add($"{method} {path} {code}: {string.Join("+", media)} (wanted {want[0]})");
            }
        }

        wrong.Should().BeEmpty(
            "a SCIM route's own refusal is a SCIM envelope; only the token filter's two are "
            + "problem+json: " + string.Join("; ", wrong));

        // ⚠️ And they are actually there. Every one of these was undeclared until the document
        // was read for them — the handlers returned 404 and 409 while the document said the route
        // could only answer 200, 401 or 403.
        refusals.Select(r => r.Code).Distinct().Order().Should().Equal(
            ["400", "401", "403", "404", "409"],
            "these are the five a SCIM route can refuse with");
    }

    /// <summary>
    /// ⚠️ And the 204 declares no body at all. Declaring one — even an empty schema — is how a
    /// deletion gets counted as "describes a success body" without describing anything, which is
    /// the loophole <c>BodylessSuccesses</c> is pinned to keep shut.
    /// </summary>
    [Fact]
    public void The_deletion_declares_no_body()
    {
        fixture.GenerationFailure.Should().BeNull();

        Microsoft.OpenApi.IOpenApiResponse deletion = ScimResponses(fixture.Document!)
            .Single(r => r is { Method: "DELETE", Code: "204" }).Response;

        (deletion.Content is null || deletion.Content.Count == 0).Should().BeTrue(
            "a 204 carries nothing, so there is nothing to describe");
    }
}

/// <summary>
/// Every SCIM verb answers with SCIM's media type, over real HTTP.
///
/// <para>⚠️ Through FAILURES, deliberately. A real 201 or 204 means creating and deleting a user
/// in the shared API host, whose database
/// <see cref="ScimOverHttpWireShapeTests.The_list_envelope_carries_capital_R_Resources"/> asserts
/// is empty — so a success here would make that test pass or fail on ORDER. A failure exercises
/// the same result type on the same route and proves the route is wired to it, which is what
/// would break if one of these handlers were converted back to <c>TypedResults</c> or left
/// answering <c>ProblemDetails</c>.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class ScimEveryVerbWireShapeTests(GeneratedOpenApiDocumentFixture fixture)
{
    private const string ScimJson = "application/scim+json";
    private const string Scope = "scim:provision";

    public static TheoryData<string, string, string?, HttpStatusCode> Verbs() => new()
    {
        // A body-less POST is refused before it reaches the store, so nothing is written.
        { "POST", "/scim/v2/Users", null, HttpStatusCode.BadRequest },
        { "PUT", "/scim/v2/Users/nobody", """{"userName":"x"}""", HttpStatusCode.NotFound },
        { "PATCH", "/scim/v2/Users/nobody", """{"Operations":[]}""", HttpStatusCode.NotFound },
        { "DELETE", "/scim/v2/Users/nobody", null, HttpStatusCode.NotFound },
    };

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task Every_verb_answers_the_scim_envelope(
        string method, string url, string? json, HttpStatusCode expected)
    {
        (HttpStatusCode status, string? mediaType, string body) =
            await fixture.SendForResponseAsync(method, url, Scope, json);

        status.Should().Be(expected);
        mediaType.Should().Be(ScimJson, $"{method} {url} must answer SCIM's media type");

        JsonObject error = JsonNode.Parse(body)!.AsObject();

        error["schemas"]!.AsArray()[0]!.GetValue<string>().Should().Be(
            "urn:ietf:params:scim:api:messages:2.0:Error");
        error["status"]!.GetValueKind().Should().Be(
            JsonValueKind.String, "RFC 7644 §3.12 requires a string");
        error["status"]!.GetValue<string>().Should().Be(((int)expected).ToString());
    }
}
