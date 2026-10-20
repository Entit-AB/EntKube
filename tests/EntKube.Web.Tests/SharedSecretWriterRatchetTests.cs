using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// No binding writer may apply or delete a <b>whole</b> Secret in an application's namespace.
///
/// <para>Nine services wrote a binding's credentials by applying a complete Secret manifest, and
/// five removed them by deleting the Secret object. Both are wrong whenever the app has pointed
/// more than one writer at the same Secret, which is supported and is what the operator who
/// reported this had done: the vault's keys and the Redis binding's keys shared
/// <c>entitweb-secrets</c>, and the second writer to run silently dropped the first's.</para>
///
/// <para>The subtle half is that <c>apply</c> <i>looks</i> additive. Its three-way merge deletes
/// the keys named in the object's <c>last-applied-configuration</c> annotation and missing from
/// the new config — and that annotation describes the object, so it describes whoever applied
/// last rather than this writer.</para>
///
/// <para>This is a source scan rather than a behavioural test because the property is about
/// which <em>verb</em> a writer reaches for. A behavioural test can show one writer is correct;
/// only this can say a tenth has not been added the old way.
/// <see cref="SharedSecretKeysTests"/> covers what the verb then does.</para>
/// </summary>
public class SharedSecretWriterRatchetTests
{
    /// <summary>Converted in full, so the floor is zero rather than a banked count.</summary>
    private const int BaselineWholeSecretWrites = 0;

    /// <summary>Same relative hop the other source-scan ratchets use.</summary>
    private const string Web = "../../../../../src/EntKube.Web";

    private static readonly string ServicesRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web, "Services"));

    /// <summary>
    /// Every place a binding's Secret name is written or deleted as a whole object.
    ///
    /// <para>Anchored on <c>KubernetesSecretName</c>: that property is what makes a Secret one a
    /// binding writes, and therefore one an app may be sharing. A Secret a service owns outright
    /// — a cluster's own TLS material, say — is not named that way and is not in scope.</para>
    /// </summary>
    private static List<string> FindWholeSecretWrites()
    {
        List<string> found = [];

        foreach (string path in Directory.EnumerateFiles(
                     ServicesRoot, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("KubernetesSecretName", StringComparison.Ordinal))
                {
                    continue;
                }

                // The verb can be a few lines either side of the name: the manifest is built
                // above the apply, and the name is an argument to the delete.
                string window = string.Join(
                    "\n", lines.Skip(Math.Max(0, i - 8)).Take(18));

                // Comments mention these verbs on purpose — they explain why the code no longer
                // uses them — so prose is stripped before matching.
                string code = Regex.Replace(window, @"^\s*//.*$", "", RegexOptions.Multiline);

                if (Regex.IsMatch(code, @"\bApplyManifestAsync\(|\bApplyManifestInNamespaceAsync\(")
                    || Regex.IsMatch(code, @"DeleteManifestAsync\(\s*""[Ss]ecret"""))
                {
                    found.Add($"{Path.GetFileName(path)}:{i + 1}");
                }
            }
        }

        return found;
    }

    [Fact]
    public void No_binding_writer_applies_or_deletes_a_whole_secret()
    {
        List<string> found = FindWholeSecretWrites();

        found.Count.Should().BeLessThanOrEqualTo(BaselineWholeSecretWrites,
            "a binding's Secret may be shared with the app's own secrets, so it has to be "
            + "written key by key through SharedSecretKeys — apply deletes the other writer's "
            + "keys through the object's last-applied annotation, and deleting the Secret takes "
            + "them outright:\n  "
            + string.Join("\n  ", found));
    }

    /// <summary>
    /// The pattern still matches something. A scan that has quietly stopped finding anything
    /// passes forever, so this fails if the anchor property disappears — renamed, or moved off
    /// these services — rather than letting the ratchet go green and blind.
    /// </summary>
    [Fact]
    public void The_scan_still_finds_the_writers_it_is_watching()
    {
        int writers = Directory
            .EnumerateFiles(ServicesRoot, "*.cs", SearchOption.AllDirectories)
            .Count(p => File.ReadAllText(p).Contains("SharedSecretKeys", StringComparison.Ordinal));

        writers.Should().BeGreaterThanOrEqualTo(8,
            "the nine converted writers are what this ratchet protects; if they no longer go "
            + "through SharedSecretKeys, the scan above is watching nothing");
    }
}
