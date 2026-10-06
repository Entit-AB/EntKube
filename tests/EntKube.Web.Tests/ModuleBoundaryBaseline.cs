using EntKube.Web.Modules;

namespace EntKube.Web.Tests;

public partial class ModuleBoundaryTests
{
    /// <summary>
    /// Every foreign key crossing a module boundary, as measured on 2026-10-05 against
    /// 2026-10-05: <b>160 of them across 38 module pairs</b>, down from 164/39 when the
    /// boundaries were first drawn.
    ///
    /// <para>This is the bill for separating any two modules' databases, and it is the
    /// reason <c>docs/decomposition.md</c> argues against doing that for most of them.
    /// Treat the numbers as a ceiling that only comes down.</para>
    ///
    /// <para><b>Reading the big ones.</b> Most of the weight points at Identity, and nearly
    /// all of that is <c>TenantId</c> and <c>CustomerId</c> — tenancy is universal and those
    /// edges are permanent.</para>
    ///
    /// <para><b>Delivery→DataServices was 15 and is now 5.</b> The service bindings were
    /// assigned to Delivery when the boundaries were drawn on reading alone. Measuring who
    /// actually uses them said otherwise — DataServices touches <c>DatabaseBinding</c> 11
    /// times to Delivery's 2, and <c>ElasticsearchBinding</c> 9 times to Delivery's none —
    /// and so does the definition: a binding exists so credentials get synced into an app's
    /// namespace, which is DataServices' work. They moved, and <c>IdentityBinding</c> went
    /// to Identity for the same reason.</para>
    /// </summary>
    private static Dictionary<(Module From, Module To), int> BaselineEdges() => new()
    {
        [(Module.Advisor, Module.Identity)] = 2,
        [(Module.Catalog, Module.Delivery)] = 1,
        [(Module.Catalog, Module.Fleet)] = 1,
        [(Module.Catalog, Module.Identity)] = 1,
        [(Module.Connectivity, Module.Catalog)] = 2,
        [(Module.Connectivity, Module.Delivery)] = 7,
        [(Module.Connectivity, Module.Fleet)] = 1,
        [(Module.Connectivity, Module.Identity)] = 1,
        [(Module.Cost, Module.Fleet)] = 3,
        [(Module.Cost, Module.Identity)] = 4,
        [(Module.DataServices, Module.Catalog)] = 4,
        [(Module.DataServices, Module.Delivery)] = 10,
        [(Module.DataServices, Module.Fleet)] = 9,
        [(Module.DataServices, Module.Identity)] = 11,
        [(Module.DataServices, Module.Secrets)] = 1,
        [(Module.Delivery, Module.Connectivity)] = 1,
        [(Module.Delivery, Module.DataServices)] = 5,
        [(Module.Delivery, Module.Fleet)] = 1,
        [(Module.Delivery, Module.Identity)] = 11,
        [(Module.Fleet, Module.Delivery)] = 1,
        [(Module.Fleet, Module.Identity)] = 5,
        [(Module.Identity, Module.Catalog)] = 1,
        [(Module.Identity, Module.DataServices)] = 4,
        [(Module.Identity, Module.Delivery)] = 5,
        [(Module.Identity, Module.Fleet)] = 1,
        [(Module.Mail, Module.Catalog)] = 1,
        [(Module.Mail, Module.Identity)] = 1,
        [(Module.Secrets, Module.Catalog)] = 1,
        [(Module.Secrets, Module.Connectivity)] = 1,
        [(Module.Secrets, Module.DataServices)] = 9,
        [(Module.Secrets, Module.Delivery)] = 4,
        [(Module.Secrets, Module.Fleet)] = 2,
        [(Module.Secrets, Module.Identity)] = 3,
        [(Module.Secrets, Module.Support)] = 1,
        [(Module.Support, Module.Delivery)] = 11,
        [(Module.Support, Module.Identity)] = 28,
        [(Module.Telemetry, Module.Fleet)] = 2,
        [(Module.Telemetry, Module.Identity)] = 3,
    };
}
