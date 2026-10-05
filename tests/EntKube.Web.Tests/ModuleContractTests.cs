using System.Reflection;
using EntKube.Contracts.Advisor;
using EntKube.Web.Data;
using EntKube.Web.Modules;
using FluentAssertions;
using Module = EntKube.Web.Modules.Module;

namespace EntKube.Web.Tests;

/// <summary>
/// The contracts stay contracts, and the god context keeps shrinking.
///
/// <para>Phase 0 gave every table and every service an owning module, and gave each module
/// a context that exposes only its own tables. None of that matters while the services
/// still take <see cref="ApplicationDbContext"/>, which can see everything — the boundary
/// exists but nothing is standing behind it yet. The ratchet below is how that gets walked
/// down deliberately instead of hopefully.</para>
/// </summary>
public class ModuleContractTests
{
    /// <summary>
    /// <see cref="EntKube.Contracts"/> must never be able to see the web application.
    ///
    /// <para>If it could, the contracts would start returning EF entities and quietly
    /// become a second name for the database — which is exactly the coupling the
    /// decomposition is trying to undo. It is also referenced by things that cannot take
    /// the whole web application with them: the CLI, the MCP server, a generated HTTP
    /// client, and eventually a separate deployable.</para>
    /// </summary>
    [Fact]
    public void The_contracts_project_cannot_see_the_web_application()
    {
        IEnumerable<string> referenced = typeof(IAdvisorApi).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("EntKube", StringComparison.Ordinal));

        string.Join(", ", referenced).Should().BeEmpty(
            "EntKube.Contracts must depend on no EntKube assembly at all — see its README");
    }

    /// <summary>
    /// Nothing a contract hands out may be a type from the web application, which in
    /// practice means an EF entity. An entity carries navigation properties and a change
    /// tracker; handing one across a boundary hands over a live route into the database.
    /// </summary>
    [Fact]
    public void No_contract_exposes_a_type_from_the_web_application()
    {
        List<string> leaks = [];

        foreach (Type contract in typeof(IAdvisorApi).Assembly.GetTypes().Where(t => t.IsInterface))
        {
            foreach (MethodInfo method in contract.GetMethods())
            {
                IEnumerable<Type> touched = method.GetParameters().Select(p => p.ParameterType)
                    .Append(method.ReturnType)
                    .SelectMany(t => t.IsGenericType ? t.GetGenericArguments().Append(t) : [t]);

                foreach (Type t in touched.Where(t => t.Namespace?.StartsWith("EntKube.Web", StringComparison.Ordinal) == true))
                {
                    leaks.Add($"{contract.Name}.{method.Name} -> {t.Name}");
                }
            }
        }

        string.Join("; ", leaks).Should().BeEmpty("a contract must carry its own types");
    }

    /// <summary>Services still taking the all-seeing context, by module.</summary>
    private static Dictionary<Module, int> GodContextUsers()
    {
        Dictionary<Module, int> counts = [];

        foreach ((Type service, Module module) in ModuleMap.Services)
        {
            bool takesIt = service.GetConstructors().Any(c => c.GetParameters().Any(p =>
                p.ParameterType == typeof(ApplicationDbContext)
                || (p.ParameterType.IsGenericType
                    && p.ParameterType.GetGenericArguments().Length == 1
                    && p.ParameterType.GetGenericArguments()[0] == typeof(ApplicationDbContext))));

            if (takesIt)
            {
                counts[module] = counts.GetValueOrDefault(module) + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// How many services each module still routes through <see cref="ApplicationDbContext"/>,
    /// as it stood when the first module contract was written — <b>117 of 176</b>.
    ///
    /// <para>This is the decomposition's actual remaining work, as a number. Every one of
    /// these is a service that can still read any table in the product. Moving one onto its
    /// module's context is what makes the boundary real for that service, and the number
    /// here comes down with it.</para>
    /// </summary>
    private static readonly Dictionary<Module, int> Baseline = new()
    {
        [Module.Telemetry] = 21,
        [Module.Support] = 15,
        [Module.DataServices] = 13,
        [Module.Identity] = 12,
        [Module.Fleet] = 12,
        [Module.Catalog] = 12,
        [Module.Delivery] = 12,
        [Module.Connectivity] = 11,
        [Module.Cost] = 4,
        [Module.Mail] = 2,
        [Module.Secrets] = 2,
        [Module.Advisor] = 1,
    };

    [Fact]
    public void No_module_adds_another_service_on_the_all_seeing_context()
    {
        Dictionary<Module, int> actual = GodContextUsers();

        List<string> regressions =
        [
            .. actual
                .Where(a => a.Value > Baseline.GetValueOrDefault(a.Key))
                .Select(a => $"{a.Key}: {a.Value} (was {Baseline.GetValueOrDefault(a.Key)})")
                .Order()
        ];

        string.Join("; ", regressions).Should().BeEmpty(
            "a new service on ApplicationDbContext can read every table in the product, "
            + "which is the thing the module contexts exist to stop. Take the module's own "
            + "context instead — and if it genuinely needs another module's data, that is "
            + "what the contract in EntKube.Contracts is for.");
    }

    [Fact]
    public void The_ratchet_does_not_carry_slack()
    {
        Dictionary<Module, int> actual = GodContextUsers();

        List<string> slack =
        [
            .. Baseline
                .Where(b => actual.GetValueOrDefault(b.Key) < b.Value)
                .Select(b => $"{b.Key}: now {actual.GetValueOrDefault(b.Key)}, baseline still {b.Value}")
                .Order()
        ];

        string.Join("; ", slack).Should().BeEmpty(
            "this module has moved a service off the all-seeing context — bank it by "
            + "lowering the baseline, or the slack lets it come back for free");
    }
}
