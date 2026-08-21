# EntKube egress agent

A small process you run inside a network that is allowed to reach an endpoint
EntKube cannot.

## When you need it

Some providers restrict their API to an IP allowlist. If EntKube runs somewhere
that is not on that list, every call fails — and there are only so many ways to
fix that:

| Situation | Use |
| --- | --- |
| EntKube's own egress IP is allowed | **Direct** — nothing to configure |
| A proxy exists on a permitted network, and EntKube can open a connection to it | **Proxy** — set the proxy URL on the connection |
| A cluster EntKube manages sits inside the provider's environment | **Cluster relay** — EntKube deploys nginx there and reaches it via the cluster's API server |
| Only a network that permits no inbound traffic is allowed | **This agent** |

The agent exists for the last row. The difficulty there is one of direction: the
permitted network can reach the provider, but nothing can reach *into* that
network to make use of it. So the connection is established from the inside out.

## How it works

The agent dials out to EntKube over HTTPS and holds a WebSocket open. EntKube
then asks it to open TCP connections; the agent dials them from its own network
and relays the bytes.

```
  your network                        internet                    provider
  ┌──────────────┐                                          ┌─────────────────┐
  │ entkube-agent│ ──── outbound wss:// ────▶ EntKube       │  Keystone / S3  │
  │              │                              │           └─────────────────┘
  │              │ ◀─── "open identity.x:443" ──┘                    ▲
  │              │ ─────────────── TCP ──────────────────────────────┘
  └──────────────┘
```

Two properties worth being explicit about, because they are what makes this
acceptable to run:

- **Nothing is published inbound.** No listening socket, no firewall rule, no
  port forward. The agent makes an ordinary outbound HTTPS connection.
- **The agent cannot read the traffic.** TLS is negotiated end-to-end between
  EntKube and the destination, so the agent relays ciphertext. EntKube's
  certificate validation and request signing apply to the real endpoint; nothing
  is terminated or re-signed in the middle.

## The security boundary

The agent can open TCP connections inside your network. That is as sensitive as
it sounds, and the control is the allowlist in the agent's own config file:

**The agent refuses any host not on its local allowlist, regardless of what
EntKube asks for.** EntKube cannot widen it. Changing it means editing config
where the agent runs and restarting it.

Matching is deliberately strict:

- `identity.example.com` matches that host only, case-insensitively.
- `*.example.com` matches `s3.example.com` but **not** `example.com`, and not
  `evilexample.com` or `example.com.attacker.net`.
- Ports are listed separately; allowing a host does not open other services on it.

If you want the tightest possible configuration, list exact hostnames and only
port 443.

## Install

Publish the binary:

```bash
dotnet publish src/EntKube.Agent -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true
```

That produces a single `entkube-agent` executable with no runtime to install —
which matters, since these networks often cannot pull a container image either.

Copy it to a host in the permitted network along with an `agent.json`:

```json
{
  "ServerUrl": "https://entkube.example.com",
  "Token": "<generated in EntKube>",
  "AllowedHosts": [
    "identity.example.com",
    "*.citycloud.com"
  ],
  "AllowedPorts": [443]
}
```

Get the token from EntKube: **Storage → Egress Agents → Add Agent**. It is shown
once and only its SHA-256 hash is stored, so a database compromise does not yield
a credential that could impersonate the agent. If you lose it, delete the agent
and register a new one.

Settings can also come from environment variables (`ENTKUBE_ServerUrl`,
`ENTKUBE_Token`) or the command line, if you would rather not have the token in a
file.

### Run it under systemd

```ini
[Unit]
Description=EntKube egress agent
After=network-online.target

[Service]
ExecStart=/opt/entkube/entkube-agent
WorkingDirectory=/opt/entkube
Restart=always
RestartSec=10
User=entkube
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

The agent reconnects on its own with backoff if the link drops, so `Restart` is
only for the process itself dying.

## Use it

On the OpenStack connection, choose **Through an EntKube agent** and select the
agent. The Egress Agents table shows whether it is connected, where it connected
from, and which hosts it reported it will reach — so you can confirm the link
comes from the network you expect without logging into the box.

## Operational notes

- **Availability.** While the agent is down, calls routed through it fail. That
  includes background work — telemetry blob writes, scheduled backups, credential
  rotation. Run it somewhere that stays up, and run two if it matters: EntKube
  accepts multiple links per agent and picks the least loaded.
- **Refusals are explicit.** A host outside the allowlist produces a clear error
  in EntKube naming the host, not a timeout.
- **Disabling.** Clearing an agent's enabled flag refuses its link without
  deleting the registration. Deleting is blocked while a connection still routes
  through it.
