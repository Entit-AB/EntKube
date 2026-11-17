# Reporting

**Support › Tickets › Reporting** counts the tickets of a period, and downloads them as a
spreadsheet.

## The figures

Choose **From** and **To** (whole days, Swedish time) and press **Show**.

| Figure | What it counts |
|---|---|
| Opened | Tickets reported in the period. A call registered by hand counts on the day the customer called, not the day it was typed in. |
| Closed | Tickets closed in the period, whenever they were opened. |
| Opened, by status now | The period's tickets by where they stand today. A ticket opened in the period and closed since counts as *Closed*. |
| By channel, by priority | The period's tickets split by how they arrived and by their priority. |
| Day by day | Opened and closed for each day. |

These are counts. Whether the agreement's response and resolution times were met is reported
per agreement, from the agreement itself, because that's where the clocks are defined.

## The spreadsheet

**Download .xlsx** gives the period's tickets, newest first: number, title, customer, status,
priority, channel, assignee, the reported, first-response, resolved and closed times (Swedish
time), and tags.

- **At most 6,000 tickets per download**, the same cap Zammad has. For a longer period, download it
  in parts.
- **A period is at most 366 days.**
- **Text that would start a formula is kept as text.** A title such as `=HYPERLINK(…)` is written
  with a leading apostrophe, so neither Excel nor a CSV re-saved from it will run it. Customers
  write the titles, and a spreadsheet of what they wrote must not run it.

The download contains customers' ticket titles. Handle it as you would the tickets themselves.
