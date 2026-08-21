using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for how the S3 endpoint is worked out.
///
/// This used to be templated from the region name alone, which produced a
/// hostname that does not resolve for any region whose name is not a bare code.
/// The failure landed after authentication had already succeeded, so it read as a
/// network fault rather than the configuration mistake it was — and behind an
/// egress agent it looked like the agent was at fault.
///
/// The service catalog is authoritative and is now consulted first.
/// </summary>
public class OpenStackEndpointResolutionTests
{
    private static KeystoneSession Session(params (string Type, string Url)[] endpoints) => new()
    {
        Token = "token",
        UserId = "user",
        ProjectId = "project",
        Endpoints = endpoints.ToDictionary(e => e.Type, e => e.Url, StringComparer.OrdinalIgnoreCase)
    };

    private static OpenStackConnection Connection(string? region = null, string? s3Endpoint = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Name = "Cleura",
        AuthUrl = "https://identity.example.com:5000/v3",
        Region = region,
        S3Endpoint = s3Endpoint
    };

    // ──────── The regression ────────

    [Fact]
    public void A_region_name_is_never_used_when_the_catalog_advertises_an_object_store()
    {
        // The exact shape that broke: region "Sto-Com" templated to
        // s3-sto-com.citycloud.com, which does not exist.
        OpenStackConnection connection = Connection(region: "Sto-Com");
        KeystoneSession session = Session(
            ("object-store", "https://s3-sto2.citycloud.com/swift/v1/AUTH_abc123"));

        string endpoint = OpenStackS3Service.ResolveS3Endpoint(connection, session);

        endpoint.Should().Be("https://s3-sto2.citycloud.com");
        endpoint.Should().NotContain("sto-com");
    }

    [Fact]
    public void The_swift_path_is_stripped_to_leave_the_s3_origin()
    {
        // Ceph RGW serves both APIs on one host; the catalog advertises the Swift
        // path, and S3 lives at the root of the same origin.
        KeystoneSession session = Session(
            ("object-store", "https://s3-kna1.citycloud.com/swift/v1/AUTH_deadbeef"));

        OpenStackS3Service.ResolveS3Endpoint(Connection(), session)
            .Should().Be("https://s3-kna1.citycloud.com");
    }

    [Fact]
    public void A_non_default_port_in_the_catalog_is_preserved()
    {
        KeystoneSession session = Session(("object-store", "https://rgw.internal:8443/swift/v1"));

        OpenStackS3Service.ResolveS3Endpoint(Connection(), session)
            .Should().Be("https://rgw.internal:8443");
    }

    [Fact]
    public void A_dedicated_s3_catalog_entry_wins_over_the_swift_one()
    {
        KeystoneSession session = Session(
            ("object-store", "https://swift.example.com/swift/v1"),
            ("s3", "https://s3.example.com"));

        OpenStackS3Service.ResolveS3Endpoint(Connection(), session)
            .Should().Be("https://s3.example.com");
    }

    // ──────── Override and fallback ────────

    [Fact]
    public void An_explicit_endpoint_overrides_the_catalog()
    {
        // The escape hatch for a cloud whose catalog does not describe its S3 host.
        OpenStackConnection connection = Connection(s3Endpoint: "https://my-rgw.example.com/");
        KeystoneSession session = Session(("object-store", "https://ignored.example.com/swift/v1"));

        OpenStackS3Service.ResolveS3Endpoint(connection, session)
            .Should().Be("https://my-rgw.example.com");
    }

    [Fact]
    public void The_region_pattern_is_used_only_when_the_catalog_offers_nothing()
    {
        OpenStackS3Service.ResolveS3Endpoint(Connection(region: "Sto2"), Session())
            .Should().Be("https://s3-sto2.citycloud.com");
    }

    // ──────── Allowlist guidance ────────

    [Fact]
    public void The_required_hosts_cover_keystone_the_catalog_and_s3()
    {
        // These are exactly what an egress agent's allowlist needs; discovering
        // them one refused call at a time is the experience this replaces.
        OpenStackConnection connection = Connection(region: "Sto-Com");
        KeystoneSession session = Session(
            ("object-store", "https://s3-sto2.citycloud.com/swift/v1/AUTH_abc"),
            ("compute", "https://nova.example.com/v2.1"),
            ("network", "https://neutron.example.com"));

        List<string> hosts = OpenStackS3Service.GetRequiredHosts(connection, session);

        hosts.Should().BeEquivalentTo(
        [
            "identity.example.com",
            "s3-sto2.citycloud.com",
            "nova.example.com",
            "neutron.example.com"
        ]);
    }

    [Fact]
    public void The_required_hosts_are_deduplicated()
    {
        // Keystone usually appears in its own catalog too; listing it twice would
        // be noise in the allowlist an operator has to copy.
        OpenStackConnection connection = Connection();
        KeystoneSession session = Session(
            ("identity", "https://identity.example.com:5000/v3"),
            ("object-store", "https://identity.example.com:5000/swift/v1"));

        OpenStackS3Service.GetRequiredHosts(connection, session)
            .Should().ContainSingle().Which.Should().Be("identity.example.com");
    }
}
