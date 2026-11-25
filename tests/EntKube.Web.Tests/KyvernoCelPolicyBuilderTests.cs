using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace EntKube.Web.Tests;

/// <summary>
/// The CEL rendering of the built-in policies. What these can check without a Kyverno is the shape it
/// depends on: the kinds and API version, the action, and the two properties of an expression that fail
/// silently — the text Kyverno's pod-controller rewrite looks for, and the pass given to an update of
/// something already in violation.
/// </summary>
public class KyvernoCelPolicyBuilderTests
{
    private static readonly KyvernoPolicyType[] PodPolicies =
    [
        KyvernoPolicyType.DisallowPrivilegedContainers, KyvernoPolicyType.DisallowRootUser,
        KyvernoPolicyType.RequireReadOnlyRootFilesystem, KyvernoPolicyType.DisallowPrivilegeEscalation,
        KyvernoPolicyType.RequireSeccompProfile, KyvernoPolicyType.DisallowHostNetwork,
        KyvernoPolicyType.DisallowHostPID, KyvernoPolicyType.DisallowHostPath,
        KyvernoPolicyType.RestrictImageRegistries, KyvernoPolicyType.RequireResourceLimits,
        KyvernoPolicyType.RequireResourceRequests, KyvernoPolicyType.RequirePodLabels,
    ];

    public static TheoryData<KyvernoPolicyType> PodPolicyTypes => [.. PodPolicies];

    private static KyvernoPolicy Policy(
        KyvernoPolicyType type, KyvernoValidationFailureAction mode = KyvernoValidationFailureAction.Enforce) => new()
    {
        Id = Guid.NewGuid(),
        PolicyType = type,
        ValidationFailureAction = mode,
        Configuration = type switch
        {
            KyvernoPolicyType.RestrictImageRegistries => """["ghcr.io/acme","docker.io"]""",
            KyvernoPolicyType.RequirePodLabels => """["app","team"]""",
            KyvernoPolicyType.VerifyImageSignatures => KyvernoPolicyService.SerializeVerifyImagesConfig(new()
            {
                ImagePatterns = ["registry.example.com/*"],
                PublicKey = "-----BEGIN PUBLIC KEY-----\nMFkwEwYH\n-----END PUBLIC KEY-----",
            }),
            _ => null,
        },
    };

    private static YamlMappingNode Load(string yaml)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static YamlNode At(YamlNode node, params string[] path) =>
        path.Aggregate(node, (n, key) => n is YamlSequenceNode s ? s[int.Parse(key)] : n[key]);

    private static List<string> Expressions(YamlMappingNode policy) =>
        ((YamlSequenceNode)At(policy, "spec", "variables")).Select(v => ((YamlScalarNode)v["expression"]).Value!).ToList();

    [Fact]
    public void Every_namespaced_built_in_renders_under_the_name_it_replaces()
    {
        foreach (KyvernoPolicyType type in Enum.GetValues<KyvernoPolicyType>()
                     .Where(t => t is not (KyvernoPolicyType.Custom or KyvernoPolicyType.RestrictClusterRbac)))
        {
            string cel = KyvernoCelPolicyBuilder.Build(Policy(type), "acme-prod")!;
            string legacy = KyvernoPolicyService.BuildManifest([Policy(type)], "acme-prod");

            YamlMappingNode node = Load(cel);
            At(node, "apiVersion").ToString().Should().Be("policies.kyverno.io/v1");
            At(node, "metadata", "name").ToString().Should()
                .Be(At(Load(legacy), "metadata", "name").ToString(), $"{type} must replace its own legacy policy")
                .And.Be(KyvernoCelPolicyBuilder.NameOf(type));
            At(node, "metadata", "namespace").ToString().Should().Be("acme-prod");
        }
    }

    [Theory]
    [InlineData(KyvernoValidationFailureAction.Enforce, "Deny")]
    [InlineData(KyvernoValidationFailureAction.Audit, "Audit")]
    public void The_mode_becomes_the_validation_action(KyvernoValidationFailureAction mode, string action)
    {
        YamlMappingNode node = Load(KyvernoCelPolicyBuilder.Build(Policy(KyvernoPolicyType.DisallowHostPath, mode), "ns")!);

        ((YamlSequenceNode)At(node, "spec", "validationActions")).Select(a => a.ToString()).Should().Equal(action);
    }

    /// <summary>
    /// Kyverno rewrites the text <c>object.spec</c>/<c>object.metadata</c> to reach a controller's pod
    /// template. A policy with more than one resource rule, or one reaching the pod any other way, gets
    /// no such rewrite.
    /// </summary>
    [Theory]
    [MemberData(nameof(PodPolicyTypes))]
    public void Pod_policies_are_shaped_for_the_pod_controller_rewrite(KyvernoPolicyType type)
    {
        YamlMappingNode node = Load(KyvernoCelPolicyBuilder.Build(Policy(type), "ns")!);

        YamlNode rule = ((YamlSequenceNode)At(node, "spec", "matchConstraints", "resourceRules"))
            .Should().ContainSingle().Subject;
        At(rule, "resources", "0").ToString().Should().Be("pods");
        At(rule, "apiGroups", "0").ToString().Should().BeEmpty();

        foreach (string expression in Expressions(node))
        {
            expression.Should().MatchRegex(@"\b(object|oldObject)\.(spec|metadata)\b");
            expression.Should().NotMatchRegex(@"\b(object|oldObject)\[");
        }

        // The rewrite covers messages too.
        string message = At(node, "spec", "validations", "0", "message").ToString();
        message.Should().NotContain("object.spec").And.NotContain("object.metadata");
    }

    [Theory]
    [MemberData(nameof(PodPolicyTypes))]
    public void An_update_to_something_already_in_violation_is_let_through(KyvernoPolicyType type)
    {
        YamlMappingNode node = Load(KyvernoCelPolicyBuilder.Build(Policy(type), "ns")!);
        List<string> expressions = Expressions(node);

        At(node, "spec", "validations", "0", "expression").ToString()
            .Should().Be("variables.complies0 || !variables.complied0");
        // The same question of both, or "already in violation" would mean something else.
        expressions[1].Should().Be(
            $"oldObject == null || !has(oldObject.kind) || ({expressions[0].Replace("object.", "oldObject.")})");
    }

    [Fact]
    public void The_background_scan_follows_the_legacy_policy()
    {
        At(Load(KyvernoCelPolicyBuilder.Build(Policy(KyvernoPolicyType.DisallowHostPath), "ns")!),
            "spec", "evaluation", "background", "enabled").ToString().Should().Be("true");
        At(Load(KyvernoCelPolicyBuilder.Build(Policy(KyvernoPolicyType.RestrictImageRegistries), "ns")!),
            "spec", "evaluation", "background", "enabled").ToString().Should().Be("false");
    }

    /// <summary>Unescaped, the dots would let <c>ghcrxio/acme</c> through.</summary>
    [Fact]
    public void A_registry_is_matched_literally_and_from_the_start()
    {
        string expression = Expressions(Load(
            KyvernoCelPolicyBuilder.Build(Policy(KyvernoPolicyType.RestrictImageRegistries), "ns")!))[0];

        // As it reaches CEL, where a backslash in a string literal is itself escaped.
        expression.Should().Contain(@"c.image.matches('^(ghcr\\.io/acme/.*|docker\\.io/.*)$')");
    }

    [Fact]
    public void A_quote_in_configuration_cannot_end_the_expression()
    {
        KyvernoPolicy policy = Policy(KyvernoPolicyType.RequirePodLabels);
        policy.Configuration = """["a' in {} || true || 'b"]""";

        Expressions(Load(KyvernoCelPolicyBuilder.Build(policy, "ns")!))[0]
            .Should().Contain(@"'a\' in {} || true || \'b' in object.metadata.labels");
    }

    [Theory]
    [InlineData(KyvernoPolicyType.RestrictImageRegistries)]
    [InlineData(KyvernoPolicyType.RequirePodLabels)]
    [InlineData(KyvernoPolicyType.VerifyImageSignatures)]
    public void An_unconfigured_policy_renders_nothing(KyvernoPolicyType type)
    {
        KyvernoPolicy policy = Policy(type);
        policy.Configuration = null;

        KyvernoCelPolicyBuilder.Build(policy, "ns").Should().BeNull();
    }

    [Fact]
    public void The_rbac_policy_denies_what_the_validator_denies()
    {
        string expression = Expressions(Load(
            KyvernoCelPolicyBuilder.Build(Policy(KyvernoPolicyType.RestrictRbac), "ns")!))[0];

        foreach (string denied in AppRbacRuleValidator.DeniedVerbs.Concat(AppRbacRuleValidator.DeniedResources))
            expression.Should().Contain($"'{denied}'");

        // Either grants exec as surely as pods/exec does.
        expression.Should().Contain("'pods/*'").And.Contain("'*/exec'");
        expression.Should().NotContain("'secrets'", "the validator only warns about it");
    }

    [Fact]
    public void The_cluster_policy_carries_the_namespaces_and_the_groups_that_stand_for_them()
    {
        YamlMappingNode node = Load(KyvernoCelPolicyBuilder.BuildClusterRbacPolicy(
            ["b-ns", "a-ns", "b-ns"], KyvernoValidationFailureAction.Enforce)!);

        At(node, "kind").ToString().Should().Be("ValidatingPolicy");
        ((YamlMappingNode)At(node, "metadata")).Children.Should().NotContainKey("namespace");
        At(node, "spec", "matchConditions", "0", "expression").ToString().Should().Contain("system:");

        string expression = Expressions(node)[0];
        expression.Should().Contain("u.namespace in ['a-ns', 'b-ns']");
        foreach (string group in new[] { "system:serviceaccounts", "system:authenticated", "system:serviceaccounts:a-ns" })
            expression.Should().Contain($"'{group}'");

        KyvernoCelPolicyBuilder.BuildClusterRbacPolicy([" "], KyvernoValidationFailureAction.Enforce).Should().BeNull();
    }

    [Fact]
    public void Several_public_keys_become_attestors_any_of_which_will_do()
    {
        KyvernoPolicy policy = Policy(KyvernoPolicyType.VerifyImageSignatures);
        policy.Configuration = KyvernoPolicyService.SerializeVerifyImagesConfig(new()
        {
            ImagePatterns = ["a.io/*"],
            PublicKey = "-----BEGIN PUBLIC KEY-----\nAAA\n-----END PUBLIC KEY-----\n"
                + "-----BEGIN PUBLIC KEY-----\nBBB\n-----END PUBLIC KEY-----\n",
        });

        YamlMappingNode node = Load(KyvernoCelPolicyBuilder.Build(policy, "ns")!);

        At(node, "kind").ToString().Should().Be("NamespacedImageValidatingPolicy");
        ((YamlSequenceNode)At(node, "spec", "attestors")).Should().HaveCount(2);
        At(node, "spec", "attestors", "1", "cosign", "key", "data").ToString()
            .Should().Be("-----BEGIN PUBLIC KEY-----\nBBB\n-----END PUBLIC KEY-----");
        At(node, "spec", "validations", "0", "expression").ToString()
            .Should().Be("images.containers.all(image, verifyImageSignatures(image, [attestors.key0, attestors.key1]) > 0)");
    }

    /// <summary>These fields are exact where the legacy ones were patterns: copied, a pattern matches nobody.</summary>
    [Fact]
    public void A_wildcard_identity_becomes_an_anchored_expression()
    {
        KyvernoPolicy policy = Policy(KyvernoPolicyType.VerifyImageSignatures);
        policy.Configuration = KyvernoPolicyService.SerializeVerifyImagesConfig(new()
        {
            ImagePatterns = ["a.io/*"],
            Issuer = "https://token.actions.githubusercontent.com",
            Subject = "https://github.com/acme/app/*",
        });

        YamlNode identity = At(Load(KyvernoCelPolicyBuilder.Build(policy, "ns")!),
            "spec", "attestors", "0", "cosign", "keyless", "identities", "0");

        At(identity, "issuer").ToString().Should().Be("https://token.actions.githubusercontent.com");
        At(identity, "subjectRegExp").ToString().Should().Be(@"^https://github\.com/acme/app/.*$");
    }
}
