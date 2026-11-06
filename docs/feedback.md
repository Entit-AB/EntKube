# In-app feedback

A widget for your own web applications. A visitor can report a bug, suggest an idea, ask a
question or leave praise, give a 1 to 5 rating, and attach an annotated screenshot. What they
send lands in an inbox, and an operator decides which of it becomes a ticket.

## Switching it on

Feedback is off for every site until you enable it, because it stores free text and images from
anonymous visitors.

1. Open **Observability › RUM › Sites** and edit a site, or create one.
2. Set **Allowed origins**, and switch on **Accept in-app feedback from this site**.
3. Give the site an **app owner** if you want to be able to raise tickets from its feedback.
4. Check that your privacy notice covers what visitors send.
5. Choose **Embed** and paste the feedback snippet into your site's `<head>`:

```html
<script src="https://entkube.example.com/feedback/v1/widget.js" data-key="SITE_KEY" defer></script>
```

`data-lang="sv"` or `data-lang="en"` overrides the visitor's browser language. Sites with a strict
Content Security Policy must allow the EntKube origin in `script-src` and `connect-src`, and
`'unsafe-inline'` or a nonce in `style-src`, because the widget carries its own styles.

## Triage

Open **Support › Tickets › Customer feedback**. Each item shows the message, rating, page, browser,
the last few JavaScript errors on the page and the RUM session id when the RUM snippet runs on the
same page. Mark items Triaged, Resolved or Dismissed, or use **Create ticket**.

Customers see their own sites' feedback, read-only, in the portal's RUM view. That applies only to
sites with an app owner.

Feedback never becomes a ticket on its own. A ticket starts contractual clocks, and the widget is a
public endpoint, so a person decides. A ticket's reporting time is the moment it was created, not
the moment the visitor pressed Send.

## Reading it from elsewhere

| Surface | Use |
|---|---|
| API | `GET /api/v1/feedback?status=New` (scope `ops:read`) |
| CLI | `entkube feedback --status New` |
| MCP | `entkube_list_feedback` |

Screenshots are not returned by the API.

## Screenshots and privacy

The browser asks the visitor what to share and shows them exactly what is captured. On devices with
no screen capture, such as phones, the visitor picks an image instead. Before sending they can draw
on it, and **Black out** permanently removes what is underneath: the image is flattened in the
browser, so the covered pixels never leave it.

The widget never reads the query string or fragment, because that is where tokens live. The server
strips them as well.

| Setting | Default | Meaning |
|---|---|---|
| `Feedback:ScreenshotRetentionDays` | 90 | Screenshots are deleted after this many days |
| `Feedback:RetentionDays` | 365 | Items are deleted after this many days. Never less than the setting above |
| `Feedback:MaxPerSitePerDay` | 500 | Reports a site may store in a rolling 24 hours |

Screenshots are left out of backup bundles so that a backup file cannot outlive their retention.
The items themselves are included.

## What protects the endpoint

The site key is public, so the guards are these: feedback must be switched on for the site, the
Origin must be on the allow-list, each IP may send 20 reports a minute, and each site has the daily
quota above. An Origin header only stops other people's web pages, not scripts, which is why the
quota exists. Images must really be PNG or JPEG, judged from their bytes, and may be at most 3 MiB.

The reporter's email address is unverified. It is shown to operators labelled as such and is never
used as a ticket's mail recipient, because anyone can type anyone's address into the form.
