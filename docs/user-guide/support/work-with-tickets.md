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

A note is written in the same editor as a mail: bold, italics, lists, quotes, code and links.
In the editor:

| Type | To |
|---|---|
| `::` and a word | insert a [text module](text-modules.md), its placeholders filled in from the ticket |
| `??` and a word or two | insert a [help answer](knowledge-base.md) |
| `@@` and a name | mention a colleague (below) |

Formatting is kept on the ticket. The portal, follower digests and mention mails show the
note's text.

### Mentioning colleagues

Type `@@` and the start of a name in the note, then pick the colleague: their name is written into
the note and they are ticked under it. You can also tick colleagues there directly. Each one ticked
is mailed the note at once and starts [following](#following-a-ticket) the ticket. If a mail could
not be sent, the confirmation names who was not reached; they still follow the ticket.

Removing a name from the text doesn't untick the colleague. Untick them under the note.

Replying to a mention mail does **not** reach the customer. Answer the customer from the
ticket.

### Deleting a note

For **ten minutes** after you write an **internal** note, it has a **delete** link in the history.
Press it, then press **delete — sure?** to confirm. Any highlights on the note go with it. Files you
added with it stay on the ticket.

Only internal notes can be deleted, only by the person who wrote them, and only in those ten
minutes. Anything the customer can see may already have been read or mailed, and it is part of
the record between us. Delete doesn't undo what the note set off: colleagues you mentioned have
already been mailed, and followers may already have had it in a digest.

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
