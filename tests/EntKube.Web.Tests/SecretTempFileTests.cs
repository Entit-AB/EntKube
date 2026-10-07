using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Temp files EntKube writes on its way to a cluster must not be world-readable.
///
/// <para><b>The measurement that matters, verified rather than assumed.</b>
/// <c>Path.Combine(Path.GetTempPath(), …)</c> followed by <c>File.WriteAllTextAsync</c> creates a
/// file at <b>0644</b>. <c>Path.GetTempFileName()</c> goes through <c>mkstemp</c> and gives
/// <b>0600</b>. Both were checked on this platform rather than taken on trust, because the whole
/// point of the rule depends on which it is.</para>
///
/// <para><b>What that meant in practice.</b> The kubeconfig half of this was fixed when
/// <see cref="EntKube.Web.Services.SecretFile"/> was introduced — 28 sites. The same leak was
/// still present in <b>22 others</b>, and what they carried was not incidental:</para>
///
/// <list type="bullet">
/// <item>a TLS <b>private key</b>, its certificate and its chain (<c>VaultService</c>);</item>
/// <item>two Helm values files — and a values file is where a catalog component's
/// <c>Secret = true</c> form fields land, such as grafana's
/// <c>grafana.adminPassword</c>;</item>
/// <item>the per-value files in <c>SyncComponentSecretsAsync</c>, which exist <em>specifically</em>
/// to keep secret values out of an argument list ("NEVER --from-literal") and then wrote them to a
/// world-readable file instead;</item>
/// <item>eleven manifests, which routinely contain <c>Secret</c> objects.</item>
/// </list>
///
/// <para><b>Twenty of the 22 were never restricted at all.</b> The other two chmod'd after
/// writing, which is the race <c>SecretFile</c>'s own documentation describes: the content is on
/// disk and readable until the second call lands. <c>SecretFile.WriteAsync</c> creates the file
/// empty, restricts it, and only then writes — which is why it is the fix for both shapes.</para>
///
/// <para><b>Why the rule is "all of them" rather than "the secret ones".</b> Deciding which temp
/// file is sensitive is the judgement that produced 22 misses. 0600 costs nothing for a file only
/// this process writes and only its own child process reads, so the safe idiom is the default and
/// an exception has to be argued for in <see cref="Allowed"/>.</para>
/// </summary>
public class SecretTempFileTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>
    /// Sites permitted to write a temp file the unsafe way, each with a reason. Empty: every one
    /// found was carrying something that should not be world-readable.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal);

    [Fact]
    public void No_temp_file_on_the_way_to_a_cluster_is_written_world_readable()
    {
        List<string> offenders = Offenders();

        offenders.Should().BeEmpty(
            "Path.Combine(Path.GetTempPath(), …) + File.WriteAllText* creates a 0644 file, and "
            + "these are the files EntKube hands to kubectl and helm — manifests, Helm values, "
            + "certificates, secret values. Use SecretFile.WriteAsync, which creates the file, "
            + "restricts it to 0600, and only then writes, so there is no window either");
    }

    /// <summary>
    /// Sites building a temp path the unsafe way and then writing content to it without
    /// <c>SecretFile</c>. Returns "File:line variable" for each, so the failure names the place.
    /// </summary>
    private static List<string> Offenders()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));

        Directory.Exists(root).Should().BeTrue(
            "this test reads the web project's source; if it moved, the test has to follow rather "
            + "than quietly stop checking anything");

        List<string> offenders = [];

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string[] lines = File.ReadAllLines(path);
            string file = Path.GetFileNameWithoutExtension(path);

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                Match temp = Regex.Match(lines[i], @"(\w+)\s*=\s*Path\.Combine\(Path\.GetTempPath\(\)");
                if (!temp.Success) continue;

                string variable = temp.Groups[1].Value;
                string site = $"{file}:{i + 1} {variable}";
                if (Allowed.Contains(site)) continue;

                // Look ahead for the write. SecretFile first means the site is already safe.
                for (int j = i; j < Math.Min(i + 30, lines.Length); j++)
                {
                    if (Regex.IsMatch(lines[j], $@"SecretFile\.WriteAsync\(\s*{Regex.Escape(variable)}\b")) break;

                    if (Regex.IsMatch(lines[j], $@"File\.WriteAll\w*\(\s*{Regex.Escape(variable)}\b"))
                    {
                        offenders.Add(site);
                        break;
                    }
                }
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }
}
