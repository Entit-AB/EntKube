# Keyboard shortcuts and tabs

## Shortcuts

Press **?** anywhere in the ticket queue for this list.

| Key | What it does |
|---|---|
| `?` | Show or hide the list of shortcuts. |
| `s` | Put the cursor in the ticket search. |
| `n` | New ticket. |
| `r` | Reply, on an open ticket. |
| `x` | Add a note. |
| `Shift` + `C` | Resolve an open ticket, or close a resolved one. |
| `.` | Copy the ticket number. `..` copies the number and title, `...` the number, title and link. Press the dots quickly. |
| `Shift` + `←` / `→` | The previous or next ticket tab. |
| `Esc` | Back to the queue. |

A shortcut only does what a button on the screen would do at that moment: on a ticket you
can't reply to there's no Reply button, so `r` does nothing. Shortcuts are ignored while you
type in a field. `Esc` closes an open menu or dialog first rather than leaving the ticket.

Closing works the way the agreement does. An open ticket is first **resolved**, which stops its
resolution clock, and closed afterwards. `Shift` + `C` therefore opens the Resolve form on an open
ticket, and closes a ticket that's already resolved.

To switch shortcuts off, clear **Shortcuts on in this browser** in the `?` list. The setting is
kept per browser, as Zammad keeps it.

The keys differ from Zammad's (`Ctrl` + `Alt` + …): those combinations are taken by the browser or
the operating system on some keyboards, notably Swedish ones, where `Alt` types characters.

## Tabs

Every ticket you open gets a tab above the queue. Click a tab to go back to that ticket, or the
**×** to close it. The list icon at the start of the strip returns to the queue.

- Tabs are kept **in this browser**, per tenant, and survive a reload. They aren't shared
  between your devices.
- There are **at most 30**. Opening a 31st closes the oldest.
- A tab keeps its place when you open its ticket again.
- A tab is only a shortcut to a ticket. Closing it changes nothing on the ticket.
