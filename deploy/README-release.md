# DAoC Server (portable Linux build)

A self-contained DAoC server with autonomous and companion bots. It needs no .NET install and
no game client. Unpack it anywhere: a normal Linux system, or a distrobox if your system is
read-only (like SteamOS).

Requirements: x86-64 Linux with glibc 2.34 or newer (Ubuntu 22.04+, Debian 12+, Fedora 35+, SteamOS). Needs `libicu` (installed on almost every desktop distro and on SteamOS).

## 1. Install navmeshes (once)

Navmeshes let NPCs and bots walk around obstacles. The world itself is built in: a fresh
Classic + Shrouded Isles world (the Offline DAoC 0.35 world) is created on first start.

Download `daoc-navmeshes-classic-si-2.tar.xz` (~360 MB) from the release page, put it next to the
server folder, and run:

```bash
./navmesh-install.sh
```

The navmeshes are verified file by file. Alternatives: `--from-archive <file>`, or
`--from-dir <folder>` to reuse another server's `zone*.nav`.

## 2. Run and manage it

```bash
./daoc-server.sh start
```

One command, one terminal. It starts the server in the background and opens the admin screen,
which has these tabs:
- **Server:** status, live log, Stop button.
- **Bots:** list, create real roster bots, delete.
- **Population:** on/off, max bots in the world.
- **Accounts:** create accounts, set player/GM/admin.

**Ctrl+Q** closes the screen and asks whether to **stop the server** (it saves first) or **leave it
running**. If you leave it running, reopen the screen any time with `./daoc-admin`. Closing the
terminal window does not stop the server.

The same actions also work as single commands:

```bash
./daoc-admin help                                      # every command
./daoc-admin bots create --realm all --count 10        # also --level 50, --class Druid
./daoc-admin bots list
./daoc-admin population max 30                         # most bots in the world at once (0 = all)
./daoc-admin accounts set-role <account> admin         # or gm / player
./daoc-admin options set bot_use_town_teleporters off  # announcements, teleporters, sieges: ./daoc-admin options
./daoc-admin goals set 20-49 30 30 20 20               # bot goals: solo, group, RvR, battlegrounds %
./daoc-admin rvr                                       # battlegrounds, keeps, relics and raids
./daoc-admin server stop                               # or: ./daoc-server.sh stop
./daoc-server.sh status
```

`./daoc-server.sh start --console` gives the raw server console in the foreground instead. Type
the same commands there, and use `exit` or Ctrl+C to save and stop. It is used automatically when
there is no terminal (an init system, for example).

Notes:
- Roles are stored in the database and in `config/admins.json`. You may also list account names
  there while the server is stopped; they apply at start or on first login.
- New bots log in within seconds (at most `bot_logins_per_second`, default 8, per second).
- `population off` logs every bot out of the world. Their characters are kept.
- Logs: `logs/console.out` (this run) and `logs/server.log`.
- The admin tools only work for the user running the server. They use `run/admin.sock` and open
  no network port. For remote admin, ssh in.

## 3. Connect

The client is installed separately. Point it at this machine on port 10300. For players on
other machines, set `<RegionIP>` in `config/serverconfig.xml` to this machine's LAN address
(or your public address when port-forwarding TCP 10300 and UDP 10400).

Accounts are created on first login, or with `./daoc-admin accounts create <name> <password>`.

## Upgrading

Replace `bin/`, `defaults/` and the scripts with the new release. `config/`, `data/` and
`navmesh/` are yours and are kept.
