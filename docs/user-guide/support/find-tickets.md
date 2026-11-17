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

## Not yet available

Free-text search and Zammad-style search syntax (`state:open AND tags:vpn`) arrive in a later
phase. Until then, use the overviews and the customer filter.
