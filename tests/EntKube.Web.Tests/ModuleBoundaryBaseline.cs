using EntKube.Web.Modules;

namespace EntKube.Web.Tests;

public partial class ModuleBoundaryTests
{
    /// <summary>
    /// Every foreign key crossing a module boundary, as measured on 2026-10-05 against
    /// commit 7357fb2: <b>164 of them across 39 module pairs</b>.
    ///
    /// <para>This is the bill for separating any two modules' databases, and it is the
    /// reason <c>docs/decomposition.md</c> argues against doing that for most of them.
    /// Treat the numbers as a ceiling that only comes down.</para>
    ///
    /// <para><b>Reading the big ones.</b> Most of the weight is pointing at Identity, and
    /// nearly all of that is <c>TenantId</c> and <c>CustomerId</c> — tenancy is genuinely
    /// universal and those edges are permanent. The ones that say something about design
    /// are Delivery→DataServices (15: the service bindings) and Support→Delivery (11),
    /// because those are the joins a Support or DataServices extraction would have to pay
    /// for.</para>
    /// </summary>
    private static Dictionary<(Module From, Module To), int> BaselineEdges() => new()
    {
        [(Module.Identity, Module.Fleet)] = 1,
        [(Module.Identity, Module.Catalog)] = 1,
        [(Module.Identity, Module.DataServices)] = 4,
        [(Module.Identity, Module.Delivery)] = 4,

        [(Module.Fleet, Module.Identity)] = 5,
        [(Module.Fleet, Module.Delivery)] = 1,

        [(Module.Catalog, Module.Identity)] = 1,
        [(Module.Catalog, Module.Fleet)] = 1,
        [(Module.Catalog, Module.Delivery)] = 1,

        [(Module.DataServices, Module.Identity)] = 11,
        [(Module.DataServices, Module.Fleet)] = 9,
        [(Module.DataServices, Module.Catalog)] = 3,
        [(Module.DataServices, Module.Delivery)] = 4,
        [(Module.DataServices, Module.Secrets)] = 1,

        [(Module.Mail, Module.Identity)] = 1,
        [(Module.Mail, Module.Catalog)] = 1,

        [(Module.Delivery, Module.Identity)] = 12,
        [(Module.Delivery, Module.Fleet)] = 1,
        [(Module.Delivery, Module.Catalog)] = 1,
        [(Module.Delivery, Module.DataServices)] = 15,
        [(Module.Delivery, Module.Connectivity)] = 1,

        [(Module.Connectivity, Module.Identity)] = 1,
        [(Module.Connectivity, Module.Fleet)] = 1,
        [(Module.Connectivity, Module.Catalog)] = 2,
        [(Module.Connectivity, Module.Delivery)] = 7,

        [(Module.Secrets, Module.Identity)] = 3,
        [(Module.Secrets, Module.Fleet)] = 2,
        [(Module.Secrets, Module.Catalog)] = 1,
        [(Module.Secrets, Module.DataServices)] = 9,
        [(Module.Secrets, Module.Delivery)] = 4,
        [(Module.Secrets, Module.Connectivity)] = 1,
        [(Module.Secrets, Module.Support)] = 1,

        [(Module.Telemetry, Module.Identity)] = 3,
        [(Module.Telemetry, Module.Fleet)] = 2,

        [(Module.Cost, Module.Identity)] = 4,
        [(Module.Cost, Module.Fleet)] = 3,

        [(Module.Support, Module.Identity)] = 28,
        [(Module.Support, Module.Delivery)] = 11,

        [(Module.Advisor, Module.Identity)] = 2,
    };
}
