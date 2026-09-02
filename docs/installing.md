# Installing the management plane

There are two installers, and they do the same install.

| | Runs on | Installs to | Use when |
| --- | --- | --- | --- |
| **`entkube-installer`** (GUI) | your desktop | a server over SSH, or the same machine | You have SSH access to the server and would rather not get a binary onto it first. It can also install the client tools locally. |
| **`entkube-install`** (console) | the server | that same machine | You are already on the server, or you are scripting the install. |

They are not two implementations. The install sequence — preflight, render, validate, pull, start,
wait — is one class, `InstallRunner`, and the only thing that differs is whether it is pointed at a
local shell or an SSH connection. A server built by either is byte-for-byte the same.

---

## Before you start

Whichever installer you use, have these ready:

| | Why |
| --- | --- |
| **A server with Docker Engine and Docker Compose v2** | This is what the management plane runs on. Either form of Compose v2 is fine — the `docker compose` CLI plugin or the standalone `docker-compose` binary. The installer checks and stops if neither is there. |
| **SSH access to it** (GUI only) | A username and either a private key or a password. If that user is not root and not in the `docker` group, you also need its `sudo` password. |
| **A DNS record already pointing at the server** | Only for the HTTPS option. Let's Encrypt validates over HTTP on port 80 *before* issuing, so a record added afterwards is too late for the first attempt. |
| **Ports 80 and 443 reachable from the internet** | Same reason. Choose the local-port option instead if the server is not public. |

You do **not** need to prepare a database, object storage, or any secrets. The installer generates
what it needs.

---

## Building an installer

Both installers are built from this repository with `scripts/release.sh`. You need the **.NET 10
SDK**; the GUI additionally bundles the Terraform provider, which needs **Go**.

```bash
scripts/release.sh gui --rid osx-arm64          # the desktop GUI, for your machine
scripts/release.sh installer --rid linux-x64    # the console installer, for the server
```

`--rid` takes `osx-arm64`, `osx-x64`, `linux-x64` or `win-x64`. Omit it to build all four.

The GUI takes a few minutes: it also builds the four client tools it can install locally, which is
most of the ~370 MB of output.

---

## Running the GUI

Everything the app needs is inside its output directory. **Move the whole directory, not just the
executable** — the `tools/` folder beside it holds the client binaries.

### macOS

```bash
open "artifacts/gui/Release/osx-arm64/EntKube Installer.app"
```

Or double-click it in Finder. `release.sh gui` wraps the build in a proper `.app` bundle, so it gets
a Dock entry and a name rather than opening a Terminal window alongside it.

The app is **not code-signed or notarised**, so if you copy it from another machine — a download, an
AirDrop, a shared drive — macOS quarantines it and refuses to open it with *"cannot be opened because
the developer cannot be verified"*. Two ways past that:

- **Right-click the app → Open → Open.** Apple's own override; you only do it once.
- Or clear the quarantine flag: `xattr -dr com.apple.quarantine "EntKube Installer.app"`

An app you built yourself on the same machine is not quarantined and just opens.

### Linux

```bash
./artifacts/gui/Release/linux-x64/entkube-installer
```

It needs a graphical session — X11 or Wayland — so run it from the desktop machine, not over a plain
SSH connection. On a minimal install you may need the libraries Avalonia draws with:

```bash
# Debian / Ubuntu
sudo apt-get install -y libx11-6 libice6 libsm6 libfontconfig1

# Fedora / RHEL
sudo dnf install -y libX11 libICE libSM fontconfig
```

Missing `libfontconfig` is the usual one, and it fails with a native loader error rather than
anything that mentions fonts.

To add it to your application menu, edit the `Exec=` line of the generated
`entkube-installer.desktop` to the absolute path, then:

```bash
cp entkube-installer.desktop ~/.local/share/applications/
update-desktop-database ~/.local/share/applications
```

### Windows

```
artifacts\gui\Release\win-x64\entkube-installer.exe
```

Double-click it, or run it from a terminal. SmartScreen may warn that the publisher is unknown, since
the executable is unsigned — *More info → Run anyway*.

---

## Using the GUI

Four steps:

1. **Target** — a server over SSH (host, user, private key or password, optional sudo) or this
   machine, plus the install directory.
2. **Configure** — the same questions the console wizard asks. Seeded from the `.env` already on the
   target, so re-running against an installed server is safe.
3. **Install** — live log while it writes the files, pulls and starts.
4. **Client tools** — optionally put the CLI, MCP server, egress agent and Terraform provider on
   *your* machine. Nothing in this step touches the server.

If anything fails, the log pane on step 3 carries the command's own output — that is where the
reason is, rather than in the banner above it.

### When it says Compose v2 is missing

`docker-compose --version` printing a version is **not** the same check. Three different things
produce this message, and the installer distinguishes them:

| What it says | What is actually wrong |
| --- | --- |
| *"available … but not through sudo"* | Compose is installed for your user, in `~/.docker/cli-plugins`. `sudo` runs with root's `HOME`, so root cannot see it. Either install it system-wide (`sudo apt-get install docker-compose-plugin`), or add your user to the `docker` group and turn **use sudo** off on the connection page. |
| *"Only Compose v1 is available"* | A genuine v1. Install `docker-compose-plugin`, or a v2 standalone binary. |
| *"Could not find … "* plus what each probe returned | Neither form answered. The message shows exactly what `docker compose version` and `docker-compose version` each said, as the user the installer connects as. |

The check that matters is the one the installer runs, as the user it connects as:

```bash
ssh you@server 'docker compose version'
ssh you@server 'sudo docker compose version'    # if "use sudo" is on
```

If the first works and the second does not, it is the `HOME` problem in the first row.

### Host keys

The first time you connect to a server, its host key is shown with an `SHA256:…` fingerprint and you
are asked whether to trust it. Verify it against the server before accepting:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

This is not ceremony. The session carries your sudo password and writes the vault root key, so an
intercepted connection hands over both. Keys already in `~/.ssh/known_hosts` are trusted silently;
accepting a new one can add it there, so `ssh` recognises it too.

### sudo

Unless the login user is root or already in the `docker` group, tick **use sudo**. It is also needed
to create `/opt/entkube`. The sudo password is sent on the command's standard input, never on its
command line, so it does not appear in the server's process list.

The install directory is chowned to the login user after it is created — the configuration files go
up over SFTP, which does not run through sudo.

### What the GUI does not do

It does not upload a copy of the console installer and run it. That would mean a ~76 MB transfer per
install and two architectures' worth of binaries carried inside the GUI to cover a decision it cannot
make in advance. Instead it runs the same install logic locally against an SSH connection, sending
only the three generated files (about 8 KB).

---

## The console installer

`entkube-install` is a single self-contained binary. Copy it to the host, run it, answer a handful of
questions.

```bash
scripts/release.sh installer --rid linux-x64
scp artifacts/installer/Release/linux-x64/entkube-install server:/tmp/
ssh server 'chmod +x /tmp/entkube-install && sudo /tmp/entkube-install --directory /opt/entkube'
```

Pick the `--rid` for the **server**, not for your laptop — this binary runs there.

`chmod +x` because the execute bit does not always survive the trip: `scp` usually keeps it, a
download or an unzip usually does not, and the failure is a bare "permission denied" that says
nothing about why.

It writes `docker-compose.yml`, `Caddyfile` and `.env` into the install directory, pulls the images,
starts everything and waits for the app to answer.

---

## Why the console installer is a terminal wizard and not a GUI

The management plane is installed on a server, and a server is reached over SSH far more often than it
is sat in front of. A windowed installer would be unusable in the most common case and unavailable in
the second most common — a headless host with no display server at all. A terminal wizard works over
SSH, in a container, on a serial console, and on all three desktop platforms.

It ships as one self-contained executable for the same reason the agent and the CLI do: it runs on a
freshly provisioned host *before* EntKube exists, so it cannot assume a .NET runtime is there.
Installing one first would be the exact step the installer exists to remove.

Output: `artifacts/installer/Release/<rid>/entkube-install[.exe]` for `osx-arm64`, `osx-x64`,
`linux-x64`, `win-x64`.

The GUI exists for the other case — installing *from* a desktop *to* a server — where a window is
available and getting a binary onto the server first is the friction worth removing.

---

## What it asks

| Question | Default | Notes |
| --- | --- | --- |
| Public domain, or a local port | public domain | The public path runs Caddy on 80/443 with automatic Let's Encrypt. The local path publishes the app's own port with no TLS — evaluation only. |
| Domain and ACME email | — | Required for the public path. DNS must already resolve to this host before Let's Encrypt will issue. |
| Database | bundled PostgreSQL 17 | Or an external Postgres you supply a connection string for, or SQLite for evaluation. |
| Telemetry storage | local disk | Or the bundled MinIO, or an S3 bucket. See below. |
| Retention | 14 days | Sealed telemetry segments older than this are dropped. |
| Registry | `entit.azurecr.io`, anonymous | Public, so the common case needs nothing. |
| Seeded admin email | none | Granted Admin on every startup. The way back in if the last admin account is lost. |

Two things are **not** asked about, deliberately:

- **SMTP** is optional and has an in-app answer that takes priority: a provider configured under
  Tenants → Notification providers wins over the `Smtp__*` configuration keys, which remain as a
  fallback. Neither is needed to install, and with no host configured at all, email notifications are
  skipped with a warning rather than failing.
- **OIDC** has eight interdependent values and a provider-side registration step. That is a documented
  procedure ([sso.md](sso.md)), not an install question. Existing OIDC settings in a `.env` are
  preserved untouched across a re-run.

---

## Pointing it at an existing deployment

An installation that already exists — including one stood up by hand, and one in a directory like
`/home/ubuntu` rather than `/opt/entkube` — can be adopted. Set the install directory to wherever it
lives and connect; the installer works out what is there before offering you any answers.

### What it reads

A deployment created by a previous run of this installer records its own answers in `.env`
(`EXPOSE_MODE`, `DATABASE_MODE`, `TELEMETRY_STORAGE`), and those are simply reused.

A deployment created by hand has none of them. Rather than fall back to defaults it never agreed to,
the installer reads the shape from the deployment itself:

| Probe | What it settles |
| --- | --- |
| `docker compose ps -a` | Which services actually have containers — the truth about what is deployed. `-a` on purpose: a stopped Postgres is still this deployment's database. |
| `docker compose --profile '*' config` | The resolved configuration, so a service behind a profile (the bundled MinIO is one) still counts as defined, and `DatabaseProvider`, published ports and any `Telemetry__ObjectStorage__*` are visible. |
| The existing `Caddyfile` | The domain and the Let's Encrypt account address. On a deployment set up by hand these are usually typed straight into the site block and never reach `.env`, so without this the installer would ask for a value already sitting in a file beside it. |

A value that cannot be confidently identified is left blank and asked for — the installer will not
guess a hostname, because a wrong one orders a certificate for a name that is not yours. A wildcard,
a bare `:443`, `localhost` and an unresolved `{env.DOMAIN}` all count as "cannot tell".

From those it infers whether you are behind Caddy or publishing a port, whether the database is
bundled / external / SQLite, and whether telemetry goes to MinIO, S3 or local disk. What it concluded
is shown before anything is changed — check it.

If the Caddyfile refers to `{env.DOMAIN}` and `.env` does not define `DOMAIN`, the installer says so
— that deployment has no host name to serve, and filling the domain in fixes it.

**This matters more than it sounds.** Without it, a deployment running the bundled MinIO would fall
back to the "local disk" default, and the regenerated compose file would simply not contain MinIO —
taking a running service away with no mention of it. `InstallerAdoptionTests` covers that case
specifically.

### Your existing database is kept, not replaced

This is the part worth reading twice, because getting it wrong is silent and total.

The generated compose file used to write a Postgres service with a fixed image tag
(`postgres:17`), a fixed volume (`postgres-data`) and fixed credentials. Pointed at a deployment
whose database differed in any of those — a volume called `pgdata`, an image pinned to 15, a service
called `db` — it would have started a **brand new, empty Postgres on a brand new volume**, while the
real data sat in the old one. Nothing deleted; everything gone. The first symptom is an application
with no data in it.

So an existing database is now carried over exactly as it is:

| Kept | Why it must be |
| --- | --- |
| Service name (`db`, `postgres`, anything) | A second service under a different name would leave yours orphaned — and `up --remove-orphans` would then remove the container holding your live database. |
| Image tag | Postgres 17 refuses to start against a 15 data directory. |
| Data location — named volume **or** bind mount | This is the one that loses data silently. A bind-mounted host path is also *not* re-declared in the `volumes:` section, which would create an empty named volume that shadows it. |
| `POSTGRES_DB` / `POSTGRES_USER` | The connection string and the health check are built from them; the wrong ones mean the app cannot connect, or `depends_on` never goes healthy. |

You will see exactly what it found before anything is written:

```
  Database    existing — service "db", image postgres:16,
              data in host path /srv/pgdata, database "entkube_prod" as "entkube_app"
```

### When it refuses

The installer **stops without changing anything** rather than risk this:

- **The database's data location cannot be read** — no volume mounted at `/var/lib/postgresql/data`,
  or an anonymous mount. There is nothing to carry over, so proceeding would write the default and
  point the app somewhere else.
- **An answer would remove the database from the deployment** — switching an adopted Postgres to
  SQLite or to an external server. Its data would stay on disk with nothing mounting it.

In both cases nothing is written and the message says what it found. Take a backup and move the data
deliberately if you really mean to switch:

```bash
docker compose exec db pg_dump -U entkube_app entkube_prod > entkube.sql
```

### The existing network is kept under its own key

Compose stamps every network it creates with a `com.docker.compose.network` label holding the **key**
it had in the file — not its name. So a deployment whose file said

```yaml
networks:
  entkube:
    name: entkube
```

owns a network labelled `entkube`, and a file that declares the same network as

```yaml
networks:
  default:
    name: entkube
```

is refused against it:

```
network entkube was found but has incorrect label com.docker.compose.network
set to "entkube" (expected: "default")
```

The two are not interchangeable, so the installer keeps whichever key the deployment already uses,
and attaches every service to it explicitly — compose joins services to `default` on its own, but any
other key has to be named on each service or they land on a second network and stop resolving each
other by hostname. A network declared `external: true` stays external.

A fresh install still pins the default network to the name `entkube`, exactly as before.

### Services are not removed behind your back

When a change would drop a service, the installer starts **without** `--remove-orphans`, so
containers you are still using are left running rather than removed as a side effect. It says so,
and tells you how to clean up deliberately when you are ready.

### What changes, and what does not

| | |
| --- | --- |
| `docker-compose.yml`, `Caddyfile` | **Replaced** with generated ones. The previous versions are kept as timestamped `.bak` files. |
| `.env` | **Merged.** Settings the installer does not manage — an OIDC block, anything you added — are carried through verbatim. |
| `VAULT__ROOTKEY`, `POSTGRES_PASSWORD`, `MINIO_ROOT_*` | Reused exactly as found, never regenerated. |
| Named volumes | Untouched. Your data stays where it is. |
| An existing database service | Reproduced exactly — name, image, data location, credentials. See above. |

Hand edits to the compose file or Caddyfile will not survive. Move them into a
`docker-compose.override.yml`, which compose merges automatically and the installer never touches.

### Filling in what is missing

Once the current shape is detected, adding something absent is just changing that answer — turn on
the bundled MinIO, switch from SQLite to Postgres, put Caddy in front of a deployment that was
serving on a plain port. Before writing anything, the installer prints what the change does to the
service list:

```
  services (running now)      caddy, entkube, postgres
  will be added               minio
```

and if an answer would *remove* something, it says so as a warning rather than doing it quietly:

```
  ! These services will no longer be part of the deployment: minio
```

Containers dropped this way are stopped and left behind, and named volumes are kept — `docker compose
down` does not delete data, only `down -v` does.

Adopting is a one-way step: after the first run the deployment carries the markers, so later runs
read them directly and no longer need to infer anything.

---

## Re-running it

Re-running is the supported way to change a setting or move to a newer image. An existing `.env` is
read before anything is asked, and every answer is offered back as its default — press Enter through
what you do not want to change.

```bash
entkube-install --directory /opt/entkube                 # change something, interactively
entkube-install --directory /opt/entkube --image-tag a1b2c3d --yes
```

Two values are reused without being offered as a question at all, because there is no safe answer
other than "keep it":

- **`VAULT__ROOTKEY`** encrypts every secret in the vault. A new key does not fail loudly — the app
  starts normally and every stored credential decrypts to nothing.
- **`POSTGRES_PASSWORD`** is applied only when the Postgres volume is *first* initialised. Changing it
  later leaves the server on the old password and the app unable to connect, with an authentication
  error that points at neither.

`docker-compose.yml` and `Caddyfile` are replaced wholesale on a re-run, with the previous version
kept as a timestamped `.bak`. Keys in `.env` that the installer does not manage — an OIDC block, a
setting you added by hand — are carried through verbatim.

Put structural changes of your own in a `docker-compose.override.yml`. Compose merges it
automatically and the installer never touches it.

---

## Scripted installs

`--non-interactive` asks nothing. Every answer must come from a flag or an existing `.env`; anything
missing is an error rather than a guess, because guessing a domain name or a connection string is
worse than stopping.

```bash
entkube-install --non-interactive --directory /opt/entkube \
  --domain entkube.example.com \
  --acme-email ops@example.com \
  --seed-admin ops@example.com
```

`--dry-run` writes the files and starts nothing, which is the way to inspect what a set of flags
produces before committing to it.

Exit codes: `0` installed or rendered, `1` the install failed, `2` invoked wrongly or the host is not
ready, `3` cancelled at the confirmation.

Run `entkube-install --help` for the full flag list.

---

## Telemetry storage

EntKube's own logs, traces and RUM are indexed into segments that are sealed and moved to object
storage. The installer offers three answers, and **the production one is usually none of them**:
register an S3 StorageLink in the app afterwards and point telemetry at it, so its credentials live in
the vault rather than in a file on the host.

| Choice | What it is |
| --- | --- |
| Local disk | Under the app's data volume. Works immediately, single node, bounded by this host's disk. |
| Bundled MinIO | Self-hosted object storage on this same host, with the bucket created for you. Better than local disk, still one machine. |
| External S3 | An existing bucket. Credentials land in `.env` in clear text — a StorageLink keeps them in the vault instead. |

Switching later loses nothing: sealed segments already written stay where they are.

---

## Architecture

The published image `entit.azurecr.io/entkube` is built for **`linux/amd64` and `linux/arm64`**, so an
Intel/AMD server and an arm64 one (Graviton, Ampere, Hetzner ARM) both work.
[`.github/workflows/deploy.yml`](../.github/workflows/deploy.yml) builds each architecture on a native
runner, pushes them by digest, and combines them into one manifest list — then asserts both entries
are present before the deploy runs, because the failure it guards against is remote and late.

If a pull still reports

```
no matching manifest for linux/arm64/v8 in the manifest list entries
```

it means the manifest list has no entry for that architecture — not a registry or credentials fault.
On the official registry that means a tag published before this change; on your own, an image built
for one architecture. The installer detects this specific failure and says so. To build both
yourself:

```bash
scripts/release.sh web --push                      # both, by default
scripts/release.sh web --registry <yours> --push   # both, to your own registry
```

---

## What it checks before writing anything

| Check | Why it is checked rather than assumed |
| --- | --- |
| `docker` present and the daemon reachable | A stopped daemon otherwise surfaces as a permission error. |
| Docker **Compose v2**, in either form | Compose v2 ships both as a `docker compose` CLI plugin and as a standalone `docker-compose` binary; both are v2 and both work, and whichever is found is used for every command. Only v1 is refused — it differs in profile handling and `depends_on` conditions that the generated file relies on, so it would produce a subtly different deployment rather than an obviously broken one. |
| Install directory writable | Every later step writes there. |
| Ports free | A warning, not an error — the usual cause is this same deployment already running. A distribution's nginx on 80 otherwise surfaces minutes later as a failed ACME order. |
| `docker compose config` | Catches a bad interpolation in five seconds instead of after a several-hundred-megabyte pull. |

---

## After it finishes

1. Open the URL and **register the first account**. Registration is open until you turn it off, so do
   this before the host is reachable by anyone else.
2. Grant that account the Admin role, and set `SEED_ADMIN_EMAIL` in `.env` if you did not during the
   install.
3. Turn off open registration under Admin.

**Back up `.env` somewhere other than this host.** It holds `VAULT__ROOTKEY`, and without that exact
key a restored database is unreadable — there is no recovery path and no way to re-derive it.

```bash
cd /opt/entkube
docker compose logs -f entkube     # what it is doing
docker compose restart entkube     # after editing .env
docker compose down                # stop; volumes are kept
docker compose down -v             # stop and DELETE all data
```

---

## Client tools

The GUI's last step installs the client-side tools onto the machine running it:

| Tool | What it is |
| --- | --- |
| `entkube` | The CLI, for a terminal or a CI job. |
| `entkube-mcp` | The MCP server, for an MCP client such as Claude. |
| `entkube-agent` | The egress agent, for reaching an IP-allowlisted provider API. |
| `terraform-provider-entkube` | The Terraform provider. |

They are bundled next to the GUI at build time by `scripts/release.sh gui`, which builds them for the
GUI's own platform and puts them in a `tools/` folder beside the executable. **That folder has to
travel with the app**; without it the step still runs and reports which tools are missing and how to
build them.

They are bundled rather than embedded because four self-contained .NET binaries come to roughly
250 MB, and rather than downloaded because these binaries deliberately have no release host — see
[releasing.md](releasing.md).

The installer copies binaries and writes `agent.json.example`. It does **not** edit shell profiles or
merge itself into an MCP client's configuration: those files belong to you and to other
applications, and an installer that rewrites them is one that can break something it did not create.
Where configuration is needed, the snippet is produced for you to paste.

---

## Testing the SSH path

The SSH executor is covered by integration tests that need a real `sshd`. They no-op unless
`ENTKUBE_SSH_TEST_HOST` is set. A throwaway server:

```bash
cat > /tmp/sshd/Dockerfile <<'EOF'
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends openssh-server sudo iproute2 \
    && rm -rf /var/lib/apt/lists/*
RUN useradd -m -s /bin/bash ops && echo 'ops:opspass' | chpasswd \
    && echo 'ops ALL=(ALL) ALL' > /etc/sudoers.d/ops
RUN mkdir -p /run/sshd && ssh-keygen -A
CMD ["/usr/sbin/sshd", "-D", "-e"]
EOF

docker build -t entkube-sshtest /tmp/sshd
docker run -d --name entkube-sshtest -p 22022:22 entkube-sshtest

ENTKUBE_SSH_TEST_HOST=127.0.0.1 ENTKUBE_SSH_TEST_PORT=22022 \
ENTKUBE_SSH_TEST_USER=ops ENTKUBE_SSH_TEST_PASSWORD=opspass \
  dotnet test tests/EntKube.Web.Tests/EntKube.Web.Tests.csproj --filter InstallerSshIntegration
```

This is worth keeping. It is what caught three defects that reading the code did not: a sudo
pipeline that fed the password to `cd` and then blocked forever, an SFTP permission call that
rejected the mode it was handed, and a missing-`docker` check that named the wrong cause whenever
sudo was involved.

---

## Installing by hand instead

The installer is not required. `docker-compose.yml` and `.env.example` in the repository root are the
reference for a hand-rolled install, and the README covers it. The installer generates its own compose
file rather than copying that one — the choices it offers are structural (an external database has to
remove the postgres service *and* the health-gated `depends_on` that references it), and a compose
override file cannot reliably do that. A half-applied override would leave an idle postgres holding a
volume that looks like it holds the data and does not.

The consequence is that the two files are separate renderings of the same knowledge.
`InstallerRendererTests` pins the parts that must not drift.
