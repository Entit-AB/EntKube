# Ticket actions: merge, split and link

## Merge

Use merge when two tickets are about the same matter, typically when a customer reported the
same fault twice. **Run it from the duplicate**, and give the number of the original.

**Merge into…** → enter the original's number → **Merge**.

What happens:

- The duplicate becomes **Merged** and points to the original. A banner on it links there.
- **Its clocks stop at the merge.** If nobody had answered it yet, the merge is recorded as its
  first response: it has been assessed as a duplicate of a ticket that is being worked. **A
  response that was already late stays late.** Merging does not erase a missed deadline.
- **Its history stays with it.** The original shows the duplicate's entries in its own
  history, shaded and marked with the duplicate's number.
- When the reporter replies quoting the duplicate's number, the reply is filed on the original.
- The duplicate's tags and followers carry over to the original, and the two are linked: the
  original as parent, the duplicate as child.

**Excluding the duplicate from the SLA** is a separate choice in the same form. Tick it and
give the reason (§14.6). Two reports of one fault are usually one incident to the customer.
Deciding that it shouldn't be counted twice is still your decision, and it is recorded under
your name.

A merge is refused when:

- the original belongs to another customer (move the ticket to them first if it is theirs);
- the original is closed or rejected (reopen it first);
- the original was itself merged (merge into the one it went into);
- the duplicate is already closed, rejected or merged.

A merged ticket can't be reopened. Reopen the original instead.

## Split

Use split when one ticket turns out to hold two matters, for example when a customer's reply
raises a second problem.

In the history, **split** next to a note opens a form. Customer replies filed on the ticket are notes, so a reply can be split off. Give the new
ticket a title and choose **Open the new ticket**. The new ticket:

- is for the same customer and application;
- is **reported at the time of that entry**, because that is when the customer raised it, so
  its clocks count from there;
- gets a receipt sent to the customer and is assigned by the rota, like any new ticket;
- is linked as the child of the ticket it came from.

## Link

Links record how tickets relate without changing either of them. In the panel under
**Linked tickets**, choose what the other ticket is, enter its number and click **Link**:

- **Related**: the same outage, the same delivery, with neither under the other.
- **Parent is**: the other ticket is the main piece of work and this is part of it.
- **Child is**: the other ticket is part of this one.

A ticket can have any number of links. A closed linked ticket is shown struck through. Remove
a link with its ×. Links only join tickets of the same tenant: moving a ticket to another
tenant removes its links.
