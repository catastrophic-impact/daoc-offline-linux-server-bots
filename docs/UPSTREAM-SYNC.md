# Taking changes from upstream

Layout rule: **each thing we own or want to replace wholesale has its own folder.** Upstream merges
then touch only the folders they map to.

| Folder | What | Upstream? |
|---|---|---|
| `src/Bots/` | All bot code and bot data (`GameBot`, `BotBrain`, `autonomous/`, companions, policies). Compiled **into the GameServer assembly** (`<Compile Include="..\Bots\**\*.cs">` in `GameServer.csproj`), namespaces unchanged. | Offline DAoC `source/server/GameServer/bots/` |
| `src/Admin.Host/` | Server side of the admin TUI: `AdminService`, the Unix socket host, console commands, bot roster and options operations. Own project; depends on GameServer, not the reverse. | none, ours only |
| `src/Admin.Protocol/`, `src/DaocAdmin/` | Wire protocol and the TUI/CLI. | none, ours only |
| `src/WorldBuilder/` | World DB builder. | none, ours only |
| `src/GameServer`, `CoreServer`, `CoreBase`, `CoreDatabase`, `Pathing`, `sharedModules`, `Tests` | Server core. Small local changes on top of upstream (listed in `UPSTREAM.md`). | Offline DAoC `source/server/<same>` |

No client code, launcher or release tooling from upstream is imported. Ever.

## The two seams between us and upstream core

- **Bots to core:** bots are NPCs pretending to be players, so core files special-case them.
  `tools/upstream/bot-touchpoints.sh` lists every non-Bots file that mentions a bot type. After a
  sync, review the ones the sync changed.
- **Admin to core:** exactly two, both explicit. `CoreServer/Actions/ConsoleStart.cs` calls
  `AdminHost.Register()` before the server is created, and `LoginRequestHandler.NewAccountPrivLevelProvider`
  is a hook that `AdminHost.Register()` fills in. Nothing else in GameServer knows the admin module exists.

## Pulling from Offline DAoC (the quick way, since 0.37)

Each release now ships a small game patch (`OfflineDAoC-v<ver>-game-patch.zip`, ~40 MB, attached to the
`v<ver>b` release) with the world-DB delta and the changed navmesh files. We use it for data, and git tags
for code. In the build box:

```bash
tools/upstream/sync.sh offlinedaoc report v0.38     # code: what changed, with our paths
git switch -c sync-0.38
tools/upstream/sync.sh offlinedaoc apply  v0.38     # code; needs a clean tree
tools/upstream/update-data.sh 0.38                  # world patch 2NN-offline-0.38.sql + navmesh, both verified
tools/build-release.sh                              # then run the tests and a startup (error counts: docs/history)
```

`apply` patches cleanly where it can. For files we edited, it falls back to a 3-way merge (base = upstream at the
synced sha) and lists them for review; real conflicts are left as `<<<<<<<` markers. `update-data.sh` fails, and
writes nothing, if the release's idea of the old world differs from ours (a skipped release, or a hand-edited row).

Finish by updating `UPSTREAM.md`, `offlinedaoc_SYNCED` in `tools/upstream/upstreams.conf` and
`AdminService.OfflineDaocVersion` (the version the TUI shows), all in the same commit. Upstream tags can share a
commit (v0.36 and v0.37 each were one), so record the sha.

`apply` takes `--bots` (only `src/Bots`) or `--core` (only the rest). Bot logic changes most upstream and we edit it
least, so a bots-only sync is the safe case.

Conflicts in `src/Bots` mean we edited upstream bot code. Prefer moving our change out into a new
file in `src/Bots/` (a policy class, a partial) so the upstream file can be replaced as-is.

## OpenDAoC (framework only)

Not synced yet and not required for this iteration. Offline DAoC forked OpenDAoC-Core with no
recorded fork point, so there is no `SYNCED` sha and `sync.sh opendaoc` only prints the folder map.
To start: pick an OpenDAoC commit, diff the mapped folders against `src/` by hand, port individual
fixes, and record each in `UPSTREAM.md` as `upstream <sha> -> our commit <sha>: why`. When a
baseline is chosen, set `opendaoc_SYNCED` and `report`/`check`/`apply` will work as for Offline
DAoC (adjust `opendaoc_MAP` if its layout differs).
