# Application management and support

EntKube as the system of record for an application-management agreement: what was
agreed, what was done under it, how long it took, and what that comes to at the
end of the month.

Built against ENTIT AB's *Avtal om applikationsförvaltning och support*. Clause
numbers in the code and below (§14.4, §10.2.1, …) are that agreement's; the
customer it was first signed with is not named here or anywhere in the code, and
examples use ENTIT's own name and the reserved `entit.example` domain instead.

## The problem

EntKube knew how to run an application and nothing about the relationship it was
being run under. Whether a fault was answered in time, whether the hours spent
came out of a bank or were billable, which support window an application had
bought, who to phone at three in the morning, what the customer is owed when we
miss — none of it was anywhere, so all of it lived in a spreadsheet and somebody's
memory.

The agreement is specific enough to implement. Its response times are in hours,
its time categories in surcharges, its penalty in a percentage. What it needs is
somewhere to keep the terms, a clock that counts the way it says to count, and a
report shaped the way it asks.

## The calendar underneath everything

Nearly every figure in the agreement is measured in a Swedish working day, and
that turns out to be the load-bearing part.

[`SwedishHolidays`](../src/EntKube.Web/Services/Support/SwedishHolidays.cs) knows
the thirteen public holidays — five of them derived from Easter via the anonymous
Gregorian algorithm — and, separately, the three eves §9 adds that are *not*
public holidays in law: midsommarafton, julafton, nyårsafton. The two sets are
kept apart deliberately: one is Swedish law, the other is a clause, and another
customer could reasonably be sold a different one.

[`BusinessCalendar`](../src/EntKube.Web/Services/Support/BusinessCalendar.cs)
turns that into window arithmetic: is the window open at this instant, how much
open time lies between two instants, when does a budget starting here run out.

Everything here is **pure and clock-free** — a function of its arguments, no
database, no `DateTime.UtcNow`. This arithmetic decides whether a penalty is owed,
so it has to be checkable without standing anything up.

### The one trap, which has now been sprung twice

Every `DateTime` read back from the database arrives as
`DateTimeKind.Unspecified` — that is what SQLite, `timestamp without time zone`
and `datetime2` all return — and everything in EntKube stores UTC. Calling
`ToUniversalTime()` on such a value reinterprets it in the *server's* zone. On a
machine set to Swedish time that shifted every stored instant by two hours and
produced negative worked time.

`BusinessCalendar.ToLocal` treats Unspecified as UTC, and is the only correct way
to get a Swedish wall-clock time or calendar date out of a stored instant.

The second time this bit was subtler: `TicketClock` counted a pause's working days
with `DateOnly.FromDateTime(pause.StartedAt)` — the *UTC* date, going around
`ToLocal` rather than through it. A pause from Saturday 00:30 to Monday 00:30 in
Stockholm covers one working day; the same two instants read as UTC dates are
Friday and Sunday and cover none.

**So: never build a `DateOnly` straight off a stored instant.** There is a
regression test for each of these.

## Support windows and time categories

Two tables that look alike and are not.

| Window | Hours |
|---|---|
| S1 | Working days 08:00–17:00 |
| S2 | Working days 05:00–22:00 |
| S3 | All days 05:00–22:00 |
| S4 | Around the clock |

A window is what an application *bought*, and it is what SLA clocks run inside.

The §13 time categories — ordinary, evening/morning, weekend or red day, night,
call-out, development — decide what an hour *costs*, and they are chosen by **when
the work was done, not by which window the application bought**. §13 is explicit
about that, and reading the two tables side by side is the obvious way to get it
wrong. [`SupportTimeCategoryRates`](../src/EntKube.Web/Services/Support/SupportTimeCategoryRates.cs)
holds the surcharges, the hour-bank factors and the minimum charges;
[`WorkPassCalculator`](../src/EntKube.Web/Services/Time/WorkPass.cs) splits a span
of work across the categories it crossed.

## The annexes, as data

| | What it is | Where |
|---|---|---|
| **Annex A** | Per application: origin, on-boarding date, management level, support window, criticality — each dated, so an earlier month prices at what was true then | [`ApplicationContract`](../src/EntKube.Web/Data/ApplicationContract.cs), [`ApplicationServiceLevel`](../src/EntKube.Web/Data/ApplicationServiceLevel.cs) |
| **Annex B** | Per customer portfolio: pricing model A (hour bank) or B (time and materials). §2 allows a change at a quarter boundary | [`PortfolioAgreement`](../src/EntKube.Web/Data/PortfolioAgreement.cs) |
| **Annex C** | The price list, versioned. §19 indexes it every January | [`PriceList`](../src/EntKube.Web/Data/PriceList.cs) |
| §23 contacts | Technical contact, deputy, escalation levels 2 and 3, both sides | [`ContractContact`](../src/EntKube.Web/Data/ContractContact.cs) |

Everything dated is resolved **as of a date** rather than read as "current", and
[`ContractService`](../src/EntKube.Web/Services/Contracts/ContractService.cs) is
the only place that resolution happens.

### Gaps are reported, not zero-priced

An application with no service level, or a level with no price in the list in
force, is **not** silently charged nothing. `CalculateBaseFeeAsync` returns an
`Unpriced` list alongside the total, and the UI shows it as "not being charged
for". Quietly pricing a gap at zero is how a customer stops paying for something
nobody notices for a year.

## Tickets and the clocks

[`TicketSla`](../src/EntKube.Web/Services/Tickets/TicketSla.cs) is the §14.4 table
in one place — a second copy of it somewhere else is a copy that will be wrong.

| | P1 | P2 | P3 | P4 |
|---|---|---|---|---|
| Response | 2 h | 4 h | 1 working day | 2 working days |
| Resolution | 8 h | 24 h | 5 working days | next release |
| Status update | hourly | 4-hourly | on change | on change |
| Escalate to level 2 | 4 h | 16 h | — | — |

The update interval is counted from **the last thing the customer was actually told** — a
customer-visible event — not from registration, because the obligation is that they are not
left in silence and it resets whenever the silence is broken. A note we write to ourselves
does not discharge it. P3 and P4 have no fixed interval (§14.4 gives them "at a change of
status"), so inventing one would put a red badge on half the queue.

It is the one clock here paused along with resolution. Everywhere else this subsystem takes
the reading that does not excuse us, but the point of an update is that the customer is not
left wondering — and while a pause is theirs, they are the one holding the information.
Demanding hourly updates meanwhile would fill the queue with red and teach everybody to
ignore the colour, which costs more than it protects.

[`TicketClock`](../src/EntKube.Web/Services/Tickets/TicketClock.cs) counts against
these. Three rules do all the work:

1. **Time counts only inside the support window.** A P1 raised on Friday evening
   under S1 is not late on Saturday.
2. **The resolution clock stops while we wait on the customer or a third party**
   (§14.4), tracked as a pause ledger.
3. **A reprioritisation restarts the targets** from when it happened (§14.3), so
   a clock runs from the later of registration and the current priority taking
   effect.

**Pauses do not stop the response clock.** §14.4 pauses resolution time by name
and says nothing about response time. Since a missed response time is the only
thing that carries a penalty, the narrower reading is the one that cannot be
accused of excusing our own lateness.

### Before the SLA starts

§4.1 and §8 begin the response and resolution times at a **signed start protocol**. A
ticket raised before that date is handled on a best-effort basis with no guaranteed
response, and nothing before it is reported as a breach or counted as a §14.6 deviation.
The clocks still run and are still shown — a red one that carries no penalty should say
which it is.

The answer is recorded on the ticket when it is created, like the support window, because
what counts is the terms in force when it arrived. A protocol signed afterwards must not
turn a best-effort week into a month of breaches.

**A contract with no protocol date is not an exemption.** It is ambiguous between "not
started" and "nobody filled it in", and it resolves against us: claiming best-effort on the
strength of a blank field would let our own missing paperwork excuse a breach. The
exemption is taken only where a date says so.

### The written report §14.6 asks for

Closing a P1 starts five working days in which a written report is owed. The deadline was
computed and displayed from the beginning, and nothing could say it had been met — so the
line stayed amber for ever and the obligation could be ignored but not discharged. It can
now be recorded as delivered, which is a customer-visible event, because the report is a
deliverable to them.

It is also listed in the monthly report while it is outstanding. A closed ticket leaves the
queue, so without that the obligation has nowhere left to be seen — and it is counted
against the whole history rather than the month, since an undelivered report does not stop
being owed when the month turns over.

### What the penalty actually attaches to

Only a missed **response** time, and only on P1 or P2. An overrun resolution time
is a missed goal, not a breach. The penalty is 10% of the month's window fee, at
most three times a calendar year; more than two P1–P2 deviations in a quarter
obliges a written action plan. Past the cap the money stops but §14.6 lets the
customer force the window down to S1, which costs considerably more — so the
report shows deviations remaining, not just deviations spent.

## Hours and the timbank

[`TimeService`](../src/EntKube.Web/Services/Time/TimeService.cs) books worked time
at the §20 granularity and draws it against the hour bank. Unused hours expire at
month end (§11.1); once the bank is spent, further work needs the customer's
approval, which the portal collects and records by name.

§12 is the same act for Model B: the customer may set a **monthly ceiling per
application**, and work past it needs their approval — except P1 and P2, which proceed
without delay, because a ceiling that stopped an emergency is one nobody could agree to.
Both rules are counted chronologically rather than by totals, so an emergency raised after
the line was already crossed excuses itself and not the ordinary work that crossed it.

**Billing is reports only.** How much of the bank is spent, how many hours were
committed. Invoicing happens in a different system and nothing here tries to
produce one.

## Knowledge — "kännedom"

[`KnowledgeService`](../src/EntKube.Web/Services/Knowledge/KnowledgeService.cs)
holds what an on-call engineer needs that is not in a manifest: architecture
notes, runbooks, data classification, backup responsibility, technical contacts,
upstream dependencies, end-of-life notices. Sections are markdown with revision
history.

Rendering uses Markdig with **`DisableHtml()`**. This text is written by operators
and shown to customers; Markdig passes raw HTML through by default, which would
make a knowledge section a script-injection point against everyone who reads it.
That matters more than it looks, because the intended way to fill a section is to
paste a document written somewhere else — which is exactly where a stray tag comes
from.

**Pasting a whole document has to work**, and for a while it did not. Blazor Server
caps anything a browser sends over the circuit at 32 KB by default, and the editor
was bound on `oninput`, so the entire body went over the wire on every keystroke. A
pasted architecture guide exceeded the cap, the hub refused the message, the text
never reached the server, and the section saved as though nothing had been typed —
with no error anywhere. The cap is now sized for a document
(`Program.MaximumDocumentBytes`), and the body binds on change, so editing a large
section does not re-send it per character. The cost is that the preview catches up
when you click out of the box rather than as you type.

## The support mailbox

§14.3 accepts e-mail as a reporting channel for P3 and P4, and in practice
everything arrives there first.

```
IMAP  →  SupportMailPoller  →  MailMessageReader  →  SupportMailService.IngestAsync
                                                              ↓
                                            ISupportMailAnalyst proposes
                                                              ↓
                                          a person accepts, by name  →  ticket
```

A reply is threaded onto its ticket by the Message-Id we sent, which carries the ticket
number —
[`SupportMessageId`](../src/EntKube.Web/Services/Mail/SupportMessageId.cs) writes that
format and is the only thing that reads it. The number is in the identifier so a reply
needs no lookup table and nothing stored: the thread carries the answer back. Subjects are
also matched on `[#1042]`, but subjects get edited, translated by clients and lost to
forwards, and the thread headers survive all three. Only our own identifiers are read — a
reply pointing at a colleague's message must not be mined for a number that merely looks
like one.

[`MailMessageReader`](../src/EntKube.Web/Services/Mail/MailMessageReader.cs) is
separate because it needs no mail server and is where the mistakes are: a missing
Message-Id gets a *derived* digest rather than a fresh guid (a synthesised id that
differed between polls would re-ingest the message every two minutes), a reply
carrying only `References` threads on ours anywhere in the chain rather than blindly on
its last entry, and the `Date` header is not trusted past our own clock — it is written by the sender, and a future date would
start a response clock before the message existed.

### Placing a message

Three registers, in order.

**The address it was sent to**, when the customer has one of their own —
`customer-support@entit.se`. That is a choice somebody made about this message, and it
outranks anything inferred about the sender: a consultant at a third company reporting a
fault on a customer's system has a domain that identifies nobody useful, and a supplier's
engineer has one that identifies the wrong customer entirely. The same address is what
replies come from, so the thread stays on it — answering from the generic address someone
who wrote to theirs teaches them to use the generic address.

These are **aliases, not second mailboxes**: the address must deliver into the mailbox the
tenant already polls, and nothing opens another connection. An alias is expanded before
the message is written, so the address often survives in no header the sender wrote — only
in `Delivered-To` or `X-Original-To`, which is why the reader keeps those too. Miss them
and a customer's own address routes nothing.

**To and Cc are written by the sender, and are treated as a claim.** Anybody can name a
customer's support alias in Cc without the message going anywhere near it, so routing on
those headers alone would let a stranger put their mail into a chosen customer's queue —
and, worse, silence the "nobody recognised this sender" prompt that would have invited a
second look. The two facts are kept in separate fields: `DeliveredTo` is what our own
server recorded and places a message outright; `ToAddresses` is what the sender typed and
places it *with a flag*, because naming the address is also exactly what a consultant
reporting on the customer's behalf would do.

**Then the §23 contacts** by exact address — somebody named in the agreement is the
strongest statement there is about who a *sender* is, and it beats a domain.

**Then the customer's registered mail domains**, longest match first, for the eighty people
at a customer who write in once and were never going to be listed individually.

A registered domain covers its subdomains, on a dot boundary — `entit.example` places
`it.entit.example` but not `notentit.example`, which anybody in the world can register and whose
mail would otherwise be triaged straight into a customer's queue.
[`SenderDomain`](../src/EntKube.Web/Services/Mail/SenderDomain.cs) holds that rule, pure
and tested, and refuses to register a public provider: handing gmail.com to one customer
would place every stranger's mail with them, and the mistake is invisible afterwards.

**From is written by the sender too — and it is what the two strongest registers read.**
A §23 contact match places a message with no caveat at all, which is right when the address
really is the person named in the agreement and badly wrong when somebody has typed it.
Nothing in the message can settle that; only the server that received it can, in the
`Authentication-Results` header where it records what SPF, DKIM and DMARC said.

That header is itself a header, so a sender is free to add one saying their own mail passed
everything. Only a verdict stamped by a server we actually trust counts, identified by its
`authserv-id` — which the tenant names on the mailbox, because it cannot be worked out from
a message. Left empty, every sender is reported as unchecked, and the mailbox screen says
plainly what that means rather than leaving the field looking optional.
[`SenderAuthentication`](../src/EntKube.Web/Services/Mail/SenderAuthentication.cs) is that
parser: pure, and mostly a set of refusals.

The verdict is **flagged, never enforced**. A forwarded message and a mailing list both fail
these checks honestly, and a rule that dropped mail would drop a genuine P1 sooner or later.
So a failure raises `FlagForgedSender` — the only red badge in the inbox — and says more when
the placement rested on the very header that failed than when the message was placed by where
it was delivered. A pass and an unchecked sender raise nothing at all: most tenants will never
name a server, and a caveat on every message would be ignored inside a week.

**Placing the customer is what makes everything else possible.** An application name is
recognised among *that customer's* applications, and the hour bank is theirs — so an
unplaced message cannot be placed on an application either. That is why a message nobody
recognised can be assigned by hand from the inbox, and why doing so **analyses it again**:
the first pass had no customer and could say almost nothing. Assigning can also remember
the sender's domain, which is offered rather than done, because it is a statement about
every future sender there and not only this one.

The application is a separate question and gets the same treatment. The analyst
recognises one by its name appearing in the subject or body, longest name first, so it is
sometimes confidently wrong — and a message saying "everything is broken" names nothing at
all. Accepting therefore carries an `AppChoice`, which distinguishes three answers that a
plain `Guid?` cannot: use what was recognised, use this one instead, and *a person looked
and it is none of them*. A ticket with no application is a valid outcome; refusing to open
one until somebody picks would be worse than the ticket.

[`SupportMailboxService`](../src/EntKube.Web/Services/Mail/SupportMailboxService.cs)
stores settings per tenant with the password in the tenant's vault. A new mailbox
starts **switched off**; five failed polls in a row stop it, because presenting a
rejected password over and over is how a service account gets locked.

Progress is tracked by IMAP UID rather than the read flag, so a mailbox configured
to touch nothing still reads each message once, and a shared mailbox a person also
reads does not have messages marked seen out from under them.
[`MailboxCursor`](../src/EntKube.Web/Services/Mail/MailboxCursor.cs) holds that
decision on its own, because it is the half that goes wrong quietly: UIDs are only
unique within one UIDVALIDITY, and a cursor carried across a rebuilt mailbox skips
whatever now sits below it — permanently, with no error, looking exactly like a
quiet week from that customer. On a change of validity the cursor is dropped and
the folder read again; the message-id check is what stops that becoming a second
set of tickets.

### Why there is no language model behind the analyst

[`ISupportMailAnalyst`](../src/EntKube.Web/Services/Mail/ISupportMailAnalyst.cs)
is a seam, and the only implementation is
[`RuleBasedMailAnalyst`](../src/EntKube.Web/Services/Mail/RuleBasedMailAnalyst.cs)
— configurable phrase tables, edited in the UI.

Support messages for this customer contain patient data. Sending them to a model
provider makes that provider a sub-processor, which §17 requires a data-processing
agreement for and §18 requires the customer to approve. Neither exists. The seam
is there so the decision can be made later, deliberately, by someone who has done
that paperwork — **not so a model can be quietly dropped in behind it.**

## Machine proposes, human decides

Nothing in this subsystem changes state on its own.

The analyst reads and drafts. The monitoring bridge opens a ticket but does not
set its priority. Short-notice maintenance is flagged, never blocked. Every state
change goes through a person accepting it, and their name lands on the resulting
event.

This is not caution for its own sake: §14.3 makes confirming a priority a written
and reasoned act, §14.4 makes a resolution something the customer agrees to, and
§14.6 attaches real money to a mis-clocked P1. What the machine is good at is
having the paperwork ready.

The name comes from
[`CurrentActor`](../src/EntKube.Web/Services/CurrentActor.cs), which a component
injects. It used to come from an `ActorName` parameter, and almost nothing passed
one — the ticket queue rendered the detail without it, the tenant tree rendered
the support inbox without it — so every accept, reject, resolution and knowledge
revision was filed under `"unattributed"`. The property the whole design rests on
was not true of the running system, because asking a parameter to carry it made
every call site a place to forget. Reading the name also never fails: an action
that cannot be taken because the identity could not be read is an outage made out
of bookkeeping.

## Telling people a ticket exists

Three audiences, from inside `TicketService.CreateAsync` rather than from its callers. A
ticket arrives by three routes — the portal, the mailbox, monitoring — and "remember to
tell somebody" at three call sites is how it ends up done at none.

- **The reporter** gets a receipt: the reference, what they reported, the support hours,
  and when they will hear back. See below.
- **The customer's designated contact**, whom §14.1 requires be told of every incident.
  The agreement has said so from the beginning; nothing sent them anything until now.
- **Whoever is on call**, with the number, the clock and the description — not the
  customer's receipt, which explains things they already know.

Failing to reach any of them never fails the ticket. A P1 that cannot be recorded because
a mail server is refusing connections is an outage made out of bookkeeping.

### The one thing sent without a person

Everything else the machine could say to a customer is a judgement — what priority this
is, whether it is resolved — and §14.3 and §14.4 make those written acts by a person. A
receipt is not a judgement. It is a fact about the past: a message arrived, at this time,
and is now numbered. §14.6 makes those timestamps the record between the parties, which
argues for telling the customer what we recorded rather than for keeping it.

So [`TicketAcknowledgement`](../src/EntKube.Web/Services/Tickets/TicketAcknowledgement.cs)
is pure and its wording is tested. The rule it keeps: **the priority is given as reported,
never as decided.** Until the first assessment §14.3 gives the customer's own assessment
precedence, and a receipt announcing "Priority: P2" reads as our decision — binding us to
something nobody assessed, or looking like a downgrade of what they told us. It also tells
them the confirmation is still coming and that they may disagree with it, because a
precedence nobody knows they have is worth nothing.

The response target is the useful sentence: a deadline computed inside the support window
is the one thing the customer cannot work out for themselves.

## Who is working on it

`Ticket.Assignee` is a claim, not a dispatch — nobody is assigned work by the system.
What ownership prevents is two people working the same fault without either knowing, which
is the failure an unowned queue actually produces. Handovers are recorded on the ticket and
are **not** customer-visible: §14.1 promises them a named contact of theirs, not a view of
our rota.

## Subconsultants (§18)

Anybody outside our payroll who takes on-call duty, handles an incident, fixes a bug or
deploys. §18 makes us answerable for their work as for our own and attaches three
conditions, all of which are dates rather than opinions: bound by confidentiality, bound by
§17 data-processing terms, and notified to the customer for approval — plus a current list
kept available on request.

**Approval is tacit.** The customer has ten working days to object, after which somebody
counts as approved. That is a working-day calculation on the §9 calendar, so the register
shows the date rather than leaving anyone to work it out.

`OnCallService.MayAccessCustomerEnvironments` is the gate: active, both undertakings in
place, and approved. The register leads with whoever fails it and says which specific thing
is missing, because §18 wants the terms in place *before* anybody touches a customer's
environment and "not allowed" is not an actionable sentence.

All of this was written and tested when the subsystem was, and none of it was reachable —
no way to register a person, no way to record that the customer had been told, and the list
§18 requires could only ever be empty. The rules were never the gap; the door was.

**The customer sees this too.** §18 gives them a current list available on request and ten
working days to object to a new name; the portal's "Who works on this" shows the list,
which ones are waiting on them and the date silence becomes approval, and lets an operator
approve or object. An objection needs a reason — §18 says approval may not be unreasonably
withheld, which makes the reason the substance of the objection rather than a note attached
to it. The list is narrowed to the people engaged for that customer plus those engaged
across the tenant: showing one customer another's subconsultants is the same mistake as
naming another customer's maintenance windows.

A shift can now say who is covering it and how to reach them. `AddShiftAsync` took a name
and an address, while the entity's own comment observed that at three in the morning an
address is not a way to reach a person — correct, and there was no field for anything else.
It carries a phone number and a Teams handle (§14.3's contact channels for P1 and P2), and
names the subconsultant when one is covering. That last one **refuses** rather than warns:
a roster is consulted at three in the morning by somebody who will not be re-reading the
register, so the check belongs where the shift is created.

## Maintenance and availability

Time under an agreed maintenance window is **excluded from measured availability**
— agreed downtime is not unavailability, and counting it reports the customer a
worse figure than they are entitled to. How many samples were excluded is reported
beside the figure, so a near-perfect month resting on very little measurement
looks like one, and a window drawn around an outage after the fact is visible.

[`MaintenanceNotice`](../src/EntKube.Web/Services/Support/MaintenanceNotice.cs)
counts the notice a window gave, in working days, on the same calendar as the SLA
clocks — so the week before Christmas is three working days, not five. The figure
appears while the start date is still being picked, and short notice is named in
the monthly report. Nothing refuses a window: maintenance sometimes has to happen
sooner than the agreement would like, and an operator who knows that is not helped
by a disabled button.

Short notice does not withdraw the exclusion. The exclusion is what the customer
is owed, not a verdict on how we behaved.

## The monthly report

[`MonthlyReportService`](../src/EntKube.Web/Services/Reporting/MonthlyReportService.cs)
assembles what §16.1 asks for, due by the fifth of the following month: uptime per
application against its target, tickets and SLA compliance per priority band, open
tickets at month end, the hour bank, committed hours, penalty deviations and what
they come to.

**Computed, never stored.** A report re-run for March must say what March said, and
everything it rests on is already immutable or dated — so recomputing is safer than
snapshotting something that can drift from its own sources.

§28 makes what it says binding unless the customer disputes it within thirty days,
which is also why editing a price list in place carries a warning: a figure that
moves underneath a report already sent is how a dispute starts.

### One thing to be careful of

A tenant hosts several customers and the report goes to one of them. Anything it
shows must be narrowed to that customer's own apps and clusters — with
`ClusterId == null` still meaning tenant-wide. This was got wrong once already:
the report named every maintenance window in the tenant, so a customer's copy
listed maintenance on somebody else's cluster.

## Where it appears

**Tenant view.** This subsystem is almost all of one section — **Support**, which
is where somebody is waiting for an answer:

```
Support
  Alerting          alerts, rules, routing, notification channels
  Incidents         on-call rota, incidents
  Mail              support inbox, mailbox connection, triage phrases
  Service levels    SLA
```

The order is the path a problem travels: something fires, it becomes an incident
somebody is on call for, and a customer writes in about it. Alerting leads for
that reason — filing the alert rules under **Observability** with the dashboards
they were written against is true of how they are authored and useless for how
they are used.

What is left in **Operations** is work on the platform rather than for a person:
the advisor's priorities, maintenance windows, the VPN mesh, disaster recovery,
lifecycle, delivery, access and cost.

The per-record panels hang off what they describe: Annex A on the application,
Annex B/C and the §23 contacts on the customer, the knowledge panel on the
application.

**Customer portal.** `Support` (raise, reply, accept a resolution), `Service
report`, `Hours` behind the operator gate, and upcoming maintenance.

`Service report` is its own entry rather than a block on the Monitoring page.
The monthly report is the agreement's document, not a dashboard: §28 makes what
it says binding unless disputed within thirty days, and it states any penalty
credit the customer can claim. Monitoring keeps the things that are live or
rolling — the health dashboard, thirty-day uptime against target, recent
incidents.

## What this deliberately does not do

- **No invoicing.** Reports only; the money is raised elsewhere.
- **No automatic prioritisation.** A machine may propose P1; a person confirms it.
- **No language model.** See above — this is a legal position, not a technical one.
- **No enforcement of the maintenance notice.** The shortfall is made visible at
  the moment it is created and again in the monthly report, under the name of
  whoever scheduled it. It is not made impossible.
- **Maintenance windows do not extend the SLA clocks.** They are excluded from
  availability only. Whether a P1 raised during agreed maintenance should have its
  response clock paused is a question the agreement does not answer, and guessing
  would be guessing in our own favour.

## Tests

310 test methods across sixteen files (more cases than that, since several are
theories), each named after the clause it defends —
`A_P1_response_is_due_two_hours_later`, `The_same_week_over_Christmas_is_not_enough`,
`A_date_in_the_future_is_not_believed`. The calendar, the clocks, the pricing and
the mail reader are all pure, so they are tested directly rather than through a
database.

Outbound mail is tested over a real socket. `SmtpSink` is a listener on loopback
that speaks the handful of verbs MailKit needs and keeps what it is given, so the
tests can check that a message actually leaves, that everybody who should hear
does, and that one stale address in the contact register does not cost the on-call
engineer their notification. The code under test constructs its own `SmtpClient`,
so the alternative was an abstraction whose only user is the test — and the
abstraction would then be the thing that was verified.

What is **not** covered: nothing here has run against a real IMAP server, and the
monthly report has not been reconciled against an invoice produced by the billing
system it is meant to feed.
