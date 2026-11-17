# Find tickets

## The queue

Open **Support › Tickets › Ticket queue** in the tenant's menu. The queue lists every ticket
that is not yet closed, across all of the tenant's customers, **worst first**: by priority,
then by how long it has waited.

Each row shows:

- the priority, marked *unconfirmed* until somebody has confirmed it;
- the title, with any tags under it and an alarm icon when a reminder is set (red once it
  is due);
- whose ticket it is: the customer, and who on our side holds it (*unclaimed* if nobody);
- the time left on each clock, or how far past it is.

When the queue spans several customers, a filter at the top narrows it to one.

## Overviews

The tabs above the list are fixed views of the same queue. The number on each tab is how
many tickets it holds right now.

| Overview | Shows |
|---|---|
| **All open** | Everything not yet closed. |
| **Mine** | Tickets you hold. |
| **Unassigned** | Tickets nobody holds. |
| **My reminders due** | Your tickets whose reminder has come due. |
| **Reminders due** | Anybody's tickets whose reminder has come due. |
| **Escalated** | Tickets with a response, resolution or update deadline already missed, or falling due within ten minutes. Resolved tickets are left out: they are waiting for the customer, not for us. |
| **Following** | Tickets you [follow](work-with-tickets.md#following-a-ticket). |

## Opening a ticket

Click a row to open the ticket. Every ticket has its own address:

```
https://<entkube>/tenants/<tenant>?section=tickets&ticket=1042
```

You can bookmark it, paste it to a colleague or open it in another browser tab, and the back
button returns you to the queue. An address carrying another tenant's ticket number opens
nothing: a ticket is only ever reached through the tenant it belongs to.

### Copying a ticket's reference

Next to the title, the clipboard icon copies:

- **Number**: `#1042`
- **Number and title**: `#1042: Login fails for all users`
- **Number, title and link**: the same, followed by the ticket's address on its own line

## Searching

The search box above the overviews searches **every** ticket of the tenant, open and closed. The
newest come first, up to 200; the line under the box tells you when there were more. Results
replace the overviews until you click **Clear**, and you can select them for
[bulk changes](work-with-tickets.md#many-tickets-at-once) like any list. The search is part of the
page's address, so you can bookmark it or send it to a colleague.

A word on its own looks in titles, descriptions and the whole history, and a number finds that
ticket. To be more precise, use fields:

| Field | Finds |
|---|---|
| `title:`, `text:` | words in the title; in the description or history |
| `status:` | `new`, `open` (in progress), `waiting` (pending), `resolved`, `closed`, `rejected`, `merged` |
| `priority:` | `P1` to `P4` |
| `customer:`, `app:` | words in the customer's or application's name |
| `assignee:` | who has it; `assignee:nobody` for unassigned |
| `requester:` | the reporter's name or address |
| `tag:` | a tag |
| `channel:` | `email`, `portal`, `phone`, `monitoring`, `integration`, `feedback` |
| `reported:`, `updated:`, `closed:`, `reminder:` | a day `2026-11-17`, a range `[2026-11-01 TO 2026-11-17]`, or a comparison `>now-24h`, `<=2026-10-31` |
| `number:` | a number, or a range `[1000 TO 1100]` |

Zammad's names work too, for example `state.name:`, `owner:`, `tags:` and `created_at:`.

Combine terms with `AND` (the default between terms), `OR`, `NOT` or `!`, and parentheses.
`"a phrase"` matches the words together, and `print*` matches by prefix. `status:(new OR open)`
applies one field to several values. Fuzzy matching (`~`) and regular expressions aren't
supported. A search that can't be read says why, under the box. Click **?** for the field list.

Examples:

```
tag:vpn status:open
customer:"Vårdbolaget" reported:>now-7d
!status:closed assignee:nobody priority:(P1 OR P2)
"certificate expired" OR "cert expired"
```

## The customer card

On a ticket, the card at the top of the right-hand column shows the customer: how many tickets are
open, how many were raised in the last 12 months, and how old the oldest open one is. It lists
their other open tickets, and shows who reported this ticket and how many tickets that address has
raised. **all tickets** and the reporter's count open a search for them.
