# Knowledge base

The knowledge base holds help answers: published ones customers read in the portal under **Help
articles**, and internal ones you insert into replies. Open it under **Support › Ticket tools ›
Knowledge base**.

It's separate from an application's runbook (**Knowledge** on the application), which is our own
operating knowledge and is never shown to customers.

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
| Who sees it | **Draft**: nobody yet. **Internal**: agents only. **Public**: customers too. **Archived**: kept, shown to nobody. |
| Publish from | For a public answer, the time (Swedish) it appears to customers. Until then the list shows it as *scheduled*. |
| For | Every customer, or one customer only, for an answer about their own setup. |

The body is cleaned when it's saved, exactly as a mail is (see [Replying by mail](replying.md)):
formatting, lists, quotes, code and links stay; images, colours and scripts go. Attachments on
answers aren't supported yet.

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

## Not supported

- **Video embeds.** They would need embedded frames, which the HTML allow-list keeps out on
  purpose. Link to the video instead.
- **RSS feeds.** Help articles are behind the portal's sign-in, and a feed would need its own
  token. Customers see new articles in the portal.
- **Files on answers.** Link to a file in a ticket or elsewhere instead.
