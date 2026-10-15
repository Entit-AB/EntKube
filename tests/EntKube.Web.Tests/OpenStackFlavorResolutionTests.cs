using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// A configured flavor becomes the id Nova actually accepts.
///
/// <para><b>The bug this covers was live and read as something else entirely.</b> Nova's
/// <c>flavorRef</c> resolves <em>ids</em>, not names, and the bootstrap VM passed the configured
/// name straight through — with a comment asserting that Nova took either. OpenStack answers that
/// with <c>400 badRequest: Flavor 4C-8GB-100GB could not be found.</c>, which reads as though the
/// flavor is missing from the project. It was not: the picker lists exactly what Nova reports, and
/// Cleura really does name flavors that way. The flavor was fine; the field was wrong.</para>
///
/// <para>The image path had always resolved its id. The asymmetry between <c>imageRef</c> and
/// <c>flavorRef</c> is what made this survive — one of them was right, so the shape looked
/// deliberate.</para>
/// </summary>
public class OpenStackFlavorResolutionTests
{
    // Shaped like Cleura's, because that is where this failed and the names are the point.
    private static readonly OpenStackFlavor[] Catalog =
    [
        new("8a1b2c3d-0001", "2C-4GB-50GB", 2, 4096, 50),
        new("8a1b2c3d-0002", "4C-8GB-100GB", 4, 8192, 100),
        new("8a1b2c3d-0003", "8C-16GB-200GB", 8, 16384, 200),
    ];

    [Fact]
    public void A_flavor_name_resolves_to_its_id()
        => OpenStackComputeService.ResolveFlavorId(Catalog, "4C-8GB-100GB")
            .Should().Be("8a1b2c3d-0002", "this is the value Nova's flavorRef accepts");

    [Fact]
    public void A_flavor_id_is_passed_through()
        => OpenStackComputeService.ResolveFlavorId(Catalog, "8a1b2c3d-0003")
            .Should().Be("8a1b2c3d-0003", "a hand-edited configuration holding an id keeps working");

    [Fact]
    public void Case_does_not_decide_whether_a_cluster_provisions()
        => OpenStackComputeService.ResolveFlavorId(Catalog, "4c-8gb-100gb")
            .Should().Be("8a1b2c3d-0002");

    /// <summary>
    /// The message has to name what is available, because OpenStack's own does not — and the usual
    /// cause is a saved configuration naming a flavor from a different region, where "could not be
    /// found" is true but unhelpful.
    /// </summary>
    [Fact]
    public void An_unknown_flavor_says_what_is_available()
    {
        Action act = () => OpenStackComputeService.ResolveFlavorId(Catalog, "16C-64GB-500GB");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*16C-64GB-500GB*not available in this region*")
            .WithMessage("*2C-4GB-50GB*4C-8GB-100GB*8C-16GB-200GB*");
    }

    [Fact]
    public void An_empty_catalog_says_so_rather_than_listing_nothing()
    {
        Action act = () => OpenStackComputeService.ResolveFlavorId([], "4C-8GB-100GB");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*none are visible to this project*");
    }
}
