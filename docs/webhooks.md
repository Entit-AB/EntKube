# Outbound webhooks

A notification channel of type **Webhook** posts alert events as JSON to a URL you
control. Two things about that path are worth understanding before you use it.

## Destination validation

EntKube's management plane sits somewhere privileged: it can reach every cluster it
manages, its own loopback, and — on a cloud instance — the link-local metadata
service at `169.254.169.254` that hands out instance credentials.

Webhook URLs are configured by *tenant* users, who are not necessarily trusted with
that network position. So every operator-supplied destination is validated before the
request is made, and anything that is not publicly routable is refused:

- loopback (`127.0.0.0/8`, `::1`, `localhost`)
- link-local (`169.254.0.0/16`, `fe80::/10`) — the cloud metadata range
- private (`10/8`, `172.16/12`, `192.168/16`, `fc00::/7`)
- carrier-grade NAT (`100.64/10`), unspecified, multicast, broadcast
- IPv4-mapped IPv6 forms of any of the above (`::ffff:169.254.169.254`)
- non-`http(s)` schemes, and credentials embedded in the URL

Hostnames are resolved and **every** address they return must be public — a name
resolving to both a public and a private address is refused, since that is the shape
of a rebinding attempt.

If you genuinely need an internal receiver, allow it instance-wide:

```
Notifications__AllowPrivateWebhookTargets=true
```

That is deliberately **not** a per-tenant setting. The reason the check exists is
that the tenant is not the party who gets to decide it.

**Residual risk**: a hostname that resolves to a public address at validation time
and a private one when the request is made (DNS rebinding) still gets through.
Closing that requires pinning the connection to the validated address via a custom
`SocketsHttpHandler` connect callback — a worthwhile follow-up, and larger than the
check itself.

## Verifying deliveries

Set a **signing secret** on the channel and each delivery carries:

| Header | Contents |
|---|---|
| `X-EntKube-Signature-256` | `sha256=<hex>` HMAC over `{timestamp}.{body}` |
| `X-EntKube-Timestamp` | Unix seconds, also inside the signed material |
| `X-EntKube-Event` | `alert.firing` or `alert.resolved` |

Without a signature a receiver cannot tell an EntKube delivery from anyone else who
learned the URL — and webhook URLs leak, through logs, browser history and
copy-paste. A bearer token proves the sender knew a secret but says nothing about the
body; an HMAC proves both.

The timestamp is signed *with* the body rather than merely sent beside it, so a
captured delivery cannot be replayed later with a fresh timestamp.

Verify it like this, and reject anything whose timestamp is outside a tolerance
window of a few minutes:

```python
import hmac, hashlib, time

def verify(secret: str, timestamp: str, body: bytes, signature: str) -> bool:
    if abs(time.time() - int(timestamp)) > 300:      # replay window
        return False
    expected = "sha256=" + hmac.new(
        secret.encode(), f"{timestamp}.".encode() + body, hashlib.sha256
    ).hexdigest()
    return hmac.compare_digest(expected, signature)  # constant time
```

Compare in constant time. A byte-by-byte comparison that exits early leaks how much
of a guessed signature was right, which is enough to forge one a byte at a time.
