# Helpdesk parity: the support module measured against Zammad

The goal: an agent working EntKube's support queue can do everything that the
[Zammad agent user documentation](https://user-docs.zammad.org/en/latest/index.html)
describes. Each phase ships with its own chapter of a user manual
(`docs/user-guide/support/`), structured the way Zammad's manual is.

This is a plan and a gap analysis. Nothing in it is built yet.

## Out of scope, and why

| Zammad feature | Reason |
|---|---|
| AI features | No LLM is wired into support. §17 DPA / §18 processor approval apply, and support mail carries patient data. `ISupportMailAnalyst` stays rule-based. |
| Caller log, live chat | Each needs telephony or a chat channel we don't run. |
| i-doit | We have no CMDB integration, and the cluster/app catalog already fills that role. |
| Secure email (S/MIME, PGP) | Needs certificate custody per mailbox. It can be revisited after the mailbox work. |
| Two-factor authentication | Already handled by the platform (see the MFA reset work). It is not a support feature. |
| UI language switching | The UI is English only, by decision. |

## The constraint that shapes every phase

Zammad is a general helpdesk. Ours is the system of record for a signed agreement
(`docs/application-management.md`): P1–P4 priorities whose confirmation is a written
act (§14.3), response clocks that run inside a support window (§14.4), and a 10%
penalty when we miss (§14.6). So:

1. **Zammad's features are added around the agreement model and never replace it.**
   A Zammad behaviour that would move an SLA clock, change a priority or erase a
   breach without a named human and a reason gets reshaped until it no longer does.
2. **Machine proposes, human confirms** still holds. A macro run by an agent is that
   agent's act and is recorded under their name and the macro's name. Templates and
   text modules only fill in drafts.
3. **Every feature ships API-first.** The API is the product (`docs/decomposition.md`),
   so every phase adds the endpoints, the CLI verbs and the parity tests along with
   the Blazor UI.
4. **Mechanics that apply to every phase:** each new table needs migrations in all three
   providers with hand-checked defaults, an entry in `ModuleMap` under Support, and a
   `BackupBundle.CurrentVersion` bump. Every query must be tenant-scoped, and anything a
   customer sees must also be customer-scoped.

## How Zammad's concepts map onto ours

| Zammad | EntKube today | Decision |
|---|---|---|
| Group (a queue with full / read / none access) | Tenant membership, plus an opt-in support roster | **No groups** (decision 1). The tenant is the queue, and the roster decides who takes work. Features Zammad scopes "per group" are scoped per tenant, optionally narrowed to a customer. |
| Organization | `Customer` | Same concept. |
| Customer (a person) | `Ticket.RequestedBy`/`RequestedByEmail`, `ContractContact`, `CustomerSupportAddress`, portal users | No single person record exists. Phase 3 introduces a requester view built from these sources; it does not create a new register of people. |
| Article | `TicketEvent` (`Note`, `Responded`, `StatusUpdate`, … with `CustomerVisible`) | This is the thread. It gains an article type (note / call / email), attachments (decision 4), rich-text bodies (decision 3) and reply/forward actions. |
| States: new, open, closed | `New`, `InProgress`, `Closed` | Same meaning. |
| State: pending reminder | none | Becomes **`ReminderAt`, an attribute that works in any open status**, rather than a status. It must never pause a §14.4 clock. Only the existing `Waiting` pause does that, because only the agreement decides what stops the clock. |
| State: pending close | `Resolved` (no auto-close today) | `Resolved` gains **`AutoCloseAt`**. The confirmation window becomes explicit instead of manual. |
| State: merged | none | Adds a **`Merged` status** and `MergedIntoTicketId`. See the merge decision below. |
| Priority: 1 low / 2 normal / 3 high | P1–P4 with proposal and confirmation | We keep ours. Zammad's three priorities have no meaning under the agreement. |
| SLA escalation and its colours | `TicketSla`, `TicketClock` (business-window arithmetic) | Already stronger than Zammad's. We only add the header popup and the colour coding. |
| Time accounting | `TimeEntry` with `WorkKind` time categories | Exists. What's missing is the **prompt on update**: an article added → "time spent?" (optional, never asked in bulk) → a `TimeEntry` against the ticket. |
| Knowledge base | `KnowledgeSection` per app (internal runbook with review intervals) | It is not the same thing. Zammad's KB is a customer-facing answer library, so it is new work (phase 5). The runbook stays as it is. |
| GitHub/GitLab issue links | Ticket bridge (two-way sync with Jira, ServiceNow, Ivanti) | GitHub and GitLab arrive as **read-only links** next to the bridge rather than inside it, as in Zammad. |
| Ticket URL / tab | None. A ticket renders inside the tenant explorer from component state. | **Phase 1 prerequisite.** Notifications, copy-URL, recently viewed and tabs all need an address for each ticket. |

### Decisions taken, which need saying because they differ from Zammad

- **A merge does not change the source ticket's SLA standing.** In Zammad a merge
  silently retires the duplicate. Here, merging away a ticket that already breached
  §14.4 would erase a penalty without anyone deciding to. The source keeps its clocks,
  its first response and any breach. Excluding it from the SLA remains the existing
  explicit act (`ExcludeFromSlaAsync`, with a reason and a name). The merge dialog
  offers that as a separate, visible checkbox.
- **Moved articles keep their visibility.** A customer-visible event moved by a merge
  stays customer-visible on the target, and an internal one stays internal. The
  customer portal must still show only that customer's tickets. A merge across two
  customers is refused; the agent has to *move* the ticket first.
- **A macro cannot send a reply or confirm a priority silently.** Zammad's macros have
  no reply action either. If a macro sets a priority, that goes through
  `ConfirmPriorityAsync` as the running agent, with the reason "macro <name>".
- **Customers never see tags, links, checklists, mentions, drafts or GitHub links.**
  Zammad hides these from customers too.

## Gap analysis

✅ exists · 🟡 partly exists · ❌ missing. The **Phase** column refers to the phases in the next section.

| Zammad capability | Us | Phase |
|---|---|---|
| Ticket per URL, deep link, copy number / title / URL | ❌ | 1 |
| Inline title rename | ❌ | 1 |
| History dialog | ✅ the events timeline | — |
| Tags (autocomplete, immediate save) | ❌ | 1 |
| Links: parent / child / normal | ❌ | 1 |
| Merge | ❌ | 1 |
| Split an article into a new ticket | ❌ | 1 |
| Pending reminder / pending close | ❌ | 1 |
| Overviews (my assigned, unassigned, reminders reached, open, escalated within 10 min) | 🟡 one SLA-sorted queue with a customer filter | 1 |
| Bulk edit (owner, status, priority, comment) | ❌ | 1 |
| Subscribe; @@mention that notifies and subscribes | ❌ | 1 |
| Owner / assign | ✅ including round-robin | — |
| Change customer | 🟡 `MoveAsync` across tenants and customers | 3 |
| Article types note / call / email; internal/public toggle | 🟡 `CustomerVisible` exists; no type | 2 |
| Reply, reply-all, forward, quoting | 🟡 reply to customer only | 2 |
| Attachments on articles and on inbound mail | ❌ dropped at ingest | 2 (S3, decision 4) |
| Rich-text editor and HTML mail | ❌ plain-text bodies end to end | 2 (decision 3) |
| Text modules `::` with `#{…}` placeholders | ❌ | 2 |
| Ticket templates | ❌ | 2 |
| Macros (single and bulk) | ❌ | 2 |
| Checklists, checklist templates, ticket-reference items, close guard | ❌ | 2 |
| Shared drafts and per-user autosave | ❌ | 2 |
| Time-accounting prompt on update; per-ticket sum per category | 🟡 `TimeEntry` exists; no prompt | 2 |
| Quick search (tickets, customers, KB) | ❌ | 3 |
| Advanced search syntax and a search details page | ❌ | 3 |
| Customer and organization sidebars, pages and stats | 🟡 customer exists; no sidebar or stats | 3 |
| In-app notifications, badge and preference matrix | ❌ mail only | 4 |
| Out of office with a substitute | 🟡 roster `IsActive` and a note | 4 |
| Presence on a ticket (viewing / editing) | 🟡 Redis presence exists for users | 4 |
| Agent dashboard widgets | 🟡 the operations advisor exists | 4 |
| Personal iCal feed of deadlines | ❌ | 4 |
| Knowledge base: categories, answers, visibility, scheduling, `??` insert | ❌ | 5 |
| Keyboard shortcuts | ❌ | 6 |
| Tabs (max 30, after-update behaviour, activity dot) | ❌ | 6 |
| Reporting (counts by status and channel, .xlsx, 6,000-row cap) | 🟡 monthly SLA report | 6 |
| GitHub/GitLab issue links (live, read-only) | ❌ | 6 |
| Mobile view | 🟡 Bootstrap only | 6 (responsive pass; no separate app) |
| Highlighter | ❌ | 6 |
| Profile: appearance, avatar, devices, API tokens | 🟡 `ApiToken` and the design-system theme exist | 6 (audit only) |

## Phases

Each phase is one PR, or a short stack of PRs. Each one includes the code, the API,
the CLI, tests and its user-manual chapter, and lands on `main` before the next
phase starts.

### Phase 1: Work the queue
A ticket route (`/tenants/{slug}/tickets/{number}`) with copy-number, copy-title and copy-URL. Tags. Links of type
parent, child and normal. Merge, with the SLA rule above. Split. `ReminderAt` and
`AutoCloseAt`, with a sweep that acts **as each tenant in turn** (see the sweep-authorization
work). Six overviews. Bulk edit. Subscribe and @@mention, delivered by mail through
the existing `TicketNotifier`.
*Manual chapters: Ticket basics, Find tickets, Work with tickets, Ticket actions.*

**Status (2026-11-16): built in the UI and the services.** The manual is in
`docs/user-guide/support/`. The rules are pinned in `TicketWorkTests` and the screens in
`TicketWorkRenderTests`. Decisions made while building it:

- **Merge stops the duplicate's clocks** by recording the merge as its first response, if it
  had none. Assessing a report as a duplicate of a ticket being worked *is* the assessment
  §14.4 waits for. Without that, a merged duplicate's response clock kept running and turned
  into a breach a week later. A merge made after the deadline is recorded as late.
- **The deep link is `?section=tickets&ticket=N`** inside the tenant explorer, rather than a
  route of its own, so that tenant access is checked where it already is.
- **Follower mail is a digest driven by the history** (`TicketEvent.RecordedAt`), not a hook in
  every write path. It is sent once the ticket has been quiet for two minutes, and the claim is
  a compare-and-set on the cursor.
- **Split is offered on notes only.** The *Created* entry holds "Registered via …", not the
  customer's words. Customer replies are filed as notes, so they can be split.

**Phase 1b: the ticket API and CLI, done (2026-11-17).** It is the first public API for tickets
at all.

- **Contract.** `ITicketApi` in the Support contracts, implemented by `TicketApi`.
- **Routes.** Ten routes under `/api/v1/tickets`, with tickets addressed by number inside the
  token's tenant.
- **Scopes.** `tickets:read` and `tickets:write`. The read label says the tickets include
  customers' correspondence and patient data.
- **CLI.** `entkube tickets …`. These are the first CLI commands that send a JSON body, so the CLI
  gained a body and PUT.
- **No MCP tools, by design.** Every ticket route is in `NoToolByDesign` with the §17/§18 reason.
  Exposing the queue to a model *is* the transfer the no-LLM rule forbids.
- **Not in the API:** person-relative overviews (a token has no person), split, rename, bulk
  changes and following.
- **Test probe.** The OpenAPI scope probe now fills integer path parameters with a number. Before
  this, a `{number:int}` route was unrouted for a GUID and returned 404, which the probe read as
  "not refused".

### Phase 2: Write faster
Article types, attachments (inbound and outbound), and reply, reply-all, forward and quote.
Text modules with a placeholder engine. Ticket templates (fill empty fields
only). Macros (atomic, bulk, after-run behaviour). Checklists and their templates.
Shared drafts and autosave. The time-accounting prompt.
*Manual chapters: Text modules, Ticket templates, Macros, Checklist, Shared drafts, Time accounting.*

**Phase 2a done (2026-11-17):** text modules, ticket templates, macros, checklists and checklist
templates, plus an agent **New ticket** form. The form was a gap in the gap analysis: tickets
could arrive only by mail, the portal, monitoring or a bridge, so a phone call couldn't be
registered at all, and templates had nothing to fill. Decisions:

- **Macros have typed columns, not an action list.** Each action has its own rules: a priority
  carries its §14.3 reason, and only a resolved ticket closes. A macro can't mail the customer,
  pause a clock or reject a ticket. It checks everything before changing anything.
- **Text modules are narrowed per customer**, not per group (decision 1). A placeholder with no
  value becomes "-", as in Zammad, and the editor flags misspelt ones.
- **The `::` trigger waits for the rich-text editor (2c).** For now the note form has a search
  that inserts the filled-in text.
- **A checklist item naming `#N` follows that ticket**, looked up in this tenant only, so it
  reveals nothing about another tenant's ticket of the same number.
- **"Reported at" on a hand-registered ticket** defaults to now and can be set earlier. A call
  logged afterwards is reported when it was made.
- **Bug found and fixed:** a browser's `datetime-local` sends no seconds, and Blazor's default
  binding expected them. The Phase 1 reminder picker therefore silently did nothing. All three
  pickers now bind `yyyy-MM-ddTHH:mm`, pinned by render tests.

**Phase 2b done (2026-11-17):** a time prompt after a note (optional, never in bulk, logged as a
`TimeEntry` against the ticket), personal autosave of the note being typed, one shared note draft
per ticket, and named shared new-ticket drafts. Drafts live on the server (`TicketDraft`), so
they survive a reload, a dropped circuit or another instance. They are never on the history. Also:

- **The time log stored Swedish times as if they were the server's.** It used `ToUniversalTime()`,
  which applies the host's offset, so a UTC container recorded a Swedish 09:00 as 09:00 UTC. It
  now converts from Swedish time, and its pickers carry the format fix.
- **Build hazard:** `@bind:event="oninput" @bind:after=…` inside TicketDetail's deeply nested
  markup sent the Razor/C# compiler into a spin that ran for over 30 minutes at 200% CPU. A plain
  `value=` plus `@oninput` handler compiles in about 90 seconds. Avoid `@bind:after` in that
  component.

**Phase 2c-1 done (2026-11-17): attachments.**

- Kept in one of the tenant's S3 `StorageLink`s (chosen under Support › Mail › Attachments), reached
  through Data Services' own `StorageService` and client factory with the tenant as an input.
- Inbound mail attachments are kept at intake, keyed to the mail. A ticket shows them once the mail
  is filed on it, including for merged duplicates, so the filing logic did not change.
- An attachment that could not be kept is still a row, with `Stored = false` and the reason shown
  on the ticket. Intake never stops because of a bucket.
- Agents attach from the ticket. Without storage they are refused with the reason. It never falls
  back to the database.
- Download: `/tenants/{slug}/support/attachments/{id}`. The tenant comes from the address, access
  is checked against it, and the attachment is then looked up inside it. Files are served as a
  download with an allow-listed type, `nosniff` and `CSP: sandbox`.
- The backup bundle carries the rows only (v17). The installer docs and README say so.
- **Test lesson:** the first in-memory store refused cross-tenant reads itself, which hid the
  service's own tenant predicate (a mutation deleting it passed). The fake is now tenant-blind on
  read.
- **Not yet:** customers downloading attachments from the portal (done in Phase 7), and an
  HTTP-level test of the download endpoint (it needs a cookie-authenticated test host, which the
  API fixture isn't).

**Phase 2c-2 done (2026-11-17): rich text and mail from the ticket.**

- **Composer.** A Reply composer on the ticket (`TicketMailComposer`) with Quill 2.0.3, vendored into
  `wwwroot/lib/quill` from the npm tarball after checking it against the registry's sha512. It
  offers reply, reply all (copying the others on the customer's last mail, minus our own
  addresses) and forward (internal, not a response), `::` text modules typed inline, quoting
  from the history, and the ticket's stored files as attachments.
- **`RichText`** (HtmlSanitizer, allow-list) is the only sanitiser. It runs before anything is
  stored or sent, and again when shown. The plain part is made from the same sanitised HTML.
  `TicketEvent.DetailHtml` holds the rich version, and `Detail` keeps the text that search,
  digests, the portal and the mail's plain part read.
- **One message with To and Cc visible**, as a person's client sends it, and no
  `Auto-Submitted`: a person wrote it. The internal notices (assignment, mention, digest) stay
  plain text, so the reply cut line is still found in text, never in markup (decision 3).
- **Not yet:** notes are still plain text (only mail is rich). Inbound HTML is shown as its text
  (remote images are therefore never loaded). Neither the `@@` trigger inside the editor nor
  customer downloads from the portal exist yet. *(Rich notes, `@@` and portal downloads: done in
  Phase 7. Inbound HTML: done in Phase 8.)*

Phase 2 is complete apart from those. Next: **Phase 3** (search, customer and organisation views).

**Phase 3 done (2026-11-17): search and the customer card.**

- **Search.** `TicketSearch` parses Zammad's syntax and compiles it to an EF expression, so it is
  the same SQL on all three providers and every instance, with no index to keep in step. The
  tenant is applied before the query. `LIKE` is given an explicit escape character, because the
  `[%]` form is SQL Server's alone. Fuzzy `~` and regex are refused with a reason.
- **Where it shows up.** The queue has a search box: the address carries `q`, results replace the
  overviews, and bulk changes work on them. The API has `GET /tickets/search` and the CLI
  `entkube tickets search`; there are no MCP tools, as for every ticket route.
- **The customer card.** Zammad's customer and organisation sidebars are one card here, because
  our Customer is the organisation. It shows open tickets, tickets in the last 12 months, the age
  of the oldest open ticket, the customer's other open tickets, and the reporter's ticket count,
  with links into search.
- **Not done:** quick search across customers and the knowledge base (the KB doesn't exist until
  Phase 5); separate customer and organisation pages (the card and a search cover what they
  showed); SQLite lower-cases only ASCII, so case-insensitive matching of `Å`, `Ä` and `Ö` works on
  Postgres and SQL Server but not in SQLite development databases.

**Phase 4 done (2026-11-17): staying informed.**

- **The bell.** In-app notifications are made by the follow-up sweep from the ticket history, the
  same way as follower digests: one line per person per ticket per pass, never about your own
  actions, with a compare-and-set cursor per tenant. A new or restored server starts the cursor at
  *now* rather than announcing the past.
- **Escalation notices**, once per ticket, clock and deadline. The claim is inserting that row.
- **The notification matrix** is Zammad's events × (mine, unassigned, followed, all). Groups are
  replaced by "unassigned" and "followed".
- **Assignments** are now recorded as `TicketEventKind.Assigned`, not `Note`, so the sweep can
  tell "yours now" apart from other news.
- **Out of office** with a substitute: the rota passes over the absent person, the substitute
  hears about their tickets and gets a *Covering for* overview, and the absences are listed.
- **Presence** is kept in the database (`TicketPresence`, 15-second heartbeat, 45-second window),
  so colleagues on another instance are seen too. Advisory only.
- **The dashboard** is computed on read. First response time is measured inside the support
  window, as §14.4 counts it.
- **A calendar feed** of the person's deadlines at `/support/calendar/{token}.ics`. It is anonymous
  by necessity, so it uses a 256-bit token stored only as a hash, and resetting it retires the old
  address.
- **Test lesson:** "nobody is told about their own" passed with that rule deleted, because the
  test's person would never have been notified anyway. The test now makes them a follower.
- **Not done:** an e-mail column in the matrix (followers already get digests), a notification
  sound, and Zammad's per-browser keyboard shortcuts (Phase 6).

**Phase 5 done (2026-11-17): the knowledge base.**

- **Categories and answers.** Answers are draft, internal, public (optionally scheduled with
  `PublishAt`) or archived, and can be narrowed to one customer. There is one language per answer
  (decision 2), as a tag.
- **One sanitiser.** Bodies go through the same `RichText` allow-list as mail: on save, and again
  on display.
- **Three readers, three rules.** The knowledge base's own list shows everything. `??` in the
  reply composer offers *published* public and internal answers, and never one narrowed to another
  customer, which would put one customer's material in another's mail. The portal's **Help
  articles** shows public, published answers for everyone or for that customer, and re-checks
  each one opened by id.
- **Kept apart from `KnowledgeSection`** (the per-application runbook), which stays internal.
- **Not done:** attachments and video embeds in answers, links between answers, related tickets,
  RSS feeds, and per-role category permissions. Editing needs **Knowledge base: Edit** on the
  member's role (phase 10, and enforced permissions since).

### Phase 3: Find anything
A query parser for Zammad's syntax that **compiles to EF**, not to a search engine.
We run on three providers and N instances, so a node-local index (even the Lucene.NET
we already use for telemetry) would answer differently on each instance. Supported:
`field:value`, quoted phrases, `*`, `[a TO b]` with open ends, `>now-Nh`, `AND`, `OR`,
`!`, and parentheses. **Not supported:** fuzzy `~` and regex, which have no portable
SQL; the manual will say so. Quick search. A search details page. Customer and
organization sidebars and pages, with stats.
*Manual chapter: Advanced search, Customers, Organizations.*

### Phase 4: Stay informed
In-app notifications (a table plus a badge) with Zammad's matrix of event × scope ×
email. Out of office with a substitute. Ticket presence on the existing Redis presence.
These updates must cross instances, because a Blazor circuit is pinned to one
(`docs/running-two-instances.md`). Agent dashboard widgets. A personal iCal feed.
*Manual chapters: Dashboard, User menu & profile settings.*

### Phase 5: Knowledge base
Categories, answers, and the visibility states public, internal, draft, scheduled and archived. Public
answers are shown in the customer portal. `??` inserts an answer into a reply.
Each answer is written in one language of its author's choosing and carries a language tag (decision 2).
*Manual chapter: Knowledge base.*

### Phase 6: Polish
Keyboard shortcuts, tabs, reporting with an .xlsx export, GitHub/GitLab issue links,
a responsive pass over the queue and ticket views for phones, and the highlighter.
*Manual chapters: Keyboard shortcuts, Tabs, Reporting, GitHub/GitLab, Mobile view.*

**Status (2026-11-17): built.** Manual chapters: `keyboard-shortcuts.md` (shortcuts and tabs),
`reporting.md`, `issue-links.md`, `highlighter.md` and `mobile.md`. Tests: `SupportReportTests`,
`IssueLinkTests`, `TicketHighlightTests`, and the Phase 6 section of `TicketWorkRenderTests`.
Decisions made while building it:

- **Shortcuts press buttons; they don't call services.** `ticket-shortcuts.js` clicks whatever is
  on the page marked `data-shortcut="…"`, so a shortcut can never do what the screen doesn't offer
  at that moment. The marks are the contract, and a render test pins them. The keys are single
  letters, not Zammad's `Ctrl`+`Alt` chords, because `Alt` types characters on Swedish keyboards.
  `Shift`+`C` presses Resolve on an open ticket and Close on a resolved one, because closing an
  open ticket starts with stopping its resolution clock.
- **Tabs live in the browser** (`localStorage`, per tenant, at most 30), not in the database. A
  tab is a shortcut to a ticket. It isn't state anybody else needs, and it isn't worth a table
  that the backup bundle and the tenant purge would have to know about.
- **Reporting counts; it doesn't judge.** Whether response and resolution times were met is
  measured per agreement by the existing SLA reports. The `.xlsx` is written by a small
  in-house writer (inline strings, no styles) rather than a spreadsheet library, and text that
  would start a formula is prefixed with `'`, because customers write the titles. The export is
  capped at 6,000 rows (Zammad's cap) and the period at 366 days.
- **Issue links are read-only and only reach configured hosts.** A tracker is a host plus a
  token in the tenant's vault, owned by the tracker row (`VaultSecret.IssueTrackerId`). Linking
  refuses any other host before anything is sent, and reading checks again. The HTTP client
  follows no redirects, because GitLab's token is a header a redirect would carry along. Answers
  are remembered for a minute, keyed by tenant *and* address. The links are hidden from customers
  everywhere: portal, mail and API.
- **Highlights are kept beside the entry, as text.** §14.6 makes the history the record, so marking
  a passage must not edit it. The passage is stored as its words, which must occur in the entry
  (compared without whitespace), rather than as offsets into markup that a sanitiser change
  could shift. The page paints highlights with the CSS Custom Highlight API, which colours text
  without changing the DOM that Blazor diffs.
- **Backup bundle v20:** issue trackers, issue links, highlights, and the tracker token on the
  secret record.

### Phase 7: Loose ends
The three gaps worth closing once Phases 1 to 6 had shipped: deleting a note (decision 5, agreed
but never built), customers downloading their files from the portal, and rich-text notes with
`@@` in the editor.

**Status (2026-11-17): built.** Tests: `RichNoteTests`, the portal section of
`TicketAttachmentTests`, and the Phase 7 parts of `TicketWorkRenderTests` and
`CustomerPortalRenderTests`. Decisions made while building it:

- **"By their author" needs an identity, not a name.** `TicketEvent.Actor` is a display name, and
  two people can share one, so notes written from the ticket view now record
  `TicketEvent.ActorUserId`. A note from before this has none and can never be deleted: nobody can
  prove they wrote it. The window counts from `RecordedAt` (when the note was written), not `At`.
- **Delete removes the entry and its highlights.** Files added with the note stay on the ticket,
  untied from it. What the note already set off isn't undone: mention mails have gone, and a
  follower digest may already have carried it. There is no API route for it, because a token has
  no person to be the author.
- **The portal shows a file only when all of these hold:**
  - it is customer-visible;
  - it is on one of that customer's own tickets, or on the mail filed on one;
  - it isn't from a delivery report (a bounce is an internal note, and it carries our own mail);
  - it isn't from a duplicate merged in from *another* customer. Agents see those; the customer doesn't.

  The download (`/portal/attachments/{customerId}/{id}`) checks the person's access to the
  customer, then asks the service, which applies the same rules. Every refusal is the same 404.
- **Notes use the mail composer's Quill editor** (`TicketNoteEditor`), with `::`, `??` and a new
  `@@` trigger. Picking a colleague writes `@@Name` and ticks them, so the checkbox list and the
  text agree. Stored like a mail: sanitised `DetailHtml`, with its text in `Detail` for search,
  digests, mention mail and the portal. Note drafts are kept as sanitised HTML; a plain-text draft
  from before is converted, so nobody's half-written note is lost.
- **Still not done:** inbound HTML mail is still shown as its text *(done in Phase 8)*.

### Phase 8: Inbound HTML and quick search

**Status (2026-11-18): built.** Tests: the HTML sections of `MailMessageReaderTests` and
`SupportMailTests`, `QuickSearchTests`, and the Phase 8 parts of `TicketWorkRenderTests`. Decisions:

- **HTML is for display only.** At arrival, the mail's HTML part is sanitised by the same allow-list
  (`RichText`) and kept as `InboundMailMessage.BodyHtml`. `Body`, the text, stays what search, the
  triage phrases, the reply cut line and the portal read, which is the line decision 3 drew. A ticket
  opened from the mail gets `Ticket.DescriptionHtml`; a mail filed on a ticket gets the entry's
  `DetailHtml`, headed with the sender (HTML-encoded). Both are sanitised again when filed and again
  when shown.
- **No images, so no tracking pixels.** `img` was never on the allow-list.
- **Pasted pictures were being lost, and are now kept.** A screenshot pasted into a mail is an
  *inline* MIME part, which MimeKit's `Attachments` leaves out, so it was neither shown nor kept.
  `MailAttachments` now also keeps every inline part that isn't the mail's own text.
- **Large HTML is shown as text.** Over 200,000 characters once sanitised (newsletters), the HTML
  isn't kept.
- **Quick search runs on plain words only**, so it can't change what a ticket query means. It finds
  this tenant's customers (every word in the name, with their open-ticket count) and knowledge-base
  answers (title, keywords or text; drafts and internal included, archived not, because the
  searcher is an agent). A customer opens `customer:"…"`: all their tickets, open and closed, at the
  cost of the name being a contains match. An answer opens in the knowledge base via `?answer=`,
  which only opens this tenant's.
- **Still not done:** separate customer and organisation pages. The customer card and these searches
  cover what they showed.

### Phase 9: Mail and sound for notifications

**Status (2026-11-18): built.** Tests: the "By mail" section of `TicketNotificationTests`, and
`NotificationBellRenderTests`. Decisions:

- **The e-mail column follows the bell.** `NotificationPreference.Mail` is a flags column with one
  flag per kind, none by default. Mail goes only where the scope already rings the bell; it never
  widens who hears what. It is sent after the claim that already makes the bell once-only (the
  cursor compare-and-set, or the `NotifiedEscalation` insert), so several instances mail once.
- **No duplicate of the digest.** An *update* to a ticket the person follows is not mailed this
  way, because the follower digest has mailed updates since Phase 1.
- **Sent through the tenant's relay** (`TicketNotifier.NotifyColleagueAsync`, as mentions are),
  behind `INotificationMailer` so tests can record it. A failed mail isn't retried; the bell has it.
- **Sound is per browser and off by default**, like the keyboard shortcuts. It is a two-note chime
  made with Web Audio, so there's no file to ship. It plays when the 30-second refresh finds more
  unread than before. Browsers that block audio until a click are woken by the first click.

### Phase 10: Knowledge-base links and editors

**Status (2026-11-18): built.** Tests: `KnowledgeBaseLinkTests`, and the Phase 10 parts of
`TicketWorkRenderTests`. Decisions:

- **Related answers** (`KbAnswerLink`) are stored once per pair, lower id first, and shown on both.
  The portal lists a related answer only if it passes the same rules as opening it
  (`Published`). One foreign key cascades and the other is `NoAction`, because SQL Server allows only
  one cascade path from `KbAnswers` into the table. Deleting an answer removes its links on both
  sides first. Bundle v21.
- **Answers given on tickets** (`KbAnswerTicket`) are recorded when an agent inserts one with `??`,
  in a reply or a note. They are shown on the ticket and on the answer, to agents only.
- **The editor permission.** `TenantFeature.KnowledgeBase` is appended last; stored permissions
  are numbers. It began opt-in: until some role in a tenant granted it at Edit or above, everyone
  could edit. Since role permissions are enforced (`TenantRole.PermissionSchema` 1) it is an
  ordinary grant — only roles granting Edit, and global admins, may edit. Roles from before were
  carried across once at startup (`TenantRoleService.CarryRolesAcrossAsync`): where nobody had
  been made editor every role got Manage; where editors had been chosen they kept Edit and the
  rest got View. The check is made on the server in each write handler, not only by hiding
  buttons. The knowledge base has no API, so those handlers are the only way to write.
- **Not supported, with reasons** (also in the manual): video embeds (they'd need iframes on the
  allow-list), RSS (the portal requires sign-in), and files on answers.

### Phase 11: One knowledge base, and contracts on the customer

The user's decisions (2026-11-18):

- **One knowledge store.** Each application's runbook (the Knowledge button's sections) moves INTO
  the knowledge base as agents-only answers tied to the application, keeping its revision history and
  review stamps. The app's Knowledge button shows only that app's part. The app's structured facts
  (classification, dependencies, end-of-life) stay on the button.
- **Two edit rules.** Help answers follow the editor permission. An application's runbook stays
  editable by anyone who can open the application.
- **Contracts on the customer alone.** The application's Contract button goes. Annex A moves to the
  customer's Contract page (PR #479).

The merge ships in three releases (full plan from a judged design: `.git/kb-merge-plan.md` locally):

- **A, enabling (this release):** nothing moves and nothing visible switches.
  - A new visibility, **Operations**, which `??` never offers and the portal never shows.
  - A CHECK constraint `CK_KbAnswers_AppShape`. An application's answer is Operations or Archived,
    carries a section and its app's customer, and is never scheduled. The visibility list is positive,
    so a future visibility is refused for app answers until a migration says otherwise.
  - `KbAudience`, the one place that says what may reach a customer, with a per-method ratchet over
    every reader of KbAnswers and a source ratchet over the portal.
  - `ForInsertAsync`: `??` re-reads an answer before inserting it.
  - Save rules for every answer:
    - an unknown id is refused, where it used to create a copy;
    - the application comes from the stored row;
    - a change keeps a revision.
  - Review stamps.
  - Deleting an app, a customer or a tenant removes the app's knowledge first.
  - Bundle v22 with revisions and the move ledger.
  - The converter, in dry run only: startup logs per tenant how sections would convert.
  - Ships with the sanitiser change that keeps tables (#478).
- **B, the move (this release):**
  - The startup mover. Each section moves in its own transaction, claimed by a ledger row; the
    answer keeps the section's id, dates, author and review stamp, its revisions come along as
    markdown, and one more revision records how it converted. An installation that is still on
    release A and edits a moved section is reconciled at the next start: the edit is appended to the
    history, never applied over the answer, and the ledger says *diverged*. A section whose
    application is no longer the tenant's is kept, archived, as a tenant-wide answer (*detached*).
    The older build's version is kept whole — its title too, which Restore applies, and its section is
    named — because it could rename a section without touching the body. A restore runs the mover
    again and says separately how the move went: a move that falls short is not a failed restore.
  - One section or tenant that cannot be moved or carried across is logged, counted as failed and
    retried next start; it no longer stops every later tenant on every start.
  - Titles are cut to 300 characters on a character boundary (a half surrogate pair is refused by
    Npgsql, so that section would never have moved).
  - The application's Knowledge button reads and writes the answers (rich text, History with
    Restore, Still true, the move's verdict). A section not moved yet is shown read-only.
  - The knowledge-base tab lists every application's part, with an Application filter and column;
    an application's answer offers only Operations or Archived and a Section instead of *For*.
  - The health meter reads the answers, and the old rows only where no ledger row exists. Every
    section now has the same 90-day review interval.
  - Quick search labels an application's answer *operations · application*.
  - The old section editing in `KnowledgeService` is gone.
  - **Found on the way: markdown was rendered unsanitised.** `DisableHtml` escaped raw tags, but
    Markdig's generic attributes rendered `{onclick=…}` on links and headings as live handlers and it
    filters no link scheme, so `javascript:` links were live too. This was true of the old runbook
    panel on main; release B shows the markdown in more places (History). The rendered HTML now goes
    through the same allow-list as mail and answers.
  - **Deploy only after release A has run on every instance**: an instance on an older release
    still writes the old tables, which is what the reconciliation is for, not a way to run.
  - Still to check in a real browser: a table pasted into the editor survives a save and reload
    (Quill's table module; bUnit cannot see it).
- **C, at least a release later:** drop the old tables, only when every installation logs zero left
  to move.

Found on the way: a backup restore with *wipe* truncated three root tables and trusted CASCADE to
reach the rest. Every table that keeps its tenant as a plain column was left behind, including
knowledge-base categories, notification settings, issue trackers, dashboards, CA-trust bundles,
telemetry alert rules and SLOs, so the restore collided with leftover rows. The tables the roots
miss are now computed from the model and truncated too.

## Decisions (2026-11-16)

1. **No groups. The tenant is the queue.** One support team works every customer, and
   the SLA-sorted queue was built around that. Wherever Zammad scopes something
   "per group" (text modules, macros, templates, shared drafts, overviews), we scope it
   per tenant, optionally narrowed to one customer. Access stays what it is today:
   tenant membership.
2. **KB answers are in any language, one language per answer.** An answer is content,
   not UI, so the English-only rule doesn't apply to it. There are no translation sets.
   Each answer carries a language tag so the portal and `??` can show it.
3. **Rich text and HTML mail.** The composer becomes a rich-text editor, and outbound mail
   becomes `multipart/alternative` (HTML plus a generated text part). What this commits us to:
   - **Sanitising runs on the server, on the way in and on the way out.** Inbound HTML
     mail is sanitised before it is stored or rendered, and agent HTML before it is sent.
     We use an allow-list sanitiser; a hand-rolled one is not acceptable.
   - **The reply cut line must survive HTML.** `SupportReplyBody` cuts plain text
     today. A reply from an HTML mail client must be cut on its text part, or on the
     HTML converted to text. It must never be cut on a heuristic over markup, because a
     wrong cut leaks what sits below the line (the §14.4 deadline, the unconfirmed
     priority) to the customer.
   - Remote images in inbound HTML are not loaded automatically (tracking pixels).
   - The `::`, `@@` and `??` popups live in the editor.
4. **Attachments are stored in S3, with metadata in the database.** They are no longer
   dropped at ingest. What this commits us to:
   - **The backup bundle covers attachment *metadata* only.** A restore brings back
     rows that point at objects. The restore and installer docs must say that the bucket
     is backed up separately, and the UI must render "attachment unavailable" for a row
     whose object is missing rather than failing.
   - Support needs S3 configured. An installation without it **refuses uploads, saying
     why**, and keeps dropping inbound attachments with a visible note on the ticket. It
     never falls back to the database silently.
   - Object keys are tenant-prefixed, and downloads go through EntKube, never through a
     presigned URL handed to a customer. Every download is an authorised, tenant-scoped
     read.
   - Per-file and per-message size caps, and content-type sniffing on download.
5. **Article deletion: internal notes only**, by their author, within 10 minutes. A
   customer-visible article may already have been mailed, and it is part of the §14.3
   record. Zammad's "switch it to internal first, then delete" route does not exist here.
