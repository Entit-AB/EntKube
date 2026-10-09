using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Secret values must not reach the acknowledgment dialog.
///
/// <para><b>The decision this implements.</b> An operator approving a Secret change needs to know
/// which keys are added, changed or removed, and where — not the values. A dialog is a screen that
/// gets shared, screenshotted and recorded in support sessions, and until now routing the
/// Secret-syncing paths through the seam would have put customer credentials on it. That is what
/// kept 13 invocations outside the gate.</para>
///
/// <para><b>Most of this file is one assertion.</b> A sentinel value goes in; the test fails if it
/// comes out. That is deliberately cruder than checking the exact output format, because the
/// property that matters is not "the redaction looks right" but "the plaintext is absent" — and a
/// test written around the format would keep passing while a new shape leaked through the side.</para>
/// </summary>
public class SecretRedactionTests
{
    /// <summary>
    /// Distinctive enough that its presence anywhere in the output is unambiguous, and not valid
    /// base64 or JSON, so nothing can claim to have "parsed" it away.
    /// </summary>
    private const string Sentinel = "L3AK-S3NT1NEL-do-not-show-me";

    private static string DiffTextOf(string raw) => new ClusterChangeDiff { DiffText = raw }.DiffText;

    // ════════════════════════════════════════════════════════════════
    //  The property: the plaintext does not come out
    // ════════════════════════════════════════════════════════════════

    [Theory]
    // kubectl diff output, which is where a Secret change normally appears.
    [InlineData("""
        diff -u -N /tmp/LIVE/secret /tmp/MERGED/secret
        --- /tmp/LIVE/v1.Secret.billing.db
        +++ /tmp/MERGED/v1.Secret.billing.db
        @@ -1,6 +1,6 @@
         apiVersion: v1
         data:
        -  DB_PASSWORD: b2xkLXBhc3N3b3Jk
        +  DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
         kind: Secret
         metadata:
           name: db
        """)]
    // A server-side dry-run rendering, used when kubectl diff is unavailable.
    [InlineData("""
        apiVersion: v1
        data:
          DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
        kind: Secret
        metadata:
          name: db
          namespace: billing
        type: Opaque
        """)]
    // The raw manifest, shown when the dry-run itself failed.
    [InlineData("""
        apiVersion: v1
        kind: Secret
        metadata:
          name: db
        stringData:
          DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
        """)]
    // A deleted Secret read back with get -o yaml.
    [InlineData("""
        # The following resource will be DELETED:

        apiVersion: v1
        data:
          DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
        kind: Secret
        """)]
    // A JSON patch body.
    [InlineData("""{"data":{"DB_PASSWORD":"L3AK-S3NT1NEL-do-not-show-me"}}""")]
    // An image-pull Secret, whose value is a JSON document in one key.
    [InlineData("""
        apiVersion: v1
        kind: Secret
        type: kubernetes.io/dockerconfigjson
        data:
          .dockerconfigjson: L3AK-S3NT1NEL-do-not-show-me
        """)]
    // A TLS Secret: two keys, one of them a private key.
    [InlineData("""
        apiVersion: v1
        kind: Secret
        type: kubernetes.io/tls
        data:
          tls.crt: c29tZS1jZXJ0
          tls.key: L3AK-S3NT1NEL-do-not-show-me
        """)]
    // A multi-document manifest where the Secret is not the first document.
    [InlineData("""
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: settings
        data:
          mode: live
        ---
        apiVersion: v1
        kind: Secret
        metadata:
          name: db
        data:
          DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
        """)]
    // A document whose kind this cannot see at all — a truncated hunk. Redacted by default,
    // which is the inversion the design rests on.
    [InlineData("""
        @@ -3,4 +3,4 @@
         data:
        -  DB_PASSWORD: b2xk
        +  DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
        """)]
    public void The_value_never_reaches_the_dialog(string raw)
    {
        DiffTextOf(raw).Should().NotContain(Sentinel);
    }

    // ════════════════════════════════════════════════════════════════
    //  What must survive, or the dialog stops being worth reading
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void The_keys_and_their_namespace_survive()
    {
        string text = DiffTextOf("""
            apiVersion: v1
            data:
              DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
              SMTP_USER: bWFpbGVy
            kind: Secret
            metadata:
              name: db
              namespace: billing
            """);

        text.Should().Contain("DB_PASSWORD").And.Contain("SMTP_USER");
        text.Should().Contain("name: db").And.Contain("namespace: billing");
        text.Should().Contain("kind: Secret");
    }

    /// <summary>
    /// A changed value must still read as changed. Replacing both sides with the same placeholder
    /// would turn a password rotation into a diff that looks like nothing happened — which is a
    /// worse outcome than showing too much, because the operator would approve it believing it was
    /// a no-op.
    /// </summary>
    [Fact]
    public void A_changed_value_still_looks_changed()
    {
        string text = DiffTextOf("""
            @@ -1,4 +1,4 @@
             data:
            -  DB_PASSWORD: b2xkLXBhc3N3b3Jk
            +  DB_PASSWORD: bmV3LXBhc3N3b3Jk
             kind: Secret
            """);

        string[] lines = text.Split('\n');
        string before = lines.Single(l => l.StartsWith("-  DB_PASSWORD", StringComparison.Ordinal));
        string after = lines.Single(l => l.StartsWith("+  DB_PASSWORD", StringComparison.Ordinal));

        before.Should().NotBe(after, "two different values must not redact to the same text");
        before.Should().Contain("sha256:");
        after.Should().Contain("sha256:");
    }

    [Fact]
    public void An_added_and_a_removed_key_keep_their_diff_markers()
    {
        string text = DiffTextOf("""
            @@ -1,4 +1,4 @@
             data:
            -  OLD_TOKEN: Z29uZQ==
            +  NEW_TOKEN: aGVyZQ==
             kind: Secret
            """);

        text.Should().Contain("-  OLD_TOKEN:").And.Contain("+  NEW_TOKEN:");
    }

    /// <summary>
    /// ConfigMap data is configuration an operator is meant to read, and most of what EntKube
    /// applies is ConfigMaps. Over-redacting them would make the dialog useless for the common
    /// case, which is why this looks for ConfigMap rather than for Secret.
    /// </summary>
    [Fact]
    public void ConfigMap_data_is_left_alone()
    {
        string text = DiffTextOf("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              mode: live
              replicas: "3"
            """);

        text.Should().Contain("mode: live").And.Contain("replicas: \"3\"");
        text.Should().NotContain("redacted");
    }

    [Fact]
    public void A_configmap_beside_a_secret_keeps_its_own_values()
    {
        string text = DiffTextOf("""
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              mode: live
            ---
            apiVersion: v1
            kind: Secret
            metadata:
              name: db
            data:
              DB_PASSWORD: L3AK-S3NT1NEL-do-not-show-me
            """);

        text.Should().Contain("mode: live", "the ConfigMap's data is not secret");
        text.Should().NotContain(Sentinel, "the Secret's is");
    }

    /// <summary>
    /// Nothing that has no data block is touched. A Deployment diff is the most common thing this
    /// sees and it must come through byte for byte.
    /// </summary>
    [Fact]
    public void A_manifest_with_no_data_block_is_returned_unchanged()
    {
        const string raw = """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: api
            spec:
              replicas: 3
              template:
                spec:
                  containers:
                    - name: api
                      image: registry.example.com/api:1.4.2
            """;

        DiffTextOf(raw).Should().Be(raw);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(empty manifest)")]
    [InlineData("(changes detected)")]
    public void Text_that_carries_nothing_is_returned_unchanged(string raw)
    {
        DiffTextOf(raw).Should().Be(raw);
    }

    /// <summary>
    /// An empty value is worth showing as empty: "this key is being set to nothing" is a
    /// different decision from "this key is being set to something I cannot see".
    /// </summary>
    [Fact]
    public void An_empty_value_is_shown_as_empty()
    {
        string text = DiffTextOf("""
            apiVersion: v1
            kind: Secret
            data:
              UNSET: ""
            """);

        text.Should().Contain("UNSET: \"\"");
    }

    /// <summary>
    /// The choke point is only sufficient because the dialog shows <em>nothing but</em>
    /// <c>DiffText</c>. If it ever rendered <c>change.Manifest</c> or <c>change.Patch</c>
    /// directly — to show "what will be sent", say — the redaction would be bypassed without a
    /// line of the gate changing, and no other test here would notice.
    ///
    /// <para>So the dependency is pinned where it can be read: the dialog's markup may reference
    /// the diff, and may not reach into the planned change's bodies.</para>
    /// </summary>
    [Fact]
    public void The_dialog_shows_nothing_but_the_redacted_diff()
    {
        string dialog = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/EntKube.Web/Components/Pages/Shared/ClusterChangeAckDialog.razor"));

        File.Exists(dialog).Should().BeTrue(
            "this test reads the acknowledgment dialog's markup; if it moved, the test has to "
            + "follow rather than quietly stop checking anything");

        string markup = File.ReadAllText(dialog);

        markup.Should().Contain("_diff.DiffText", "that is the redacted text");

        foreach (string body in new[] { ".Manifest", ".Patch" })
        {
            markup.Should().NotContain(body,
                $"rendering the planned change's {body} would show secret values the diff has "
                + "had removed, and would do it without touching the gate");
        }
    }

    /// <summary>
    /// The gate's own NoChange() path, and HasChanges, must be unaffected — redaction is about
    /// what is shown, not about whether anything is shown.
    /// </summary>
    [Fact]
    public void Redaction_does_not_disturb_the_no_change_verdict()
    {
        ClusterChangeDiff none = ClusterChangeDiff.NoChange();

        none.HasChanges.Should().BeFalse();
        none.DiffText.Should().BeEmpty();
    }
}
