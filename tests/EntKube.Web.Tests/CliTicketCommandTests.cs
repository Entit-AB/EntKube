using System.Text.Json;
using EntKube.Cli;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The ticket commands are the first CLI commands that send a body, so what they send is pinned
/// here: the parity ratchets build paths only, and never look at a body.
/// </summary>
public class CliTicketCommandTests
{
    private static CliCommand Command(params string[] path) =>
        CliCommands.All.Single(c => c.Path.SequenceEqual(path));

    private static string Body(CliCommand command, params string[] args) =>
        JsonSerializer.Serialize(command.BuildBody!(CliArgs.Parse(args)), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Merge_sends_the_original_and_excludes_only_when_asked()
    {
        CliCommand merge = Command("tickets", "merge");

        Body(merge, "--ticket", "12", "--into", "7")
            .Should().Be("{\"into\":7,\"excludeFromSla\":false,\"reason\":null}");

        Body(merge, "--ticket", "12", "--into", "7", "--exclude-from-sla", "--reason", "Same outage")
            .Should().Be("{\"into\":7,\"excludeFromSla\":true,\"reason\":\"Same outage\"}");
    }

    [Fact]
    public void A_ticket_number_that_is_not_one_is_a_usage_error_not_a_request()
    {
        Action bad = () => Command("tickets", "merge").BuildBody!(CliArgs.Parse(["--ticket", "12", "--into", "seven"]));
        bad.Should().Throw<CliUsageException>().WithMessage("*--into must be a ticket number*");
    }

    [Fact]
    public void A_note_is_internal_unless_the_flag_says_otherwise()
    {
        CliCommand note = Command("tickets", "note");

        Body(note, "--ticket", "3", "--text", "Looking").Should().Contain("\"customerVisible\":false");
        Body(note, "--ticket", "3", "--text", "Looking", "--customer-visible").Should().Contain("\"customerVisible\":true");
    }

    [Fact]
    public void A_reminder_time_is_read_as_utc()
    {
        Body(Command("tickets", "reminder", "set"), "--ticket", "3", "--at", "2026-11-20T09:00")
            .Should().Contain("\"at\":\"2026-11-20T09:00:00Z\"");
    }

    [Fact]
    public void Untagging_escapes_the_tag_into_the_path() =>
        Command("tickets", "untag").BuildPath(CliArgs.Parse(["--ticket", "3", "--tag", "slow login"]))
            .Should().Be("/api/v1/tickets/3/tags/slow%20login");
}
