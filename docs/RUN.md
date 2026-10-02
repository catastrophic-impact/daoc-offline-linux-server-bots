# Running the server

A server is one portable folder (from `tools/build-release.sh`, or a release tarball). Its scripts
work on the folder they are in, so you can run them from anywhere, on the host or inside a
distrobox. The folder's own README (`deploy/README-release.md`) is the short player-facing
version of this page.

## First time

```bash
tar -xzf daoc-server-<version>-linux-x64.tar.gz      # anywhere; creates daoc-server/
cd daoc-server
./navmesh-install.sh                                 # with daoc-navmeshes-*.tar.xz next to the folder
./daoc-server.sh start
```

On first start the folder creates:
- `data/opendaoc.sqlite3.db`, a fresh Classic + SI world copied from `defaults/world.sqlite`;
- `config/`, from `defaults/`.

`navmesh-install.sh` also accepts `--from-archive <file>` and `--from-dir <folder>`.

## Folder layout

| Path | What | Kept on upgrade |
|---|---|---|
| `bin/` | Server program, `daoc-admin`, bundled .NET, `lib/Detour.so` | replaced |
| `defaults/` | `world.sqlite` and config templates | replaced |
| `config/` | `serverconfig.xml`, `logconfig.xml`, `invalidnames.txt`, `admins.json` | **yours** |
| `data/` | `opendaoc.sqlite3.db`: the world, accounts, characters, bots, economy | **yours** |
| `navmesh/` | `zone*.nav` plus `MANIFEST.sha256` | **yours** |
| `logs/` | `server.log`, `error.log`, `warn.log`, `console.out` (this run), `console.prev.out` | |
| `run/` | `server.pid`, `admin.sock` | |
| `daoc-server.sh`, `daoc-admin`, `navmesh-install.sh`, `release.conf`, `README.md`, `VERSION` | Scripts and info | replaced |

## Start and stop

```bash
./daoc-server.sh start             # server in the background + admin screen in this terminal
./daoc-server.sh start --console   # raw server console in the foreground (also used when there's no terminal)
./daoc-server.sh stop              # saves first
./daoc-server.sh status
./daoc-admin                       # reopen the admin screen
```

- In the admin screen, **Ctrl+Q** asks whether to stop the server or leave it running.
- Closing the terminal doesn't stop a background server.
- Stopping always saves, whether by the Stop button, `daoc-admin server stop`, `exit` in the
  console, Ctrl+C or SIGTERM.
- Only one server can use the game port (10300) at a time; `start` refuses if it's taken.

## Admin

The **admin screen** has four tabs:
- **Server:** status, live log, Stop.
- **Bots:** list, create, delete.
- **Population:** on/off, max in world.
- **Accounts:** create, set player/GM/admin.

The **CLI** (`./daoc-admin help`) has the same commands, and they also work typed into the
server console:

```
status | server stop
bots list [--realm alb|mid|hib] [--online]
bots create [--realm alb|mid|hib|all] [--count N] [--level 1|50] [--class NAME]
bots delete <name|id> | bots delete-all --yes
population | population on | off | max <N>
accounts list | show <name> | create <name> <password> | set-role <name> player|gm|admin
```

How it works:
- The server listens on `run/admin.sock` (mode 0600), so there's no network port. Only the OS
  user running the server can administer it; for remote admin, ssh in.
- `config/admins.json` lists GM and admin account names. It's rewritten by the admin tools and
  applied at start and on first login. You can edit it while the server is stopped.
- Code: `src/GameServer/admin/` (server side), `src/DaocAdmin` (client), `src/Admin.Protocol`
  (shared).

## Connecting a client

The client is a separate install. Point it at this machine on port 10300. With the Offline DAoC
client under Wine:

```bash
wine connect.exe game.dll 127.0.0.1 <account> <password>
```

Accounts are created on first login, or with `./daoc-admin accounts create`.

For other machines on your network, set `<RegionIP>` in `config/serverconfig.xml` to this
machine's LAN address and restart. For players outside your network, use your public address and
forward **TCP 10300** and **UDP 10400** on your router.

## Upgrading

1. Stop the server.
2. Back up `data/`.
3. Unpack the new release somewhere.
4. Copy its `bin/`, `defaults/` and scripts over the old folder.
5. Start.

`config/`, `data/` and `navmesh/` stay.

For code-only updates during development, `tools/dev-deploy.sh` does a quick version of this (see
[BOTS-DEV.md](BOTS-DEV.md)).

## Backups

Copy `data/opendaoc.sqlite3.db` while the server is stopped. While it's running, use
`sqlite3 data/opendaoc.sqlite3.db ".backup backup.db"`, which is safe with WAL. Keep backups out
of git.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `port 10300 is already in use` | Another server is running (for example the `project/` build). Stop it, or change `<Port>` in `config/serverconfig.xml`. |
| `no navmeshes yet` | Run `./navmesh-install.sh`. |
| `the ICU library (libicu) is missing` | Install libicu, or run the server inside a distrobox. |
| The client can't connect from another machine | Set `<RegionIP>` to this machine's LAN or public address; check the firewall and router (TCP 10300, UDP 10400). |
| `The server did not start` | Read the lines printed after it, `logs/console.out` and `logs/error.log`. |
| Bots don't appear | Check that the population is on (`./daoc-admin population`) and that bots exist (`bots list`). New bots log in within seconds. |
