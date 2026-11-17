# GitHub and GitLab issues

A ticket can be linked to issues on GitHub or GitLab, for example the bug the developers are
fixing for it. The ticket then shows each issue's title, whether it's open or closed, its labels,
assignees and milestone, read live from the tracker.

## Setting up a tracker (administrators)

Under **Support › Ticket tools › Issue trackers**, add each host your issues live on:

| Field | |
|---|---|
| Kind | GitHub (github.com or GitHub Enterprise) or GitLab (gitlab.com or self-managed). |
| Host | The name only: `github.com`, `gitlab.example.se`. No path, port or IP address. |
| API token | Optional. Without one, only public issues can be read. A read-only token is enough: on GitHub a fine-grained token with *Issues: read*, on GitLab a token with `read_api`. The token is kept in the tenant's vault, never in the database itself. |
| Switched on | Off stops issues on that host being read. Existing links stay. |

When you change a tracker, leave the token empty to keep the stored one, or use **Remove the token**.
Removing a tracker removes its token. Issues already linked on that host stay on their tickets as
plain links.

## Linking an issue

In a ticket's side panel, under **Issues**, paste the issue's address and press **Link**:

- GitHub: `https://github.com/owner/repo/issues/12`
- GitLab: `https://gitlab.example.se/group/project/-/issues/7`

Only issues can be linked, not pull or merge requests, and only on a host set up as above. The
**×** unlinks an issue. If an issue can't be read, the link stays and the panel says why: the token
was refused, the issue wasn't found (or the token can't see it), or the tracker didn't answer in time.

## What EntKube does and doesn't do

- **It only reads.** EntKube never comments on, labels or closes an issue.
- **It only calls the hosts set up here.** A link to any other host is refused before anything
  is sent, so a ticket can't make EntKube send a token, or a request, somewhere nobody chose.
- **It reads each issue when the ticket opens**, and remembers the answer for a minute.
- **Customers never see the links.** They aren't in the portal, in mail to the customer, or in
  the API's ticket. An engineering issue can say things about a customer's system that weren't
  written for the customer.
