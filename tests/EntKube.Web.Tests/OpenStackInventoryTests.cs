using System.Net;
using System.Text;
using EntKube.Web.Services;
using EntKube.Web.Services.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the OpenStack inventory discovery that backs the provisioning form's
/// pickers. Nothing here talks to a cloud: a stub handler answers Nova/Neutron/Glance
/// with recorded payload shapes, and what is asserted is that we read the fields the
/// form depends on — the sizing behind a flavor name, the external flag that decides
/// which network picker a network belongs in, and that pagination is followed rather
/// than silently truncating the list an operator chooses from.
/// </summary>
public class OpenStackInventoryTests
{
    private const string ComputeUrl = "https://nova.example.com/v2.1";
    private const string NetworkUrl = "https://neutron.example.com";
    private const string ImageUrl = "https://glance.example.com";

    [Fact]
    public async Task ListFlavorsAsync_readsSizing_andFollowsPagination()
    {
        StubHandler handler = new();
        handler.OnGet(
            $"{ComputeUrl}/flavors/detail?limit=500",
            $$"""
            {
              "flavors": [
                { "id": "1", "name": "b.2c4gb", "vcpus": 2, "ram": 4096, "disk": 40 }
              ],
              "flavors_links": [
                { "rel": "next", "href": "{{ComputeUrl}}/flavors/detail?limit=500&marker=1" }
              ]
            }
            """);
        handler.OnGet(
            $"{ComputeUrl}/flavors/detail?limit=500&marker=1",
            """
            { "flavors": [ { "id": "2", "name": "b.4c8gb", "vcpus": 4, "ram": 8192, "disk": 80 } ] }
            """);

        IReadOnlyList<OpenStackFlavor> flavors = await Sut(handler).ListFlavorsAsync(Session());

        flavors.Select(f => f.Name).Should().Equal("b.2c4gb", "b.4c8gb");
        flavors[1].Should().BeEquivalentTo(new OpenStackFlavor("2", "b.4c8gb", 4, 8192, 80));
        flavors[1].Display.Should().Be("b.4c8gb — 4 vCPU, 8 GiB RAM, 80 GB disk");
    }

    [Fact]
    public async Task ListImagesAsync_asksForActiveImages_andFollowsTheRootRelativeNextLink()
    {
        StubHandler handler = new();
        handler.OnGet(
            $"{ImageUrl}/v2/images?status=active&limit=200&sort_key=name&sort_dir=asc",
            """
            {
              "images": [ { "id": "img-1", "name": "ubuntu-22.04", "min_disk": 20, "min_ram": 0 } ],
              "next": "/v2/images?status=active&marker=img-1"
            }
            """);
        handler.OnGet(
            $"{ImageUrl}/v2/images?status=active&marker=img-1",
            """
            { "images": [ { "id": "img-2", "name": "ubuntu-24.04-kube-v1.31.4" } ] }
            """);

        IReadOnlyList<OpenStackImage> images = await Sut(handler).ListImagesAsync(Session());

        images.Select(i => i.Id).Should().Equal("img-1", "img-2");
        images[0].Display.Should().Be("ubuntu-22.04 — needs ≥20 GB disk");
        images[1].Display.Should().Be("ubuntu-24.04-kube-v1.31.4");
    }

    [Fact]
    public async Task ListNetworksAsync_carriesTheExternalFlag_soTheTwoPickersCanBeSplit()
    {
        StubHandler handler = new();
        handler.OnGet(
            $"{NetworkUrl}/v2.0/networks",
            """
            {
              "networks": [
                { "id": "ext-1", "name": "public", "router:external": true, "shared": true, "subnets": ["s1"] },
                { "id": "net-1", "name": "project-net", "router:external": false, "subnets": ["s2", "s3"] },
                { "id": "net-2", "name": "no-flag-at-all" }
              ]
            }
            """);

        IReadOnlyList<OpenStackNetwork> networks = await Sut(handler).ListNetworksAsync(Session());
        OpenStackInventory inventory = new() { Networks = networks };

        inventory.ExternalNetworks.Select(n => n.Id).Should().Equal("ext-1");
        inventory.TenantNetworks.Select(n => n.Id).Should().Equal("net-1", "net-2");
        networks[0].Display.Should().Be("public (shared)");
        networks[1].SubnetCount.Should().Be(2);
    }

    [Fact]
    public async Task ListAvailabilityZonesAsync_dropsZonesThatCannotTakeABoot()
    {
        StubHandler handler = new();
        handler.OnGet(
            $"{ComputeUrl}/os-availability-zone",
            """
            {
              "availabilityZoneInfo": [
                { "zoneName": "nova", "zoneState": { "available": true } },
                { "zoneName": "drained", "zoneState": { "available": false } },
                { "zoneName": "no-state-reported" }
              ]
            }
            """);

        IReadOnlyList<string> zones = await Sut(handler).ListAvailabilityZonesAsync(Session());

        zones.Should().Equal("nova", "no-state-reported");
    }

    [Fact]
    public async Task ListFlavorsAsync_surfacesAnApiFailure_ratherThanReturningAnEmptyList()
    {
        // An empty picker reads as "this project has no flavors", which would quietly
        // push the operator into typing a name; a thrown error becomes the form's
        // "fill these in by hand" banner instead.
        StubHandler handler = new();
        handler.OnGet($"{ComputeUrl}/flavors/detail?limit=500", "not authorized", HttpStatusCode.Forbidden);

        Func<Task> act = () => Sut(handler).ListFlavorsAsync(Session());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*403*");
    }

    // ──────── Harness ────────

    private static OpenStackComputeService Sut(StubHandler handler)
    {
        Mock<IHttpClientFactory> innerHttpFactory = new();
        innerHttpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        AgentRegistry agents = new(null!, NullLogger<AgentRegistry>.Instance);
        OpenStackHttpFactory httpFactory = new(innerHttpFactory.Object, agents);

        return new OpenStackComputeService(httpFactory, NullLogger<OpenStackComputeService>.Instance);
    }

    private static KeystoneSession Session() => new()
    {
        Token = "token",
        UserId = "user",
        ProjectId = "project",
        Endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["compute"] = ComputeUrl,
            ["network"] = NetworkUrl,
            ["image"] = ImageUrl
        }
    };

    /// <summary>Answers exact request URLs with canned JSON; anything unexpected fails the test.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> responses = new();

        public void OnGet(string url, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            responses[url] = (body, status);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();

            if (!responses.TryGetValue(url, out (string Body, HttpStatusCode Status) response))
            {
                throw new InvalidOperationException($"Unexpected request: {request.Method} {url}");
            }

            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json")
            });
        }
    }
}
