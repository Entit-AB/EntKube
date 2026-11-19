using System.Text.RegularExpressions;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Fail-closed for the knowledge base's FUTURE readers (helpdesk phase 11). An application's
/// runbook lives in KbAnswers beside the help answers customers read, so every method that reads
/// KbAnswers is classified here, per METHOD: customer-facing ones must go through KbAudience,
/// which refuses application answers and Operations. A new reader fails until somebody decides
/// which it is; a reader that vanished fails until it is taken off the list.
///
/// <para>A global query filter would have done this silently — and turned the backup, the purge
/// and the category delete into partial views, which is how a scoped writer once deleted a
/// production key. A list someone must touch is the louder choice.</para>
/// </summary>
public class KbAnswerReadersRatchetTests
{
    private const string Web = "../../../../../src/EntKube.Web";

    /// <summary>Methods whose result can reach a customer: the portal, or ?? into a reply or note.</summary>
    private static readonly string[] CustomerFacing =
    [
        "Services/Tickets/KnowledgeBaseService.cs:ForAgentAsync",
        "Services/Tickets/KnowledgeBaseService.cs:ForInsertAsync",
        "Services/Tickets/KnowledgeBaseService.cs:Published",
    ];

    /// <summary>Methods only agents' screens, sweeps or the backup read.</summary>
    private static readonly string[] AgentsOnly =
    [
        "Services/BackupService.cs:ExportAsync",
        "Services/BackupService.cs:ImportAsync",
        "Services/Knowledge/KnowledgeMerge.cs:ConvergeOneAsync",
        "Services/Knowledge/KnowledgeMerge.cs:MoveOneAsync",
        "Services/Knowledge/KnowledgeService.cs:GetGapsAsync",
        "Services/TenantService.cs:DeleteAppKnowledgeAsync",
        "Services/TenantService.cs:DeleteTenantAsync",
        "Services/Tickets/KnowledgeBaseService.cs:AnswersAsync",
        "Services/Tickets/KnowledgeBaseService.cs:AnswersForAppAsync",
        "Services/Tickets/KnowledgeBaseService.cs:AnswersOnTicketAsync",
        "Services/Tickets/KnowledgeBaseService.cs:AppNamesAsync",
        "Services/Tickets/KnowledgeBaseService.cs:DeleteAnswerAsync",
        "Services/Tickets/KnowledgeBaseService.cs:DeleteCategoryAsync",
        "Services/Tickets/KnowledgeBaseService.cs:GetAsync",
        "Services/Tickets/KnowledgeBaseService.cs:LinkTicketAsync",
        "Services/Tickets/KnowledgeBaseService.cs:MarkReviewedAsync",
        "Services/Tickets/KnowledgeBaseService.cs:RelatedAsync",
        "Services/Tickets/KnowledgeBaseService.cs:SaveAnswerAsync",
        "Services/Tickets/KnowledgeBaseService.cs:SetRelatedAsync",
        "Services/Tickets/QuickSearch.cs:SearchAsync",
    ];

    [Fact]
    public void Every_method_that_reads_KbAnswers_is_classified()
    {
        Dictionary<string, string> found = Readers();

        found.Should().NotBeEmpty("an empty scan means the pattern stopped matching, not that nothing reads the table");
        string[] listed = [.. CustomerFacing.Concat(AgentsOnly)];

        found.Keys.Except(listed).Should().BeEmpty("a method that reads KbAnswers must be classified here, customer-facing or agents-only");
        listed.Except(found.Keys).Should().BeEmpty("a method that no longer reads KbAnswers must be taken off the list");
    }

    [Fact]
    public void Every_customer_facing_reader_goes_through_the_audience_rules()
    {
        Dictionary<string, string> found = Readers();

        foreach (string method in CustomerFacing)
        {
            found.Should().ContainKey(method);
            found[method].Should().Contain("KbAudience.", $"{method} can reach a customer, so it must use KbAudience");
        }
    }

    /// <summary>Nothing the portal runs may even name application knowledge.</summary>
    [Fact]
    public void Nothing_the_portal_runs_names_application_knowledge()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));
        string[] portal = [.. Directory.GetFiles(Path.Combine(root, "Components/Pages/Portal"), "*.razor", SearchOption.AllDirectories)];

        portal.Should().Contain(p => p.EndsWith("CustomerPortal.razor", StringComparison.Ordinal), "the scan must still see the portal");

        foreach (string file in portal)
        {
            string source = File.ReadAllText(file);

            foreach (string forbidden in new[] { "KbAnswers", "AnswersForAppAsync", "KnowledgeService", "KbVisibility.Operations", "AppKnowledgePanel" })
            {
                source.Should().NotContain(forbidden, $"{Path.GetFileName(file)} is the customer portal");
            }
        }
    }

    /// <summary>"File:Method" → the method's source, for every method naming <c>.KbAnswers</c> outside the model.</summary>
    private static Dictionary<string, string> Readers()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Web));
        Directory.Exists(root).Should().BeTrue("this test reads the web project's source");
        Dictionary<string, string> readers = [];

        foreach (string file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Data{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string[] lines = File.ReadAllLines(file);
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"\.KbAnswers\b"))
                {
                    continue;
                }

                (string name, int start) = Enclosing(lines, i);
                readers[$"{relative}:{name}"] = string.Join('\n', lines.Skip(start).Take(NextDeclaration(lines, start + 1) - start));
            }
        }

        return readers;
    }

    private static readonly Regex Declaration = new(
        @"^\s+(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>?,\.\s\[\]\(\)]+?\s(\w+)\(");

    private static (string Name, int Line) Enclosing(string[] lines, int index)
    {
        for (int i = index; i >= 0; i--)
        {
            Match m = Declaration.Match(lines[i]);

            if (m.Success)
            {
                return (m.Groups[1].Value, i);
            }
        }

        return ("(unknown)", 0);
    }

    private static int NextDeclaration(string[] lines, int from)
    {
        for (int i = from; i < lines.Length; i++)
        {
            if (Declaration.IsMatch(lines[i]))
            {
                return i;
            }
        }

        return lines.Length;
    }
}
