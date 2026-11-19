# Knowledge base

The knowledge base holds help answers: published ones customers read in the portal under **Help
articles**, and internal ones you insert into replies. Open it under **Support › Ticket tools ›
Knowledge base**.

It also holds each application's runbook: what's written under **Knowledge** on an application
is the same set of answers, listed here under that application and never shown to customers. See
[An application's knowledge](#an-applications-knowledge).

## Categories

On the left, add a category at the top or under another one. The pencil renames a category or
moves it under a different parent, taking everything inside it along. A category can't be moved
under itself or anything beneath it, and only an empty category can be deleted.

## Answers

**New answer** opens the editor:

| Field | |
|---|---|
| Title, Category, Keywords | Keywords help both search and `??` find the answer. |
| Language | A tag such as `sv`, `en` or `en-GB`. Each answer is written in one language. There are no translation sets: write a second answer for a second language. |
| Who sees it | **Draft**: nobody yet. **Internal**: agents, and offered by `??`, so it may be inserted into a reply a customer reads. **Public**: customers too, in the portal. **Operations**: our own operating knowledge; agents only, never offered by `??`, never in the portal. **Archived**: kept, shown to nobody. |
| Publish from | For a public answer, the time (Swedish) it appears to customers. Until then the list shows it as *scheduled*. |
| For | Every customer, or one customer only, for an answer about their own setup. |

The body is cleaned when it's saved, exactly as a mail is (see [Replying by mail](replying.md)):
formatting, headings, lists, quotes, code, links and plain tables stay; images, colours and scripts
go. Attachments on
answers aren't supported yet.

## History and review

Every change to an answer keeps what it said before. Write a line in **What changed and why** to keep
with it. Open **History** under the editor to read earlier versions; **Restore** makes one current
again, and what the answer said until then is kept too.

**Still true** records that you read the answer and it still holds. It doesn't change the text or the
last-changed date. The answer shows when it was last confirmed and by whom.

## An application's knowledge

An application's runbook, architecture notes and other written sections are answers too. They're
written on the application (**Knowledge**, under *Written down*) or here, and are the same rows in
both places:

- **Agents only.** They're **Operations** (or **Archived** once retired), never offered by `??`, never
  in the portal, and can't be given on a ticket. The editor offers no other visibility for them.
- **Listed under their application.** The **Application** filter above the list shows *Everything*,
  *Help answers only*, or one application's part. The *Application* column says whose an answer is.
- **A section, not a customer.** Instead of *For*, such an answer has a **Section** (Runbook,
  Architecture, Decisions, Known defects, Access, Backup and recovery, Notes). Its customer is always
  the application's.
- **Reviewed every 90 days.** An application's section that nobody has confirmed with **Still true**
  for 90 days counts against the application's knowledge checks.

Sections written before this release were in markdown; EntKube moved them in at startup, keeping
their history and review stamps. A moved section that didn't convert exactly says so in a yellow
banner. The markdown it was written in is kept under **History**, as the entry saved by *EntKube
(moved to the knowledge base)* at the move, above the section's own earlier versions. A section not moved yet shows on
the application as *not moved to the knowledge base yet*, read-only, until EntKube's next start.

In quick search (the ticket queue's search box), an application's section is marked
*operations · application name*.

## Inserting an answer into a reply

In a reply or a note, type `??` followed by a word or two. Matching answers appear, internal
ones marked *internal*. Click one to insert it where you typed, with its formatting.

Inserting an answer records that it was given on the ticket. The ticket's side panel lists the
**Help answers given**, and the answer lists the tickets it was **given on**. Customers see
neither.

You're only offered **published** answers: public ones from their publish time, and internal
ones. An answer written for **another customer** is never offered on this customer's ticket.

## What customers see

In the portal, **Help articles** lists public answers that are published and written for
everyone or for that customer, with a search box. Drafts, internal and archived answers, answers
scheduled for later, and answers for other customers are never shown, not even when opened by a
direct link.

## Related answers

In an answer's editor, **Related answers** links it to others, for example *Reset your password*
and *Set up MFA*. A link shows on both answers. In the portal, an open article lists its related
articles, but only those the customer could open themselves: an internal answer, or one written for
another customer, is never listed.

## Who may edit

By default every member of the tenant may edit the knowledge base. To restrict it, give a role
**Support knowledge base: Edit** (or Manage) under **Admin › Roles**. From then on only members of
such roles, and global administrators, can add, change or delete categories and answers. Everyone
else sees *Only roles granted Support knowledge base: Edit change answers* and can read.
Granting *View* alone doesn't restrict anything.

An application's sections are the exception: anyone who can open the application can edit them, here
or on the application, whatever the knowledge-base role says. A runbook is kept current by the people
operating the application.

## Not supported

- **Video embeds.** They would need embedded frames, which the HTML allow-list keeps out on
  purpose. Link to the video instead.
- **RSS feeds.** Help articles are behind the portal's sign-in, and a feed would need its own
  token. Customers see new articles in the portal.
- **Files on answers.** Link to a file in a ticket or elsewhere instead.
