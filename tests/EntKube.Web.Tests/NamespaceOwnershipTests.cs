using EntKube.Web.Services.Cost;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Who a namespace's cost is attributed to when more than one deployment targets it.
///
/// <para><b>What was wrong.</b> <c>CostReportService</c> took the customer from the FIRST
/// deployment in the group, on the stated grounds that "any of them identifies it, and billing
/// needs only that". That holds only while every app in the group belongs to one customer, and
/// nothing enforces it: an app is unique by <c>(CustomerId, Name)</c> and a deployment by
/// <c>(AppId, Name)</c>, while the namespace is free text. So two apps under two different
/// customers can target one <c>(cluster, namespace)</c> — and when they did, the whole namespace's
/// cost was billed to whichever row <c>Enumerable.First()</c> returned, the other customer was
/// charged nothing, and the per-customer panel rendered the full amount in that customer's own
/// table. The enumeration order, which nothing pins, decided who paid.</para>
///
/// <para>⚠️ <b>The fix is the argument the file had already won one field over.</b> The apps list
/// keeps every app precisely because billing a shared namespace to whichever was enumerated first
/// "would put a made-up number on an invoice", and the environment is null when a namespace serves
/// two because it "belongs to neither". The customer now follows the same rule, so such a namespace
/// is unattributed and its cost is reported as such.</para>
///
/// <para>⚠️ <b>Why this file exists at all.</b> The decision was an inline grouping over an
/// anonymous type in the middle of a method that needs a database, Delivery, Prometheus and a
/// cluster client — so nothing could assert it, and it was wrong. It is now a static method taking
/// a list and returning a dictionary, which is testable with values and no fixture.</para>
/// </summary>
public class NamespaceOwnershipTests
{
    private static readonly Guid Cluster = Guid.Parse("c1000000-0000-0000-0000-000000000001");
    private static readonly Guid Acme = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Globex = Guid.Parse("a0000000-0000-0000-0000-00000000000b");

    private static NamespacePlacement Placement(
        string ns, Guid appId, string appName, Guid? customerId, string customerName,
        string? environment = "Production") =>
        new(Cluster, ns, appId, appName, customerId, customerName, environment);

    private static NamespaceOwner Owner(params NamespacePlacement[] placements) =>
        CostReportService.OwnersByNamespace(placements)[(Cluster, placements[0].Namespace)];

    /// <summary>
    /// ⚠️ THE DEFECT. Two apps, two customers, one namespace — and the cost may be charged to
    /// neither of them, because there is no measurement that could divide it.
    /// </summary>
    [Fact]
    public void A_namespace_two_customers_share_is_attributed_to_neither()
    {
        NamespaceOwner owner = Owner(
            Placement("shared", Guid.NewGuid(), "api", Acme, "Acme Ltd"),
            Placement("shared", Guid.NewGuid(), "worker", Globex, "Globex Inc"));

        owner.CustomerId.Should().BeNull(
            "whichever row came first used to be charged the whole namespace, and nothing pins "
            + "that order");
        owner.CustomerName.Should().BeNull("or the panel labels it with a customer who owes nothing");

        owner.Apps.Should().HaveCount(2, "both apps are still named — only the attribution is refused");
    }

    /// <summary>
    /// ⚠️ <b>THE POSITIVE CONTROL.</b> Without it, "never attribute a shared namespace" would pass
    /// the fact above — and that would un-bill every namespace two of one customer's own apps share,
    /// which is a far larger wrong than the defect.
    /// </summary>
    [Fact]
    public void A_namespace_two_apps_of_ONE_customer_share_is_still_that_customers()
    {
        NamespaceOwner owner = Owner(
            Placement("shared", Guid.NewGuid(), "api", Acme, "Acme Ltd"),
            Placement("shared", Guid.NewGuid(), "worker", Acme, "Acme Ltd"));

        owner.CustomerId.Should().Be(Acme, "one customer owns both apps, so the cost is theirs");
        owner.CustomerName.Should().Be("Acme Ltd");

        owner.Apps.Should().HaveCount(
            2, "but it still cannot be split between their two apps, so both are named");
    }

    [Fact]
    public void The_ordinary_case_is_unchanged()
    {
        NamespaceOwner owner = Owner(Placement("acme-prod", Guid.NewGuid(), "api", Acme, "Acme Ltd"));

        owner.CustomerId.Should().Be(Acme);
        owner.CustomerName.Should().Be("Acme Ltd");
        owner.Apps.Should().ContainSingle();
        owner.EnvironmentName.Should().Be("Production");
    }

    /// <summary>
    /// ⚠️ The order must not matter, which is the whole complaint. Asserted by reversing it: the
    /// old code gave two different answers for the same two deployments.
    /// </summary>
    [Fact]
    public void Reversing_the_deployments_gives_the_same_answer()
    {
        NamespacePlacement acme = Placement("shared", Guid.NewGuid(), "api", Acme, "Acme Ltd");
        NamespacePlacement globex = Placement("shared", Guid.NewGuid(), "worker", Globex, "Globex Inc");

        NamespaceOwner oneWay = Owner(acme, globex);
        NamespaceOwner theOther = Owner(globex, acme);

        oneWay.CustomerId.Should().Be(theOther.CustomerId);
        oneWay.CustomerName.Should().Be(theOther.CustomerName);
    }

    /// <summary>
    /// A namespace with no customer at all — an app whose customer could not be resolved — stays
    /// unattributed, and must not be confused with the two-customer case by a count of one
    /// DISTINCT value that happens to be null.
    /// </summary>
    [Fact]
    public void A_namespace_whose_app_has_no_customer_is_unattributed()
    {
        NamespaceOwner owner = Owner(Placement("orphan", Guid.NewGuid(), "api", null, ""));

        owner.CustomerId.Should().BeNull();
    }

    /// <summary>
    /// ⚠️ The nastier shape: one deployment resolves a customer and the other does not. That is
    /// still two distinct values, so it is still unattributed — charging the resolvable one for the
    /// whole namespace would be the same defect wearing a different hat.
    /// </summary>
    [Fact]
    public void One_resolvable_customer_and_one_not_is_also_attributed_to_neither()
    {
        NamespaceOwner owner = Owner(
            Placement("shared", Guid.NewGuid(), "api", Acme, "Acme Ltd"),
            Placement("shared", Guid.NewGuid(), "mystery", null, ""));

        owner.CustomerId.Should().BeNull();
        owner.Apps.Should().HaveCount(2);
    }

    /// <summary>The two rules that were already right, pinned so the new one cannot disturb them.</summary>
    [Fact]
    public void A_namespace_serving_two_environments_still_belongs_to_neither()
    {
        NamespaceOwner owner = Owner(
            Placement("shared", Guid.NewGuid(), "api", Acme, "Acme Ltd", environment: "Production"),
            Placement("shared", Guid.NewGuid(), "api-staging", Acme, "Acme Ltd", environment: "Staging"));

        owner.EnvironmentName.Should().BeNull();
        owner.CustomerId.Should().Be(Acme, "one customer, so the attribution stands");
    }

    /// <summary>
    /// ⚠️ The measure. Every fact above reads one entry out of the dictionary, so all of them
    /// would pass against a method that returned a single hard-coded owner. This checks the
    /// grouping itself: two namespaces on one cluster are two entries, and the same namespace name
    /// on two clusters is not one entry.
    /// </summary>
    [Fact]
    public void The_grouping_is_per_cluster_and_per_namespace()
    {
        Guid other = Guid.Parse("c1000000-0000-0000-0000-000000000002");

        var owners = CostReportService.OwnersByNamespace(
        [
            Placement("one", Guid.NewGuid(), "api", Acme, "Acme Ltd"),
            Placement("two", Guid.NewGuid(), "worker", Acme, "Acme Ltd"),
            new(other, "one", Guid.NewGuid(), "api", Globex, "Globex Inc", "Production"),
        ]);

        owners.Should().HaveCount(3);
        owners[(Cluster, "one")].CustomerId.Should().Be(Acme);
        owners[(other, "one")].CustomerId.Should().Be(
            Globex, "the same namespace name on another cluster is a different namespace");
    }
}
