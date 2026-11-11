using System.Text.Json;
using EntKube.Contracts.Secrets;
using EntKube.Web.Services;
using EntKube.Web.Services.PublicApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The four <c>{ok}</c>-family routes serialise exactly as the anonymous objects they replaced:
/// <c>POST /deployments/{id}/sync</c>, <c>POST /deployments/{id}/restart</c>,
/// <c>POST /apps/{appId}/secrets/sync</c> and
/// <c>POST /advisor/findings/{findingId}/acknowledge</c>.
///
/// <para>⚠️ <b>The oracles are the OLD code, cut out of <c>origin/main</c> rather than
/// retyped.</b> A record's mapping and its expected values written from one reading of the handler
/// would agree on a consistent misreading of it.</para>
///
/// <para>⚠️ <b>The sharpest risk in this group is a record SHARED between two routes.</b>
/// <c>restart</c> and <c>acknowledge</c> both emit <c>{"ok":true}</c> and both use
/// <c>OkResponse</c>; <c>sync</c> emits <c>{ok, output}</c> and must NOT. Giving <c>OkResponse</c>
/// a nullable <c>Output</c> to serve all three would add <c>"output":null</c> to two routes that
/// have never sent that key, because <c>JsonSerializerDefaults.Web</c> sets no
/// <c>DefaultIgnoreCondition</c>. The key-list facts below are what hold those two shapes
/// apart.</para>
/// </summary>
public class OkFamilyWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// ⚠️ Every nullable member is SET and every string differs, because the point is a byte
    /// comparison: a null on both sides, or two fields holding one value, proves nothing about
    /// whether the right member was read. The two bindings differ in BOTH members so a
    /// Kind/Secret transposition is visible, and the second's error is null — the case that
    /// distinguishes a synced binding from a failed one.
    /// </summary>
    private static AppSecretSyncResult SyncResult() => new(
        Output: "synced 2 of 3 bindings",
        Bindings:
        [
            new(SecretBindingKind.Database, "billing-db", "connection refused"),
            new(SecretBindingKind.Cache, "billing-redis", null),
        ],
        UnlistedFamilies:
        [
            new(SecretBindingKind.Storage, "the storage family could not be listed"),
        ],
        FatalError: "the vault was sealed");

    [Fact]
    public void A_deployment_sync_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        KubernetesOperationResult<string> result =
            KubernetesOperationResult<string>.Success("deployment.apps/billing-api configured");

        // Copied verbatim from origin/main's handler.
        object asItWas = new { ok = true, output = result.Data };

        JsonSerializer.Serialize(DeploymentSyncResponse.From(result), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ A sync that applied cleanly and printed NOTHING still answers <c>"output":null</c>.
    /// <c>KubernetesOperationResult&lt;T&gt;.Data</c> is <c>T?</c>, so this is reachable — unlike
    /// the <c>ns</c> on <c>GET /deployments</c>, whose null is a contract allowance no producer
    /// can actually emit. Substituting <c>""</c> here would be a different answer.
    /// </summary>
    [Fact]
    public void A_sync_that_printed_nothing_publishes_a_null_output_not_an_empty_string()
    {
        KubernetesOperationResult<string> quiet = new() { IsSuccess = true, Data = null };

        string json = JsonSerializer.Serialize(
            DeploymentSyncResponse.From(quiet), AsTheApiSerialises);

        json.Should().Be("""{"ok":true,"output":null}""");
    }

    /// <summary>
    /// ⚠️ <c>Ok</c> comes from <c>IsSuccess</c>, which is the one enforcement this group has:
    /// an <c>ok:true</c> built from a FAILED result is unwriteable rather than merely unwritten.
    /// <see cref="OkResponse"/> cannot have that — its constructor is public — so this fact is
    /// the only place the invariant is actually checked.
    /// </summary>
    [Fact]
    public void A_failed_result_cannot_produce_an_ok_true_sync_body()
    {
        DeploymentSyncResponse response = DeploymentSyncResponse.From(
            KubernetesOperationResult<string>.Failure("the cluster was unreachable"));

        response.Ok.Should().BeFalse(
            "mapped from IsSuccess, so the body cannot contradict the result it was built from");
    }

    [Fact]
    public void A_restart_and_an_acknowledge_serialise_exactly_as_the_objects_they_replaced()
    {
        // Copied verbatim from origin/main's handlers — both of them, which is the point: the two
        // literals were byte-identical, and that is what justifies one record for two routes.
        object asRestartWas = new { ok = true };
        object asAcknowledgeWas = new { ok = true };

        string actual = JsonSerializer.Serialize(OkResponse.Succeeded, AsTheApiSerialises);

        actual.Should().Be(JsonSerializer.Serialize(asRestartWas, AsTheApiSerialises));
        actual.Should().Be(JsonSerializer.Serialize(asAcknowledgeWas, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ The key list is ONE key, and that is the assertion. If a later edit gives
    /// <see cref="OkResponse"/> an <c>Output</c> member to serve the sync route too, restart and
    /// acknowledge silently begin answering <c>{"ok":true,"output":null}</c> — a new key on two
    /// routes that have never sent one. This fact is what makes that fail instead.
    /// </summary>
    [Fact]
    public void The_ok_body_carries_exactly_one_key()
    {
        JsonSerializer.Serialize(OkResponse.Succeeded, AsTheApiSerialises)
            .Should().Be("""{"ok":true}""");

        System.Text.Json.Nodes.JsonObject ok =
            JsonSerializer.SerializeToNode(OkResponse.Succeeded, AsTheApiSerialises)!.AsObject();

        ok.Select(p => p.Key).Should().Equal("ok");

        System.Text.Json.Nodes.JsonObject sync =
            JsonSerializer.SerializeToNode(
                DeploymentSyncResponse.From(
                    KubernetesOperationResult<string>.Success("out")), AsTheApiSerialises)!
            .AsObject();

        sync.Select(p => p.Key).Should().Equal(
            ["ok", "output"],
            "the sync body is the SUPERSET of the ok body — two shapes, deliberately");
    }

    [Fact]
    public void An_app_secret_sync_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        AppSecretSyncResult result = SyncResult();

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            failed = result.AnythingFailed,
            output = result.Output,
            bindings = result.Bindings
                .Select(b => new { kind = b.Kind.ToString(), secret = b.SecretName, error = b.Error })
                .ToList(),
            unlistedFamilies = result.UnlistedFamilies
                .Select(f => new { kind = f.Kind.ToString(), error = f.Error })
                .ToList(),
            fatalError = result.FatalError,
        };

        JsonSerializer.Serialize(AppSecretSyncResponse.From(result), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// Named as well as compared, so a failure says WHICH key moved — and in key order, including
    /// the nested rows the CLI reads by name out of <c>bindings</c>.
    /// </summary>
    [Fact]
    public void The_secret_sync_keys_are_the_ones_clients_already_read()
    {
        System.Text.Json.Nodes.JsonObject body =
            JsonSerializer.SerializeToNode(
                AppSecretSyncResponse.From(SyncResult()), AsTheApiSerialises)!.AsObject();

        body.Select(p => p.Key).Should().Equal(
            "failed", "output", "bindings", "unlistedFamilies", "fatalError");

        body["bindings"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "kind", "secret", "error");

        body["unlistedFamilies"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            ["kind", "error"],
            "a family failure has no `secret` key — a whole family was never listed, so there is "
            + "no one secret to name");
    }

    /// <summary>
    /// ⚠️ The renames, asserted as VALUES. All three members of a binding are strings on adjacent
    /// factory parameters, so the key list above would be satisfied by a mapping that put the
    /// right names on the wrong members.
    /// </summary>
    [Fact]
    public void The_renamed_binding_fields_carry_the_value_their_source_member_holds()
    {
        AppSecretSyncResponse body = AppSecretSyncResponse.From(SyncResult());

        body.Bindings[0].Secret.Should().Be("billing-db", "`secret` is SecretName");
        body.Bindings[0].Kind.Should().Be("Database", "and `kind` is the family, not the secret");
        body.Bindings[0].Error.Should().Be("connection refused");
        body.Bindings[1].Error.Should().BeNull("null is how a binding says it synced");

        body.UnlistedFamilies[0].Kind.Should().Be("Storage");
        body.UnlistedFamilies[0].Error.Should().Be("the storage family could not be listed");

        body.Output.Should().Be("synced 2 of 3 bindings");
        body.FatalError.Should().Be(
            "the vault was sealed", "`fatalError` and `output` are different fields");
    }

    /// <summary>
    /// ⚠️ <b>Hard-coded expectations, deliberately</b> — the same reason as the Delivery enums:
    /// writing <c>kind.ToString()</c> here would be the production expression compared against
    /// itself. A number would break the CLI's KIND column, and so would a camelCased name.
    /// </summary>
    [Theory]
    [InlineData(SecretBindingKind.Database, "Database")]
    [InlineData(SecretBindingKind.Cache, "Cache")]
    [InlineData(SecretBindingKind.Messaging, "Messaging")]
    [InlineData(SecretBindingKind.Storage, "Storage")]
    [InlineData(SecretBindingKind.Identity, "Identity")]
    public void A_binding_kind_is_published_by_name_in_its_own_case(
        SecretBindingKind kind, string expected)
    {
        SecretBindingSyncResponse binding =
            SecretBindingSyncResponse.From(new SecretSyncOutcome(kind, "a-secret", null));

        binding.Kind.Should().Be(expected);

        JsonSerializer.Serialize(binding, AsTheApiSerialises)
            .Should().Contain($"\"kind\":\"{expected}\"", "a number here would break the CLI's KIND column");
    }

    /// <summary>
    /// ⚠️ <c>failed</c> is COMPUTED, so the fixture builds each disjunct rather than assigning the
    /// flag — and each one must be sufficient on its own. This is also what decides 200 versus
    /// 207, so a disjunct wrongly dropped from <c>AnythingFailed</c> would turn a partial failure
    /// into a clean 200.
    /// </summary>
    [Fact]
    public void Failed_is_true_for_each_of_the_three_things_that_can_go_wrong()
    {
        AppSecretSyncResponse clean = AppSecretSyncResponse.From(new AppSecretSyncResult(
            Output: "all good",
            Bindings: [new(SecretBindingKind.Database, "db", null)],
            UnlistedFamilies: [],
            FatalError: null));
        clean.Failed.Should().BeFalse("the control — without it the three below prove nothing");

        AppSecretSyncResponse fatal = AppSecretSyncResponse.From(new AppSecretSyncResult(
            Output: null, Bindings: [], UnlistedFamilies: [], FatalError: "sealed"));
        fatal.Failed.Should().BeTrue("a fatal error alone");

        AppSecretSyncResponse unlisted = AppSecretSyncResponse.From(new AppSecretSyncResult(
            Output: null, Bindings: [],
            UnlistedFamilies: [new(SecretBindingKind.Cache, "could not list")],
            FatalError: null));
        unlisted.Failed.Should().BeTrue("an unlistable family alone");

        AppSecretSyncResponse oneBad = AppSecretSyncResponse.From(new AppSecretSyncResult(
            Output: null,
            Bindings:
            [
                new(SecretBindingKind.Database, "db", null),
                new(SecretBindingKind.Cache, "redis", "refused"),
            ],
            UnlistedFamilies: [], FatalError: null));
        oneBad.Failed.Should().BeTrue("one failed binding among several that worked");
    }

    /// <summary>
    /// ⚠️ <b>The server's 200-or-207 decision, which nothing asserted before.</b>
    /// <c>CliPartialFailureTests</c> pins what the CLI does when handed a 207 — from a body it
    /// builds itself — so the client half was covered and "does the route answer 207 at all" was
    /// covered nowhere. A handler returning <c>TypedResults.Ok</c> on both branches passed the
    /// entire suite, and every pipeline would read a partial sync as clean, because
    /// <c>HttpResponseMessage.IsSuccessStatusCode</c> is true for 207.
    ///
    /// <para>⚠️ Asserted through the union's runtime <c>Result</c>, not over HTTP: the decision is
    /// a pure function of the body, and reaching it over HTTP would mean a cluster that fails a
    /// binding on demand. The status is what is checked, not merely the result TYPE —
    /// <c>JsonHttpResult&lt;T&gt;</c> constructed with any other status would be the same
    /// type.</para>
    /// </summary>
    [Fact]
    public void A_partial_sync_answers_207_and_a_clean_one_200_with_the_same_body()
    {
        AppSecretSyncResponse partial = AppSecretSyncResponse.From(SyncResult());
        partial.Failed.Should().BeTrue("the fixture must be a failing one, or this proves nothing");

        IResult partialAnswer = PublicApiEndpoints.SecretSyncAnswer(partial).Result;

        partialAnswer.Should().BeOfType<JsonHttpResult<AppSecretSyncResponse>>()
            .Which.StatusCode.Should().Be(
                207,
                "207 Multi-Status is the machine-readable half of 'some of this did not happen'");

        AppSecretSyncResponse clean = AppSecretSyncResponse.From(new AppSecretSyncResult(
            Output: "all delivered",
            Bindings: [new(SecretBindingKind.Database, "billing-db", null)],
            UnlistedFamilies: [],
            FatalError: null));
        clean.Failed.Should().BeFalse();

        PublicApiEndpoints.SecretSyncAnswer(clean).Result
            .Should().BeOfType<Ok<AppSecretSyncResponse>>(
                "a clean sync is an ordinary 200 — otherwise the fix would be 'always 207'");

        // ⚠️ And the BODY does not change between the two statuses. A reader could reasonably
        // assume the 207 carries something extra; it does not, and a client must read `failed`
        // or the status, never a difference in shape.
        JsonSerializer.Serialize(
                ((JsonHttpResult<AppSecretSyncResponse>)partialAnswer).Value, AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(partial, AsTheApiSerialises));
    }

    /// <summary>
    /// ⚠️ An empty <c>bindings</c> array with a null <c>fatalError</c> means the app has no
    /// bindings — NOT that the sync died. Both keys are written either way, so a client can tell
    /// the two apart; this is the fact that keeps them distinguishable.
    /// </summary>
    [Fact]
    public void An_app_with_no_bindings_is_not_the_same_as_a_sync_that_died()
    {
        string none = JsonSerializer.Serialize(
            AppSecretSyncResponse.From(new AppSecretSyncResult(null, [], [], null)),
            AsTheApiSerialises);

        none.Should().Be("""{"failed":false,"output":null,"bindings":[],"unlistedFamilies":[],"fatalError":null}""");

        string died = JsonSerializer.Serialize(
            AppSecretSyncResponse.From(new AppSecretSyncResult(null, [], [], "the vault was sealed")),
            AsTheApiSerialises);

        died.Should().Contain("\"fatalError\":\"the vault was sealed\"");
        died.Should().Contain("\"failed\":true");
    }
}
