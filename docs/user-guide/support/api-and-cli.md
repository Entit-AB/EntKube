# Tickets from scripts: the API and the CLI

Everything in [Work with tickets](work-with-tickets.md) and [Ticket actions](ticket-actions.md)
that a script is likely to need is also available over the API and the `entkube` command line.
Tickets are named by their **number**, the same one the customer quotes.

## Tokens

Mint an API token under the tenant's **API tokens**, with:

- **`tickets:read`** to list and read tickets. A ticket carries the customer's own words,
  which can include personal and patient data. Treat this token accordingly.
- **`tickets:write`** to tag, link, merge, set reminders and add notes.

Everything a token changes is written to the ticket's history as `api-token:<token name>`, so
name tokens after what uses them.

## Commands

```
entkube tickets list [--view open|unassigned|reminders|escalated]
entkube tickets search --query 'tag:vpn status:open'
entkube tickets show --ticket 1042
entkube tickets tag --ticket 1042 --tag vpn
entkube tickets untag --ticket 1042 --tag vpn
entkube tickets link --ticket 1043 --to 1042 --role parent
entkube tickets unlink --ticket 1043 --link <link id from tickets show>
entkube tickets merge --ticket 1044 --into 1042 [--exclude-from-sla --reason "Same outage"]
entkube tickets reminder set --ticket 1042 --at 2026-11-20T08:00Z --note "Call Karin"
entkube tickets reminder clear --ticket 1042
entkube tickets note --ticket 1042 --text "Waiting for the vendor" [--customer-visible]
```

The same rules apply as on screen:

- A merge never erases a missed response, and excluding the duplicate needs a reason.
- A reminder never stops a clock.
- A note is internal unless you ask for the customer to see it.

A refusal, such as a merge across customers or a tag with a comma, answers **422** with the
reason. A ticket number that is not this tenant's answers **404**.

## Not available to AI assistants

EntKube's MCP server lets an AI assistant use the API, but it has **no ticket tools, by
design**. Giving a language model the support queue would pass customers' correspondence,
patient data included, to a third party. The agreement requires a data-processing agreement
(§17) and the customer's approval (§18) for that.

## Not through the API

Overviews that depend on who is asking (*Mine*, *My reminders due*, *Following*), because a
token belongs to the tenant, not to a person. Also splitting, renaming, bulk changes and
following, which remain on screen for now.
