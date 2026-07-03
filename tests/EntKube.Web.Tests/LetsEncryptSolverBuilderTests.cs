using EntKube.Web.Data;
using EntKube.Web.Services;

namespace EntKube.Web.Tests;

public class LetsEncryptSolverBuilderTests
{
    private const string BaseIssuer = """
        apiVersion: cert-manager.io/v1
        kind: ClusterIssuer
        metadata:
          name: letsencrypt-prod
        spec:
          acme:
            server: https://acme-v02.api.letsencrypt.org/directory
            email: ops@example.com
            privateKeySecretRef:
              name: letsencrypt-prod-key
        """;

    private static IReadOnlyList<ClusterComponent> NoComponents() => [];

    [Fact]
    public void Apply_DefaultsToHttp01_WhenNothingSelected()
    {
        string result = LetsEncryptSolverBuilder.Apply(BaseIssuer, new Dictionary<string, string>(), NoComponents());

        Assert.Contains("solvers:", result);
        Assert.Contains("http01:", result);
        Assert.Contains("gatewayHTTPRoute:", result);
        Assert.DoesNotContain("dns01:", result);
    }

    [Fact]
    public void Apply_Http01_ProducesGatewayParentRefsSequence()
    {
        Dictionary<string, string> f = new() { ["enable-http01"] = "true" };

        string result = LetsEncryptSolverBuilder.Apply(BaseIssuer, f, NoComponents());

        // parentRefs must be a YAML sequence (list item), not a "0:" map key.
        Assert.Contains("parentRefs:", result);
        Assert.Contains("kind: Gateway", result);
        Assert.DoesNotContain("'0':", result);
        Assert.DoesNotContain("\"0\":", result);
    }

    [Fact]
    public void Apply_Dns01Cloudflare_UsesCloudflareSolverAndSecretRef()
    {
        Dictionary<string, string> f = new()
        {
            ["enable-http01"] = "false",
            ["enable-dns01"] = "true",
            ["dns-provider"] = "cloudflare"
        };

        string result = LetsEncryptSolverBuilder.Apply(BaseIssuer, f, NoComponents());

        Assert.Contains("dns01:", result);
        Assert.Contains("cloudflare:", result);
        Assert.Contains("apiTokenSecretRef:", result);
        Assert.Contains("cloudflare-api-token-secret", result);
        Assert.DoesNotContain("azureDNS", result);
    }

    [Fact]
    public void Apply_BothChallenges_ProducesTwoSolvers()
    {
        Dictionary<string, string> f = new()
        {
            ["enable-http01"] = "true",
            ["enable-dns01"] = "true",
            ["dns-provider"] = "route53",
            ["route53-region"] = "eu-west-1",
            ["route53-access-key-id"] = "AKIAEXAMPLE"
        };

        string result = LetsEncryptSolverBuilder.Apply(BaseIssuer, f, NoComponents());

        Assert.Contains("http01:", result);
        Assert.Contains("dns01:", result);
        Assert.Contains("route53:", result);
        Assert.Contains("region: eu-west-1", result);
        Assert.Contains("secretAccessKeySecretRef:", result);
        Assert.DoesNotContain("'0':", result);

        // Sanity: the result must still be parseable YAML and contain the acme email.
        Assert.Contains("email: ops@example.com", result);
    }

    [Fact]
    public void Apply_Dns01Azure_PopulatesAzureFields()
    {
        Dictionary<string, string> f = new()
        {
            ["enable-http01"] = "false",
            ["enable-dns01"] = "true",
            ["dns-provider"] = "azure",
            ["dns-zone"] = "example.com",
            ["dns-resource-group"] = "my-rg",
            ["dns-subscription-id"] = "sub-123",
            ["dns-tenant-id"] = "tenant-123",
            ["dns-client-id"] = "client-123"
        };

        string result = LetsEncryptSolverBuilder.Apply(BaseIssuer, f, NoComponents());

        Assert.Contains("azureDNS:", result);
        Assert.Contains("hostedZoneName: example.com", result);
        Assert.Contains("resourceGroupName: my-rg", result);
        Assert.Contains("clientSecretSecretRef:", result);
        Assert.Contains("azuredns-config", result);
    }

    [Fact]
    public void PopulateEditValues_DerivesTogglesAndProvider()
    {
        string installed = """
            spec:
              acme:
                solvers:
                - dns01:
                    cloudflare:
                      apiTokenSecretRef:
                        name: cloudflare-api-token-secret
            """;

        Dictionary<string, string> values = new();
        LetsEncryptSolverBuilder.PopulateEditValues(installed, values);

        Assert.Equal("false", values["enable-http01"]);
        Assert.Equal("true", values["enable-dns01"]);
        Assert.Equal("cloudflare", values["dns-provider"]);
    }
}
