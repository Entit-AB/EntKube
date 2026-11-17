# Macros

A macro applies a set of changes to a ticket in one click. For example, "Take it, tag it
`network`, confirm P2", or "Close as spam".

## Running one

On an open ticket, pick a macro under **Run a macro…** and click **Run**. To run it on many
tickets, select them in the queue and choose **Run a macro** in the bar.

- **It runs as you.** The history records each change under your name with the macro beside it,
  for example `nils (macro "Close as spam")`.
- **It's all or nothing.** If a macro can't finish on a ticket, for example because it closes a
  ticket it doesn't resolve and that ticket isn't resolved, it changes nothing there and says why.
  When running on many tickets, those tickets are listed as left alone.
- **Afterwards** you stay on the ticket, or go back to the queue, depending on how the macro is set
  up. Running on many tickets always leaves you on the list.

## What a macro can do

- Assign the ticket to whoever runs it, or put it back in the queue.
- Add and remove tags.
- Confirm or change the priority. The macro carries the written reason §14.3 requires.
- Add a note, internal or one the customer sees.
- Set a reminder some hours ahead.
- Resolve with a given text (the customer sees it), and close a resolved ticket.

## What a macro can't do

These limits are deliberate:

- It can't mail the customer.
- It can't pause a clock. A pause needs the party and the reason written down each time.
- It can't reject a ticket. §14.4 needs the system the fault was in.
- It can't run on a closed, rejected or merged ticket.

Macros are kept under **Support › Ticket tools › Macros**.
