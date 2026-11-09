# Vintage Story Server Manager

A self-hosted Docker image that runs a [Vintage Story](https://www.vintagestory.at/) dedicated server **and** a web interface for managing it: server control, console, mods, backups and user accounts.

> **Status:** early development. The container, data layout and user accounts are in place. The server management features are being built (see [Roadmap](#roadmap)).

## How it works

One container holds:

- **The Vintage Story dedicated server**, installed in the image at `/opt/vintagestory`. The version is pinned at build time with `VS_VERSION`.
- **The manager**, an ASP.NET Core Blazor (Interactive Server) app on .NET 10. It serves the web UI and will run the game server as a supervised child process (start, stop, restart, console input and output), so it never needs access to the Docker socket.

Vintage Story 1.22+ runs on .NET 10, so a single `aspnet:10.0` runtime image covers both.

## Data volume

Everything that should outlive the container lives under `/data`. Mount a host folder there. To move the server, mount the same folder into another container.

```text
/data
├── vintagestory/   Vintage Story data path (--dataPath)
│   ├── Saves/            worlds
│   ├── Mods/             mod zips
│   ├── ModConfig/
│   ├── Logs/
│   └── serverconfig.json
├── manager/        manager.db (accounts, settings) and auth keys
└── backups/        backups made by the manager
```

`/data/vintagestory` uses the standard Vintage Story data-path layout. To migrate an existing server, copy its data folder (the one containing `Saves/`, `Mods/` and `serverconfig.json`) into `./data/vintagestory/`.

The container runs as the non-root `app` user (UID `1654`). A bind-mounted host folder must be writable by that UID:

```bash
mkdir -p data && sudo chown -R 1654:1654 data
```

## Running

```bash
docker compose up -d --build
```

| Port | Purpose |
|---|---|
| `80` → `8080` | Management web UI. Put a reverse proxy in front for HTTPS; forwarded headers are honoured. |
| `42420/tcp+udp` | Game server |

Health check: `GET /healthz`.

### Accounts

There is no public sign-up. Every page needs a login, and only admins can add users (under **Users**).

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
| `DataPaths__GameInstallDirectory` | `/opt/vintagestory` | Vintage Story server install |
| `Admin__Username` | `admin` | First admin account, created when there are no users |
| `Admin__Password` | *(generated)* | Password for the first admin |
| `Admin__ResetPassword` | `false` | Recovery: reset the named admin's password on start |
| `VS_VERSION` (build arg) | `1.22.7` | Server version baked into the image |
| `VS_CHANNEL` (build arg) | `stable` | `stable` or `unstable` |
| `VS_MD5` (build arg) | *(empty)* | Optional checksum from `https://api.vintagestory.at/stable.json` |

## Development

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
cd src/VintageStoryServerManager.Web && dotnet run
```

In Development, data goes to `./.data/` and the game install is expected at `./.vintagestory/` (both gitignored). To get a local server install:

```bash
mkdir -p .vintagestory && curl -L https://cdn.vintagestory.at/gamefiles/stable/vs_server_linux-x64_1.22.7.tar.gz | tar -xz -C .vintagestory
```

### Solution layout

| Project | Responsibility |
|---|---|
| `VintageStoryServerManager.Core` | Domain models, options, service interfaces. No infrastructure dependencies. |
| `VintageStoryServerManager.Infrastructure` | File system, server process supervision, mod scanning, ModDB client, backups. |
| `VintageStoryServerManager.Web` | Blazor UI, ASP.NET Core Identity accounts, EF Core (SQLite). |
| `VintageStoryServerManager.Tests` | xUnit tests. |

## Roadmap

1. ~~**Accounts:** first admin from the environment, no public registration, admin user management.~~
2. **Server control:** supervise `VintagestoryServer.dll`, with start, stop and restart, a live console, command input and status.
3. **Mods (read-only):** scan `Mods/`, read `modinfo.json`, show versions, dependencies and compatibility warnings.
4. **ModDB:** search, install and update mods with dependency resolution.
5. **Safety:** back up before changes, roll back, restore worlds.
6. **Later:** player management, server config editor, in-place game version upgrades.
