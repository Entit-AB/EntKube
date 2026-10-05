using EntKube.Web.Data;
using EntKube.Web.Modules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EntKube.Web.Tests;

/// <summary>
/// Holds the module map to the EF model, and ratchets how much the modules reach across
/// each other. See <c>docs/decomposition.md</c>.
///
/// <para><b>Why a test and not a convention.</b> A module boundary that is only written
/// down is a folder name. The whole plan for splitting EntKube rests on the boundaries
/// being real before anything moves, and the cheapest moment to notice a new cross-module
/// foreign key is the commit that adds it — not the day somebody tries to lift one module
/// into its own process and finds sixty joins in the way. The same reasoning, and the same
/// technique, as <see cref="BackupCoverageTests"/>: walk the model, compare against a
/// hand-written list, fail when they disagree.</para>
///
/// <para><b>The coupling numbers are a baseline, not a target of zero.</b> Some
/// cross-module references are permanent and correct — every tenant-owned table points at
/// Tenant, which Identity owns. What matters is that the number only goes down, and that
/// going up is a decision somebody made on purpose rather than a thing that happened.</para>
/// </summary>
public partial class ModuleBoundaryTests
{
    private static IModel Model()
    {
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("DataSource=:memory:").Options);

        return db.Model;
    }

    /// <summary>
    /// Entity types in the model that no module needs to own: ASP.NET Identity's own
    /// tables, which belong to the framework rather than to us.
    /// </summary>
    private static bool IsFrameworkOwned(IEntityType e) =>
        e.ClrType.Namespace?.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal) == true;

    private static IEnumerable<IEntityType> OwnableEntities() =>
        Model().GetEntityTypes()
            .Where(e => !e.IsOwned())
            .Where(e => !IsFrameworkOwned(e))
            .DistinctBy(e => e.ClrType);

    [Fact]
    public void Every_table_has_a_module_that_owns_it()
    {
        List<string> unassigned =
        [
            .. OwnableEntities()
                .Where(e => ModuleMap.Owner(e.ClrType) is null)
                .Select(e => e.ClrType.Name)
                .Order()
        ];

        // Joined rather than asserted as a collection so one run names every orphan.
        string.Join(", ", unassigned).Should().BeEmpty(
            "a table with no owning module is how the decomposition rots: it belongs to "
            + "whoever touched it last. Add it to ModuleMap.Entities and pick a module.");
    }

    [Fact]
    public void Nothing_is_mapped_that_the_model_no_longer_has()
    {
        HashSet<Type> inModel = [.. OwnableEntities().Select(e => e.ClrType)];

        List<string> stale =
        [
            .. ModuleMap.Entities.Keys
                .Where(t => !inModel.Contains(t))
                .Select(t => t.Name)
                .Order()
        ];

        string.Join(", ", stale).Should().BeEmpty(
            "a mapping for a table that is gone is dead weight, and hides the fact that "
            + "its module lost a responsibility");
    }

    /// <summary>
    /// Every foreign key that crosses a module boundary, as (owner of the dependent table)
    /// → (owner of the principal table). These are the joins that would become network
    /// calls if the two modules were ever split into separate databases, so the count is
    /// the real cost of that split and worth watching.
    /// </summary>
    private static Dictionary<(Module From, Module To), int> CrossModuleForeignKeys()
    {
        Dictionary<(Module, Module), int> edges = [];

        foreach (IEntityType entity in OwnableEntities())
        {
            if (ModuleMap.Owner(entity.ClrType) is not Module from)
            {
                continue;
            }

            foreach (IForeignKey fk in entity.GetForeignKeys())
            {
                Type principal = fk.PrincipalEntityType.ClrType;

                if (ModuleMap.Owner(principal) is not Module to || to == from)
                {
                    continue;
                }

                edges[(from, to)] = edges.GetValueOrDefault((from, to)) + 1;
            }
        }

        return edges;
    }

    /// <summary>
    /// The cross-module foreign keys as they stood when the boundaries were drawn
    /// (2026-10-05, commit 7357fb2). A pair may shrink or vanish; it may not grow, and a
    /// pair not listed here may not appear, without someone deciding to update this.
    /// </summary>
    private static readonly Dictionary<(Module From, Module To), int> Baseline = BaselineEdges();

    [Fact]
    public void No_module_reaches_further_into_another_than_it_already_did()
    {
        Dictionary<(Module From, Module To), int> actual = CrossModuleForeignKeys();

        List<string> regressions =
        [
            .. actual
                .Where(e => e.Value > Baseline.GetValueOrDefault(e.Key))
                .Select(e => $"{e.Key.From}→{e.Key.To}: {e.Value} (was {Baseline.GetValueOrDefault(e.Key)})")
                .Order()
        ];

        string.Join("; ", regressions).Should().BeEmpty(
            "a new foreign key across a module boundary is a join that has to become a "
            + "network call if these modules are ever separated. That may well be the "
            + "right call — but make it on purpose and raise the number here, rather than "
            + "letting the boundary erode quietly.");
    }

    /// <summary>
    /// The baseline has to stay honest in the other direction too. A pair that has been
    /// reduced should have its number brought down, or the slack silently permits the
    /// coupling to come back.
    /// </summary>
    [Fact]
    public void The_baseline_does_not_carry_slack()
    {
        Dictionary<(Module From, Module To), int> actual = CrossModuleForeignKeys();

        List<string> slack =
        [
            .. Baseline
                .Where(b => actual.GetValueOrDefault(b.Key) < b.Value)
                .Select(b => $"{b.Key.From}→{b.Key.To}: now {actual.GetValueOrDefault(b.Key)}, baseline still {b.Value}")
                .Order()
        ];

        string.Join("; ", slack).Should().BeEmpty(
            "this coupling has been reduced — lower the baseline so it cannot return for free");
    }
}
