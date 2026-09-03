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
