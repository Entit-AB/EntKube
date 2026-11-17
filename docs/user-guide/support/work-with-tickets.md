# Work with tickets

An open ticket shows its clocks at the top, the **Actions** under them, then the time logged
and the **History**. The panel on the right holds tags, linked tickets, reminders and who
follows the ticket.

Everything you do is written to the history under your name. The history is the record
between us and the customer (§14.6): entries are added, and never edited or removed.

## Answering and recording

| Action | What it does |
|---|---|
| **Record first response** | Stops the response clock: confirmed, assessed, work begun. Only the first one counts. Replying to the customer by mail also does this. |
| **Pause the clock** | The ticket is waiting on the customer, their supplier or a third party. Say who and why; §14.4 requires it. The resolution clock stops, but the response clock keeps running. |
| **Resume** | They answered, and the clock runs again. |
| **Resolve** | Say what restored the service, or which workaround the customer accepted. The customer sees this. |
| **Reject (§14.4)** | The fault lay outside the agreement. Say where. |
| **Close** | Offered once a ticket is resolved. |
| **Confirm priority / Reprioritise** | Needs a written reason. A change restarts the targets. |

## Notes

**Add a note** writes an entry on the history. Tick **The customer sees this** for a status
update the customer should read in the portal. Leave it unticked for a working note.

### Mentioning colleagues

Under the note, tick the colleagues you want to bring in. Each of them is mailed the note at
once and starts [following](#following-a-ticket) the ticket. If a mail could not be sent, the
confirmation names who was not reached; they still follow the ticket.

Replying to a mention mail does **not** reach the customer. Answer the customer from the
ticket.

### Text modules

Under the note, **Insert a text module** finds reusable text by name or keyword and adds it with
its placeholders filled in. See [Text modules](text-modules.md).

## Who has the ticket

- **Take this**: the ticket is yours. If you are on the support rota, the ticket is mailed to
  you, and replying above the line in that mail answers the customer.
- **Hand to…**: give it to somebody on the rota, who is told by mail.
- **Put it back**: return it to the queue, unassigned.

## Renaming

Click the pencil next to the title. The new title is what the customer sees in the portal, so
the rename is recorded.

## Tags

Type in **Add a tag** and press Enter, or pick a tag from the suggestions. Remove one with its
small ×. Tags change at once. See [tags](ticket-basics.md#tags).

## Reminders and automatic close

**Reminder.** Pick a date and time (Swedish time) and, optionally, what it is for. When the
reminder comes due, whoever holds the ticket is mailed, the ticket shows a red alarm, and it
appears in **Reminders due**. **A reminder never stops a clock.** If you are waiting on the
customer, pause the clock instead: that is the only thing the agreement counts as waiting.

**Close automatically.** Offered on a resolved ticket. Choose 3, 5 or 10 working days. The
ticket then closes by itself unless it is reopened first, and the customer sees the close.
Reopening cancels it.

## Following a ticket

**Follow** in the panel mails you what happens on a ticket you don't hold. Changes are
collected while the ticket is busy, and one summary arrives a couple of minutes after it goes
quiet, so three quick notes make one mail. Your own changes are left out of it. You stop
following automatically if you leave the tenant.

## Many tickets at once

In the queue, tick the tickets you want, or tick the box in the header to select every row
shown. A bar appears where you choose the change:

- hand to somebody on the rota, or put back in the queue
- add or remove a tag
- set a reminder
- change the priority (one written reason, recorded on every ticket)
- add the same note to each, optionally visible to the customer
- close the resolved ones

Each ticket is changed on its own. A ticket the change does not apply to is left alone, and
the confirmation says which and why, for example *#1043, not resolved, so not closed*.
