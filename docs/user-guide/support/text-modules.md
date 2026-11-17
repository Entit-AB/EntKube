# Text modules

Text modules are pieces of text you write often, such as a greeting, a standard answer or a
signature, ready to drop into a note.

## Using one

In a note or a mail, type `::` and part of a module's name or keywords, then click the module.
It replaces what you typed, with its placeholders already filled in from the ticket, and you can
edit it before you add the note or send the mail.

## Placeholders

A text module can contain placeholders, which are filled in when you insert it:

| Placeholder | Becomes |
|---|---|
| `#{ticket.number}` | the ticket's number |
| `#{ticket.reference}` | the reference to keep in a mail subject |
| `#{ticket.title}` | the title |
| `#{ticket.priority}`, `#{ticket.status}` | P1–P4; the status |
| `#{ticket.reported_at}` | when it was reported (Swedish time) |
| `#{ticket.response_due}` | when the first response is due (Swedish time) |
| `#{customer.name}`, `#{app.name}` | the customer; the application |
| `#{requester.name}`, `#{requester.email}` | who reported it |
| `#{user.name}` | you |

A placeholder that has no value on this ticket becomes `-`. So does a misspelt one; the editor
warns you about those when you save the module.

## Managing them

Text modules are kept under **Support › Ticket tools › Text modules**. A module can be offered on
every customer's tickets, or on one customer's tickets only, for example a signature that
customer asked for. Inactive modules are kept but not offered.
