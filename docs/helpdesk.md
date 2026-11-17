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
- **Not yet:** customers downloading attachments from the portal, and an HTTP-level test of the
  download endpoint (it needs a cookie-authenticated test host, which the API fixture isn't).

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
  customer downloads from the portal exist yet.

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
