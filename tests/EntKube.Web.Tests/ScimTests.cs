using System.Text.Json.Nodes;
using EntKube.Web.Data;
using EntKube.Web.Services.Scim;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for SCIM filter parsing.
///
/// The property they defend: an unsupported filter is an ERROR, never ignored.
/// Silently dropping a filter turns "find this one user" into "here is every user",
/// and a provisioning client that asked whether someone exists then concludes
/// something false about who is already there.
/// </summary>
public class ScimFilterTests
{
    [Fact]
    public void Parses_the_filter_directories_actually_send()
    {
        ScimFilterResult result = ScimFilter.Parse("""userName eq "someone@example.com" """);

        result.IsSupported.Should().BeTrue();
        result.Attribute.Should().Be("username");
        result.Value.Should().Be("someone@example.com");
    }

    [Fact]
    public void No_filter_means_list_everything()
    {
        ScimFilterResult result = ScimFilter.Parse(null);

        result.IsSupported.Should().BeTrue();
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Attribute_names_are_case_insensitive()
    {
        ScimFilter.Parse("""USERNAME eq "x" """).Attribute.Should().Be("username");
    }

    [Fact]
    public void An_unquoted_boolean_value_parses()
    {
        ScimFilterResult result = ScimFilter.Parse("active eq true");

        result.IsSupported.Should().BeTrue();
        result.Value.Should().Be("true");
    }

    [Theory]
    [InlineData("""userName co "smith" """)]
    [InlineData("""userName sw "a" """)]
    [InlineData("userName pr")]
    [InlineData("""meta.lastModified gt "2026-01-01" """)]
    public void Operators_other_than_eq_are_refused_not_approximated(string filter)
    {
        // Honouring "co" as equality would return the wrong users.
        ScimFilterResult result = ScimFilter.Parse(filter);

        result.IsSupported.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("""userName eq "a" and active eq true""")]
    [InlineData("""userName eq "a" or userName eq "b" """)]
    [InlineData("""not(userName eq "a")""")]
    [InlineData("""(userName eq "a")""")]
    public void Compound_filters_are_refused_rather_than_truncated(string filter)
    {
        // Treating 'a eq x and b eq y' as just 'a eq x' would silently widen the result.
        ScimFilter.Parse(filter).IsSupported.Should().BeFalse();
    }

    [Fact]
    public void Filtering_on_an_unsupported_attribute_is_refused()
    {
        ScimFilterResult result = ScimFilter.Parse("""displayName eq "Someone" """);

        result.IsSupported.Should().BeFalse();
        result.Error.Should().Contain("displayName");
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("userName eq")]
    [InlineData("""userName eq "" """)]
    public void Malformed_filters_are_refused(string filter)
    {
        ScimFilter.Parse(filter).IsSupported.Should().BeFalse();
    }

    [Fact]
    public void An_unsupported_filter_never_reports_itself_as_empty()
    {
        // IsEmpty means "no filter, list everything". An unsupported filter reaching
        // that path would be exactly the silent widening this guards against.
        ScimFilterResult result = ScimFilter.Parse("""userName co "x" """);

        result.IsEmpty.Should().BeFalse();
        result.IsSupported.Should().BeFalse();
    }
}

/// <summary>
/// Tests for SCIM resource shaping and the boolean handling that decides whether a
/// deprovisioning request actually deprovisions.
/// </summary>
public class ScimUserMappingTests
{
    private static ApplicationUser User(DateTimeOffset? lockoutEnd = null) => new()
    {
        Id = "user-1",
        UserName = "someone@example.com",
        Email = "someone@example.com",
        LockoutEnd = lockoutEnd,
    };

    [Fact]
    public void Renders_a_user_as_a_scim_resource()
    {
        JsonObject resource = ScimUserService.ToScim(User());

        resource["id"]!.GetValue<string>().Should().Be("user-1");
        resource["userName"]!.GetValue<string>().Should().Be("someone@example.com");
        resource["active"]!.GetValue<bool>().Should().BeTrue();
        resource["schemas"]!.AsArray()[0]!.GetValue<string>().Should().Be(ScimUserService.UserSchema);
        resource["meta"]!["location"]!.GetValue<string>().Should().Be("/scim/v2/Users/user-1");
    }

    [Fact]
    public void A_far_future_lockout_renders_as_inactive()
    {
        ScimUserService.ToScim(User(DateTimeOffset.MaxValue))["active"]!
            .GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_failed_login_lockout_does_not_render_as_deprovisioned()
    {
        // Someone locked out for fifteen minutes after mistyping their password has not
        // been removed from the directory, and reporting them as inactive would make the
        // IdP believe a deprovision it never issued had taken effect.
        ScimUserService.ToScim(User(DateTimeOffset.UtcNow.AddMinutes(15)))["active"]!
            .GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void A_past_lockout_renders_as_active()
    {
        ScimUserService.ToScim(User(DateTimeOffset.UtcNow.AddDays(-1)))["active"]!
            .GetValue<bool>().Should().BeTrue();
    }

    // ── Boolean coercion: the difference between deprovisioned and not ──

    [Fact]
    public void Reads_a_json_boolean()
    {
        ScimUserService.ReadBool(JsonValue.Create(false), true).Should().BeFalse();
        ScimUserService.ReadBool(JsonValue.Create(true), false).Should().BeTrue();
    }

    [Theory]
    [InlineData("False")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void Reads_a_boolean_sent_as_a_string(string value)
    {
        // Some Entra connector versions send "False" as a string. Reading that as
        // anything other than false leaves a deprovisioned user with working access.
        ScimUserService.ReadBool(JsonValue.Create(value), true).Should().BeFalse();
    }

    [Fact]
    public void An_absent_value_falls_back_rather_than_guessing()
    {
        ScimUserService.ReadBool(null, true).Should().BeTrue();
        ScimUserService.ReadBool(null, false).Should().BeFalse();
    }

    [Fact]
    public void An_uninterpretable_value_falls_back()
    {
        ScimUserService.ReadBool(JsonValue.Create("maybe"), true).Should().BeTrue();
    }

    [Fact]
    public void Disabled_detection_matches_what_the_service_writes()
    {
        // These two must agree, or a user disabled by SCIM would still report active.
        ApplicationUser disabled = User(DateTimeOffset.MaxValue);

        ScimUserService.IsDisabled(disabled).Should().BeTrue();
        ScimUserService.IsDisabled(User()).Should().BeFalse();
        ScimUserService.IsDisabled(User(DateTimeOffset.UtcNow.AddMinutes(5))).Should().BeFalse();
    }
}

// ── PATCH: the operation a directory deprovisions with ────────────────────────

/// <summary>
/// What a PatchOp body is understood to MEAN, decided without a database.
///
/// <para>⚠️ <b>PATCH had no test of any kind before this.</b> It is the operation Entra uses to
/// deprovision — it sends <c>replace active=false</c> rather than a DELETE — so it is the one
/// place where getting it wrong leaves a removed employee with working access while the directory
/// portal reports success. The interpretation was moved out of <c>PatchAsync</c> into
/// <see cref="ScimUserService.Plan"/> precisely so these decisions could be asserted without
/// standing up a <c>UserManager</c>.</para>
/// </summary>
public class ScimPatchPlanTests
{
    private static JsonObject Patch(params JsonObject[] operations) => new()
    {
        ["schemas"] = new JsonArray(ScimUserService.PatchSchema),
        ["Operations"] = new JsonArray([.. operations.Cast<JsonNode>()]),
    };

    /// <summary>
    /// ⚠️ The defect this class was written for. <c>ReadBool(value, true)</c> meant a
    /// <c>replace active</c> arriving with no value ENABLED the account — a deprovision turned
    /// into a reactivation. Defaulting the other way would be equally wrong, deactivating someone
    /// the directory never asked to remove, so the only safe answer is to refuse.
    /// </summary>
    [Fact]
    public void A_valueless_replace_of_active_is_refused_rather_than_guessed()
    {
        ScimPatchPlan plan = ScimUserService.Plan(
            Patch(new JsonObject { ["op"] = "replace", ["path"] = "active" }));

        plan.IsRefused.Should().BeTrue(
            "guessing true reactivates a disabled user and guessing false disables one nobody "
            + "asked to disable");
        plan.ScimType.Should().Be("invalidValue");
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public void A_pathless_valueless_active_is_refused_too()
    {
        ScimPatchPlan plan = ScimUserService.Plan(
            Patch(new JsonObject
            {
                ["op"] = "replace",
                ["value"] = new JsonObject { ["active"] = null },
            }));

        plan.IsRefused.Should().BeTrue("the pathless form is the one Entra actually sends");
        plan.ScimType.Should().Be("invalidValue");
    }

    /// <summary>
    /// ⚠️ A fully qualified path was not recognised, so this form of deprovision matched nothing
    /// and answered 200 OK — the worst possible combination.
    /// </summary>
    [Fact]
    public void A_schema_qualified_active_path_still_deprovisions()
    {
        ScimPatchPlan plan = ScimUserService.Plan(Patch(new JsonObject
        {
            ["op"] = "replace",
            ["path"] = "urn:ietf:params:scim:schemas:core:2.0:User:active",
            ["value"] = false,
        }));

        plan.IsRefused.Should().BeFalse();
        plan.Steps.Should().ContainSingle().Which
            .Should().Be(new ScimPatchStep(ScimPatchAction.SetActive, false, null));
    }

    /// <summary>
    /// Stripping the enterprise URN as well would map <c>...:enterprise:2.0:User:manager</c> onto
    /// a core attribute name, conflating two different attributes.
    /// </summary>
    [Fact]
    public void An_enterprise_extension_path_is_not_flattened_onto_a_core_attribute()
    {
        ScimUserService.NormalisePatchPath(
            "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:active")
            .Should().Be("urn:ietf:params:scim:schemas:extension:enterprise:2.0:user:active");

        ScimUserService.NormalisePatchPath(
            "urn:ietf:params:scim:schemas:core:2.0:User:active").Should().Be("active");
    }

    /// <summary>
    /// ⚠️ SCIM attribute names are case-insensitive, and this file already handled that for paths
    /// and field keys — but the <c>Operations</c> lookup itself was exact, so a connector sending
    /// <c>operations</c> was told its PatchOp had no Operations array and could never deprovision.
    /// </summary>
    [Theory]
    [InlineData("Operations")]
    [InlineData("operations")]
    [InlineData("OPERATIONS")]
    public void The_operations_array_is_found_whatever_its_capitalisation(string member)
    {
        JsonObject patch = new()
        {
            [member] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "active",
                ["value"] = false,
            }),
        };

        ScimPatchPlan plan = ScimUserService.Plan(patch);

        plan.IsRefused.Should().BeFalse();
        plan.Steps.Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }

    /// <summary>
    /// ⚠️ Deliberate tolerance, not an oversight. Entra sends PATCH for <c>name.givenName</c> and
    /// <c>displayName</c> on ordinary profile edits. The RFC would permit a 400 <c>invalidPath</c>,
    /// but answering that makes Entra mark the user as failed and retry forever, so an attribute
    /// this service does not store is ignored. What is never tolerated is guessing a value.
    /// </summary>
    [Fact]
    public void An_attribute_we_do_not_store_is_ignored_rather_than_refused()
    {
        ScimPatchPlan plan = ScimUserService.Plan(Patch(new JsonObject
        {
            ["op"] = "replace",
            ["path"] = "name.givenName",
            ["value"] = "Nils",
        }));

        plan.IsRefused.Should().BeFalse("refusing this breaks ordinary profile updates");
        plan.Steps.Should().BeEmpty("and it must not pretend to have applied anything either");
    }

    /// <summary>
    /// <c>GetValue&lt;string&gt;()</c> throws on a JSON number, and the exception escaped the
    /// handler — producing the application's HTML error page instead of a SCIM error envelope,
    /// which is the single thing <c>Respond</c> exists to prevent.
    /// </summary>
    [Fact]
    public void A_non_string_op_or_path_does_not_throw()
    {
        ScimPatchPlan plan = ScimUserService.Plan(Patch(
            new JsonObject { ["op"] = 1, ["path"] = 2, ["value"] = false }));

        plan.IsRefused.Should().BeFalse();
        plan.Steps.Should().BeEmpty("an unreadable op is ignored, not obeyed");
    }

    /// <summary>
    /// The string-boolean coercion that <c>ScimUserService.ReadBool</c> already handles has to
    /// survive the trip through the plan — some Entra connector versions send <c>"False"</c>.
    /// </summary>
    [Fact]
    public void A_boolean_sent_as_a_string_still_deprovisions()
    {
        ScimPatchPlan plan = ScimUserService.Plan(Patch(new JsonObject
        {
            ["op"] = "replace",
            ["path"] = "active",
            ["value"] = "False",
        }));

        plan.Steps.Should().ContainSingle().Which.Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_missing_operations_array_is_refused_as_invalid_syntax()
    {
        ScimPatchPlan plan = ScimUserService.Plan(new JsonObject { ["schemas"] = new JsonArray() });

        plan.IsRefused.Should().BeTrue();
        plan.ScimType.Should().Be("invalidSyntax");
    }

    [Fact]
    public void A_remove_is_skipped_rather_than_applied()
    {
        ScimPatchPlan plan = ScimUserService.Plan(Patch(
            new JsonObject { ["op"] = "remove", ["path"] = "active" }));

        plan.IsRefused.Should().BeFalse();
        plan.Steps.Should().BeEmpty("remove on a single-valued attribute has no meaning here");
    }
}

/// <summary>
/// The error envelope a connector parses on every failure.
///
/// <para>⚠️ This was inline in a private method, so nothing asserted it. A directory
/// administrator never sees our HTTP status — they see whatever their connector could make of this
/// body, so the difference between "uniqueness" and an unexplained 409 is the difference between a
/// fixable report and a support ticket.</para>
/// </summary>
public class ScimErrorBodyTests
{
    [Fact]
    public void A_refusal_carries_its_scim_keyword()
    {
        JsonObject body = ScimEndpoints.ErrorBody(
            ScimResult.BadRequest("A replace of 'active' requires a value.", ScimTypes.InvalidValue));

        body["schemas"]!.AsArray()[0]!.GetValue<string>().Should().Be(ScimUserService.ErrorSchema);
        body["scimType"]!.GetValue<string>().Should().Be("invalidValue");
        body["detail"]!.GetValue<string>().Should().Contain("requires a value");
    }

    /// <summary>
    /// RFC 7644 s3.12 states status as a string. It is the kind of thing that gets "tidied" into
    /// a number by someone reading the JSON rather than the spec, and a connector doing a strict
    /// parse then fails to read the error at all.
    /// </summary>
    [Fact]
    public void The_status_is_a_string_not_a_number()
    {
        JsonObject body = ScimEndpoints.ErrorBody(ScimResult.NotFound("No user with id 'x'."));

        body["status"]!.GetValue<string>().Should().Be("404");
        body["status"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.String);
    }

    /// <summary>A duplicate userName is the one conflict a provisioning client acts on.</summary>
    [Fact]
    public void A_conflict_is_reported_as_uniqueness_by_default()
    {
        ScimEndpoints.ErrorBody(ScimResult.Conflict("already exists"))["scimType"]!
            .GetValue<string>().Should().Be("uniqueness");
    }

    /// <summary>
    /// And absent when there is nothing useful to say, rather than a placeholder — an invented
    /// keyword is worse than none, because the connector will report it.
    /// </summary>
    [Fact]
    public void A_failure_with_no_keyword_omits_the_member_entirely()
    {
        ScimEndpoints.ErrorBody(ScimResult.BadRequest("userName is required."))
            .ContainsKey("scimType").Should().BeFalse();
    }
}

