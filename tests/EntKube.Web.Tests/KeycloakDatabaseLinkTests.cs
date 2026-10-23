using System.Reflection;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Which database a Keycloak is linked to, and how an omitted argument came to unlink it.
///
/// <para><b>The defect.</b> <c>KeycloakService.ConfigureAsync</c> took
/// <c>registeredPostgresDatabaseId</c> as an optional parameter defaulting to <c>null</c>, and
/// wrote it unconditionally. <c>ClusterDetail.razor</c> has a <c>cnpg-database</c> field and no
/// registered-Postgres one, so it omitted the argument — which took the default and <b>cleared</b>
/// whatever the identity tab had set.</para>
///
/// <para>⚠️ The two ids are mutually exclusive, which
/// <c>KeycloakComponentConfig</c>'s own doc comment states. So for a Keycloak backed by a
/// registered Postgres, saving that form left <em>neither</em> id set — a state the model says
/// cannot exist. <c>BlueprintFromClusterService</c> reads
/// <c>CnpgDatabaseId ?? RegisteredPostgresDatabaseId</c>, so a blueprint exported from that
/// cluster then carried no database for its Keycloak at all.</para>
///
/// <para><b>Why these tests and not a behavioural one.</b> The service's write was never wrong:
/// writing the value it is given is correct, and would be correct still. What was wrong is that a
/// caller could decline to give one. So the guarantee worth pinning is structural — the parameter
/// has no default, so the compiler asks every call site — plus the one call site whose answer is
/// "preserve". A behavioural test of <c>ConfigureAsync</c> would exercise the half that was
/// already right.</para>
/// </summary>
public class KeycloakDatabaseLinkTests
{
    /// <summary>
    /// ⭐ The structural guarantee. ⚠️ A defaulted parameter that is written unconditionally is a
    /// silent clear: the call site that forgets it looks identical to one that means it, and the
    /// omission is invisible in a diff while a wrong value is not. The same reasoning gives
    /// <c>ChangeIntent</c> no default on the nine exec verbs — see
    /// <see cref="ExecChangeIntentTests"/>.
    /// </summary>
    [Fact]
    public void The_registered_postgres_link_has_no_default_so_a_caller_cannot_omit_it()
    {
        MethodInfo configure = typeof(KeycloakService)
            .GetMethods()
            .Single(m => m.Name == "ConfigureAsync");

        ParameterInfo registered = configure.GetParameters()
            .SingleOrDefault(p => p.Name == "registeredPostgresDatabaseId")
            ?? throw new Xunit.Sdk.XunitException(
                "ConfigureAsync no longer takes registeredPostgresDatabaseId; if the database link "
                + "moved to a different shape, this test has to follow rather than quietly stop "
                + "checking anything");

        registered.HasDefaultValue.Should().BeFalse(
            "it is written unconditionally, so a default makes an omitted argument clear the "
            + "stored link while looking like a caller that meant null");

        // Health check that survives the finding: its sibling is the comparison this is about.
        // CnpgDatabaseId never had a default, which is exactly why only one of the two could be
        // forgotten — so if this stops being true the asymmetry has moved and the test above is
        // measuring the wrong parameter.
        configure.GetParameters()
            .Single(p => p.Name == "cnpgDatabaseId")
            .HasDefaultValue.Should().BeFalse();
    }

    /// <summary>
    /// The one call site whose answer is "preserve", pinned by reading the source: the form it
    /// serves has no registered-Postgres field, so saving it expresses no opinion about one and
    /// must not decide the question.
    ///
    /// <para>Picking a CNPG database <em>does</em> decide it, because the two are mutually
    /// exclusive — so that branch clears the registered link deliberately, and the test asserts
    /// the conditional rather than just the presence of a read.</para>
    /// </summary>
    [Fact]
    public void The_cluster_page_preserves_a_link_it_has_no_field_for()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/EntKube.Web/Components/Pages/Tenants/ClusterDetail.razor"));

        File.Exists(path).Should().BeTrue(
            "this test reads the cluster page's source; if it moved, the test has to follow "
            + "rather than quietly stop checking anything");

        // Comments stripped: the explanation left at the call site names the behaviour it
        // replaced, and matching prose instead of code is the trap every ratchet here avoids.
        string code = string.Join('\n', File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                     && !l.TrimStart().StartsWith('*')));

        code.Should().Contain("GetConfigForComponentAsync(TenantId, componentId)",
            "preserving a link means reading what is stored");

        code.Should().Contain("existingKeycloak?.RegisteredPostgresDatabaseId",
            "and passing it back rather than letting the parameter default to null");

        code.Should().Contain("cnpgDatabaseId is null",
            "the preserve is conditional: choosing a CNPG database clears the registered link on "
            + "purpose, because the two cannot both be set");
    }
}
