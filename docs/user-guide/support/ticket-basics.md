# Ticket basics

A ticket is one matter a customer has raised: a fault, a question or a request. It arrives
by mail, from the customer portal, from monitoring or from a connected ticket system. Every
ticket belongs to one **customer**, and usually to one of their **applications**. The
application decides which agreement, and so which support hours, the ticket is measured
against.

## The number

Every ticket has a number, such as `#1042`. The customer is told it in the receipt and asked
to keep it in the subject when they write about the matter. Replies are filed on the ticket by
that number.

**A number never changes**: not when the ticket is moved, merged or renamed.

## Statuses

| Status | Meaning |
|---|---|
| **New** | Nobody has given the first response yet. |
| **In progress** | Responded to and being worked. |
| **Waiting** | Paused while we wait for the customer, their supplier or a third party. The resolution clock is stopped; the response clock is not. |
| **Resolved** | Service is restored or a workaround was accepted. Waiting for the customer to agree, or to close by itself (see [reminders and automatic close](work-with-tickets.md#reminders-and-automatic-close)). |
| **Closed** | Done. |
| **Rejected** | The fault lay in a system outside the agreement (§14.4). The time is still billable, but it is not counted as an SLA deviation. |
| **Merged** | A duplicate, folded into another ticket. See [merge](ticket-actions.md#merge). |

*Closed*, *Rejected* and *Merged* are final. A merged ticket can't be reopened; reopen the ticket it went into.

## Priority

Priorities run from **P1** (critical) to **P4**. The customer proposes one when they report.
Until somebody here confirms it at the first assessment, the queue shows it as
**unconfirmed**. Confirming or changing a priority always needs a written reason (§14.3).
Changing it restarts the targets from that moment.

## The clocks

Each ticket shows up to four deadlines. All of them run only inside the application's support
hours, so a report that arrives on a Saturday starts counting on Monday morning. The support
hours come from the application's Annex A, kept on its customer's **Contract** page under
**Annex A — applications**.

| Clock | What it measures |
|---|---|
| **Response** | Time to the first response: confirmed, assessed, work begun. Missing it on a P1 or P2 costs 10% of the window fee (§14.6). |
| **Resolution** | Time to resolved. A goal, not a penalty. Stopped while the ticket is waiting on somebody else. |
| **Next update** | When the customer must next hear from us. Hourly on a P1, every four hours on a P2. |
| **Escalation** | When the ticket must go to level 2 (§14.5). |

In the queue, a **red row** is a missed response that carries the penalty, and a **yellow row**
is a missed response that does not.

## Tags

Tags are short labels, such as `vpn` or `login`, that you use to sort your own work. They are
stored in lower case, so `VPN` and `vpn` are the same tag. The box offers the tags already in
use. **The customer never sees tags.**

## Who sees what

The customer sees their tickets in the portal, with every history entry marked *the customer
sees this*, and the files on it that were shared with them (see [Attachments](attachments.md)).
They never see tags, links, issue links, highlights, reminders, followers, internal notes or
internal files, or who on our side holds the ticket.
