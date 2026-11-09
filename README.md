# Vintage Story Server Manager

A self-hosted Docker image that runs a [Vintage Story](https://www.vintagestory.at/) dedicated server **and** a web interface for managing it.

- **Dashboard and server control:** status, uptime, version, players online, start, stop and restart, with automatic restart after a crash.
- **Live console:** the server's output in your browser, and a command box with history.
- **Mods:**
  - Every installed mod is listed with its `modinfo.json` details, along with health checks for duplicates, missing or outdated dependencies, game-version mismatches and unreadable files.
  - You can enable, disable, delete or upload mods.
  - You can search the official mod database, install mods with their dependencies resolved, and check for and apply updates.
- **Backups:**
  - Full, world, mods or config backups. World backups are safe while the server runs.
  - Automatic backups before every mod change, config edit, restore and game version switch.
  - One-click restore and download.
- **Players:** online players, kick, timed bans and unban, the whitelist and in-game roles.
- **Server config editor** for `serverconfig.json`, with validation and an automatic backup.
- **Log viewer** with crash logs first, follow mode and download.
- **Game versions:** download other server versions from the official API (checksum verified) and switch between them without rebuilding the image.
- **Accounts:** no public sign-up. Admins create users as Viewer, Operator or Admin.

## How it works

One container holds:

- **The Vintage Story dedicated server**, bundled at `/opt/vintagestory`. The version is pinned at build time with `VS_VERSION`.
- **The manager**, an ASP.NET Core Blazor (Interactive Server) app on .NET 10 with a MudBlazor UI. It runs the game server as a child process with redirected console streams, so it can start and stop it, read its output and send commands without the Docker socket. On `docker stop` it sends `/stop` and waits for the world to save.

Vintage Story 1.22+ runs on .NET 10, so a single `aspnet:10.0` runtime image covers both.

## Data volume

Everything that should outlive the container lives under `/data`. Mount a host folder there. To move the server, mount the same folder into another container.

```text
/data
├── vintagestory/   Vintage Story data path (--dataPath)
│   ├── Saves/            worlds
│   ├── Mods/             enabled mods
│   ├── ModConfig/
│   ├── Playerdata/
│   ├── Logs/
│   └── serverconfig.json
├── manager/        manager.db (accounts), auth keys, disabled-mods/, game.json (active version), tmp/
├── game/           server versions downloaded from the Game versions page
└── backups/        backup zips
```

`/data/vintagestory` uses the standard Vintage Story data-path layout. To migrate an existing server, copy its data folder (the one containing `Saves/`, `Mods/` and `serverconfig.json`) into `./data/vintagestory/`.

The container runs as the non-root `app` user (UID `1654`). A bind-mounted host folder must be writable by that UID:

```bash
mkdir -p data && sudo chown -R 1654:1654 data
```

## Running

```bash
docker compose up -d
```

`compose.yaml` builds the image locally. To use the published image instead, remove the `build:` section. CI publishes `ghcr.io/kayrah87/vintagestoryservermanager` with these tags:

- `latest` and `vs-<game version>`, from `main`;
- `<version>`, from `v*` tags;
- `sha-<commit>`.

| Port | Purpose |
|---|---|
| `80` → `8080` | Management web UI. Put a reverse proxy in front for HTTPS; forwarded headers are honoured. |
| `42420/tcp+udp` | Game server |

Health check: `GET /healthz`.

Keep `stop_grace_period: 2m` (as in `compose.yaml`) so the game server has time to save when the container stops.

### Accounts and roles

There is no public sign-up, and every page needs a login.

| Role | Can |
|---|---|
| Viewer (no role) | See the dashboard and mods |
| Operator | Start, stop and restart, use the console, manage players, read logs |
| Admin | Everything: install and remove mods, backups and restores, server config, game versions, users |

On first start, with no users in the database, the manager creates an admin account:

- With `Admin__Username` and `Admin__Password` set, it uses those.
- Without them, it creates `admin` with a random password and prints it once in the container log:

  ```bash
  docker compose logs vintagestory | grep "generated password"
  ```

These settings only apply to the very first start. Change your password afterwards under your account page.

**Locked out?** Start the container once with `Admin__ResetPassword=true`, `Admin__Username` and `Admin__Password`. That account gets the new password, is unlocked and made an admin. Remove the setting afterwards.

Five wrong passwords in a row lock an account for 15 minutes. An admin can unlock it straight away by setting a new password.

### Configuration

| Variable | Default | Description |
|---|---|---|
| `DataPaths__Root` | `/data` | Root of the data volume |
| `DataPaths__GameInstallDirectory` | `/opt/vintagestory` | Bundled Vintage Story server install |
| `Server__AutoStart` | `true` | Start the game server with the container |
| `Server__RestartOnCrash` | `true` | Restart after an unexpected exit (at most 3 times in 10 minutes) |
| `Server__StopTimeoutSeconds` | `90` | How long to wait for `/stop` before killing the server |
| `Server__ConsoleHistoryLines` | `2000` | Console lines kept in memory |
| `Backups__KeepAutomatic` | `20` | Automatic backups to keep (manual backups are never pruned) |
| `Admin__Username` | `admin` | First admin account, created when there are no users |
| `Admin__Password` | *(generated)* | Password for the first admin |
| `Admin__ResetPassword` | `false` | Recovery: reset the named admin's password on start |
| `ModDb__BaseUrl` | `https://mods.vintagestory.at/` | Mod database |
| `ModDb__GameApiUrl` | `https://api.vintagestory.at/` | Game version list |
| `VS_VERSION` (build arg) | `1.22.7` | Server version bundled in the image |
| `VS_CHANNEL` (build arg) | `stable` | `stable` or `unstable` |
| `VS_MD5` (build arg) | *(empty)* | Optional checksum from `https://api.vintagestory.at/stable.json` |

## Good to know

- **Mod changes** take effect when the server restarts; the Mods page offers a restart button. Disabled mods are moved to `/data/manager/disabled-mods`, outside every folder the game loads from.
- **Mod health checks** treat dependency versions as minimums, as the game does.
- **Installing from the mod database:**
  - It picks the newest release tagged for the running game version. Failing that, it takes one for the same minor version and warns you.
  - Missing or outdated dependencies are installed too.
  - You see the plan before anything changes.
- **Server config** can only be saved while the server is stopped, because the game rewrites `serverconfig.json` when it shuts down.
- **Player actions** go through server console commands, so the server must be running.
- **Restores** need the server stopped. They replace only the parts in the backup (for example a Mods backup leaves the world alone), and back up what they replace first.
- **Switching game versions** needs the server stopped and makes a full backup first. Worlds opened in a newer version may not load in an older one.

## Development

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
cd src/VintageStoryServerManager.Web && dotnet run
```

In Development, data goes to `./.data/`, the game install is expected at `./.vintagestory/` (both gitignored), and the server doesn't start automatically. To get a local server install:

```bash
mkdir -p .vintagestory && curl -L https://cdn.vintagestory.at/gamefiles/stable/vs_server_linux-x64_1.22.7.tar.gz | tar -xz -C .vintagestory
echo -n 1.22.7 > .vintagestory/.vsversion
```

### Solution layout

| Project | Responsibility |
|---|---|
| `VintageStoryServerManager.Core` | Domain models, options, interfaces and pure logic (version comparison, `modinfo.json` parsing, mod health checks, release selection, console output parsing). |
| `VintageStoryServerManager.Infrastructure` | Server process supervision, game installs, mods, backups, the mod database client and installer, server config, logs and players. |
| `VintageStoryServerManager.Web` | Blazor UI (MudBlazor), ASP.NET Core Identity accounts, EF Core (SQLite), download endpoints. |
| `VintageStoryServerManager.Tests` | xUnit tests. |

## Roadmap

1. ~~**Accounts:** first admin from the environment, no public registration, user management.~~
2. ~~**Server control:** supervised server process, start, stop and restart, live console, status.~~
3. ~~**Mods (read-only):** scan `Mods/`, read `modinfo.json`, health checks.~~
4. ~~**ModDB:** search, install and update with dependency resolution.~~
5. ~~**Safety:** automatic backups before changes, rollback, world restore.~~
6. ~~**More:** player management, server config editor, log viewer, in-place game version switching.~~

Ideas for later: scheduled backups, managing several servers from one manager, notifications (Discord or webhooks) on crashes and player joins.
