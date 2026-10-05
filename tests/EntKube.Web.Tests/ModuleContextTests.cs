using System.Reflection;
using Module = EntKube.Web.Modules.Module;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Modules;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EntKube.Web.Tests;

/// <summary>
/// The per-module contexts expose their own module and nothing else, and every one of them
/// builds exactly the model <see cref="ApplicationDbContext"/> does.
///
/// <para><b>Why the second half matters as much as the first.</b> Twelve contexts over one
/// database is only safe while they agree about the mapping. If one of them built a subtly
/// different model — a missing conversion, an index configured on one side only — the
/// disagreement would not show up at startup. It would show up as a query that works
/// through one context and fails, or silently returns something else, through another.</para>
/// </summary>
public class ModuleContextTests
{
    /// <summary>Every module context type, paired with the module it serves.</summary>
    public static TheoryData<Module, Type> Contexts()
    {
        TheoryData<Module, Type> data = [];

        foreach (Module module in Enum.GetValues<Module>())
        {
            Type? context = typeof(ModuleDbContext).Assembly
                .GetType($"EntKube.Web.Data.Modules.{module}DbContext");

            context.Should().NotBeNull($"module {module} needs a {module}DbContext");
            data.Add(module, context!);
        }

        return data;
    }

    private static DbContext Create(Type contextType)
    {
        Type optionsType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        dynamic builder = Activator.CreateInstance(optionsType)!;
        SqliteDbContextOptionsBuilderExtensions.UseSqlite(
            (DbContextOptionsBuilder)builder, "DataSource=:memory:");

        return (DbContext)Activator.CreateInstance(contextType, (object)builder.Options)!;
    }

    /// <summary>The entity types a context hands out directly, via DbSet or IQueryable.</summary>
    private static (HashSet<Type> Writable, HashSet<Type> Readable) Surface(Type contextType)
    {
        HashSet<Type> writable = [], readable = [];

        foreach (PropertyInfo p in contextType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!p.PropertyType.IsGenericType)
            {
                continue;
            }

            Type open = p.PropertyType.GetGenericTypeDefinition();
            Type arg = p.PropertyType.GetGenericArguments()[0];

            if (open == typeof(DbSet<>)) writable.Add(arg);
            else if (open == typeof(IQueryable<>)) readable.Add(arg);
        }

        return (writable, readable);
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void A_module_context_exposes_exactly_the_tables_its_module_owns(Module module, Type contextType)
    {
        HashSet<Type> owned = [.. ModuleMap.Entities.Where(e => e.Value == module).Select(e => e.Key)];
        (HashSet<Type> writable, _) = Surface(contextType);

        string missing = string.Join(", ", owned.Except(writable).Select(t => t.Name).Order());
        string extra = string.Join(", ", writable.Except(owned).Select(t => t.Name).Order());

        missing.Should().BeEmpty(
            $"{module} owns these tables in ModuleMap but {contextType.Name} does not expose them");
        extra.Should().BeEmpty(
            $"{contextType.Name} exposes tables {module} does not own — that is the boundary "
            + "leaking, and it is a compile-time affordance to cross it");
    }

    /// <summary>
    /// A module may read the four hub tables and may not write them. Writing another
    /// module's table through a side door is exactly what the boundary is for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contexts))]
    public void Hub_tables_a_module_does_not_own_are_readable_but_never_writable(Module module, Type contextType)
    {
        (HashSet<Type> writable, HashSet<Type> readable) = Surface(contextType);

        foreach (Type hub in readable)
        {
            ModuleMap.Owner(hub).Should().NotBe(module,
                $"{hub.Name} belongs to {module}, so it should be an ordinary DbSet, not a read-only hub");

            writable.Should().NotContain(hub,
                $"{contextType.Name} exposes {hub.Name} both ways; the read-only hub is pointless if it is also writable");
        }
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Every_module_context_builds_the_same_model_as_the_application_context(Module module, Type contextType)
    {
        using ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("DataSource=:memory:").Options);

        using DbContext moduleContext = Create(contextType);

        moduleContext.Model.ToDebugString(MetadataDebugStringOptions.LongDefault)
            .Should().Be(
                application.Model.ToDebugString(MetadataDebugStringOptions.LongDefault),
                $"{module} must map the database identically to ApplicationDbContext — they "
                + "share one schema and one migration history, and a disagreement between "
                + "them would surface as a query that works through one context and not the other");
    }
}
