using System.Reflection;
using EntKube.Web.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Module = EntKube.Web.Modules.Module;

namespace EntKube.Web.Tests;

/// <summary>
/// Every service has an owning module, or is named as platform wiring.
///
/// <para><b>Why this is a test.</b> <c>Program.cs</c> had 188 registrations in one run with
/// nothing saying which subsystem any of them belonged to, which is how a composition root
/// becomes a place things are appended to rather than a description of the system. Splitting
/// it by module only helps while the split stays true, and a new service does not put itself
/// in the right file.</para>
/// </summary>
public class ModuleCompositionTests
{
    /// <summary>Every module composition class, by the module it serves.</summary>
    private static Dictionary<Module, Type> CompositionClasses()
    {
        Dictionary<Module, Type> found = [];

        foreach (Module module in Enum.GetValues<Module>())
        {
            Type? type = typeof(ModuleMap).Assembly
                .GetType($"EntKube.Web.Modules.Composition.{module}Services");

            type.Should().NotBeNull($"module {module} needs a {module}Services composition class");
            found[module] = type!;
        }

        return found;
    }

    [Fact]
    public void Every_module_has_a_composition_class_with_an_Add_method()
    {
        foreach ((Module module, Type type) in CompositionClasses())
        {
            type.GetMethod($"Add{module}Module", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Should().NotBeNull($"{type.Name} must expose Add{module}Module so Program.cs can call it");
        }
    }

    /// <summary>
    /// The registrations a module's composition class actually performs, by running it
    /// against a bare collection. Reading the file would prove nothing about what runs.
    /// </summary>
    private static List<ServiceDescriptor> RegistrationsOf(Module module, Type composition)
    {
        ServiceCollection services = [];

        composition
            .GetMethod($"Add{module}Module", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [services]);

        return [.. services];
    }

    [Fact]
    public void A_module_registers_only_services_it_owns()
    {
        List<string> wrong = [];

        foreach ((Module module, Type composition) in CompositionClasses())
        {
            foreach (ServiceDescriptor d in RegistrationsOf(module, composition))
            {
                Type implementation = d.ImplementationType ?? d.ServiceType;
                Module? owner = ModuleMap.ServiceOwner(implementation);

                if (owner is null)
                {
                    wrong.Add($"{implementation.Name} is registered by {module} but has no owner in ModuleMap.Services");
                }
                else if (owner != module)
                {
                    wrong.Add($"{implementation.Name} is registered by {module} but ModuleMap says {owner} owns it");
                }
            }
        }

        string.Join("; ", wrong).Should().BeEmpty(
            "a service registered by a module that does not own it is the composition root "
            + "drifting away from the module map — pick one and correct the other");
    }

    /// <summary>
    /// Owned by a module, yet impossible for the container to construct — so "mapped" and
    /// "registered" come apart here, and they are different facts.
    ///
    /// <para><c>TelemetrySegmentBlobStore</c> takes a <c>Guid tenantId</c> in its constructor and
    /// is built by hand in <c>SegmentManagerRegistry</c>, one instance per tenant. No
    /// registration can express that, so no test of the container could ever have found it — and
    /// it reaches <see cref="ApplicationDbContext"/> out of a scope, which is exactly the thing
    /// the module contexts exist to stop.</para>
    ///
    /// <para>⚠️ It was unmapped for that reason, and being unmapped made it <b>invisible to every
    /// per-module count</b> rather than counted as zero. Keeping the mapping and exempting it
    /// here is the honest arrangement: ownership says whose code it is, registration says how it
    /// is built, and conflating the two is what hid it. This list may only hold types the
    /// container genuinely cannot build.</para>
    /// </summary>
    private static readonly IReadOnlySet<Type> CannotBeContainerRegistered = new HashSet<Type>
    {
        typeof(EntKube.Web.Services.Telemetry.TelemetrySegmentBlobStore),
    };

    [Fact]
    public void Every_mapped_service_is_actually_registered_by_its_module()
    {
        HashSet<Type> registered = [];

        foreach ((Module module, Type composition) in CompositionClasses())
        {
            foreach (ServiceDescriptor d in RegistrationsOf(module, composition))
            {
                registered.Add(d.ImplementationType ?? d.ServiceType);
            }
        }

        List<string> orphaned =
        [
            .. ModuleMap.Services.Keys
                .Where(t => !registered.Contains(t))
                .Where(t => !CannotBeContainerRegistered.Contains(t))
                .Select(t => t.Name)
                .Order()
        ];

        string.Join(", ", orphaned).Should().BeEmpty(
            "these are mapped to a module but nothing registers them, so either the mapping "
            + "is dead weight or a service quietly stopped being wired up");
    }

    [Fact]
    public void Nothing_is_both_owned_by_a_module_and_declared_platform()
    {
        ModuleMap.Services.Keys.Intersect(ModuleMap.PlatformServices)
            .Select(t => t.Name)
            .Should().BeEmpty("a service is either a module's or it is platform wiring, not both");
    }
}
