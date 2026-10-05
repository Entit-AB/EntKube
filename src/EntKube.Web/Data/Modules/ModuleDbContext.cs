using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Data.Modules;

/// <summary>
/// Base for the per-module contexts. Builds the same model as
/// <see cref="ApplicationDbContext"/> — see <see cref="EntKubeModel"/> for why — and leaves
/// it to each subclass to decide what it exposes.
///
/// <para><b>What a module context is for.</b> A service that takes a
/// <see cref="SupportDbContext"/> can reach tickets, contracts and worked hours and nothing
/// else: there is no <c>Apps</c> property on it, so a query against another module's table
/// does not compile. That is the boundary the decomposition needs, and it is enforced by the
/// type system rather than by a reviewer noticing.</para>
///
/// <para><b>What it is not, yet.</b> The model still contains every entity, so a navigation
/// property that already crosses a module boundary — there are 94 of those — can still be
/// walked with <c>Include</c> from an entity the context does own. Starting a query from
/// another module's table is prevented; following an existing foreign key out of your own is
/// not. Closing that needs the cross-module reads to go through service contracts first,
/// which is the next phase of the plan, not this one.</para>
///
/// <para>Migrations are generated from <see cref="ApplicationDbContext"/> only. These
/// contexts share its connection and its migration history; none of them owns a schema.</para>
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options)
    : Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext<ApplicationUser>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        EntKubeModel.ConfigureAll(builder);
    }
}
