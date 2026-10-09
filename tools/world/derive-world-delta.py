#!/usr/bin/env python3
"""Derive world/patches from an Offline DAoC release's pristine world (currently 0.35).

Developer-only and run once per world update; the generated patches are committed, so users never
need the release. The build applies them on top of the pinned upstream OpenDAoC-Database to recreate
that release's Classic + SI world exactly (minus player progress).

  derive-world-delta.py <pristine opendaoc.sqlite3.db> <upstream-built world.sqlite> <out dir>

Per world table (player-progress tables excluded):
  * rows only in upstream     -> DELETE   (e.g. 37k later "Atlas" spawns 0.33 archived)
  * rows only in the release/changed -> INSERT OR REPLACE with the release's values
  * tables only in the release       -> CREATE TABLE (its DDL) + all rows (e.g. offline_level50_loadouts)
LastTimeRowUpdated is ignored when comparing.
"""
import pathlib
import sqlite3
import sys

WORLD = "Offline DAoC 0.35"  # the release the pristine DB comes from; named in each patch header

src_path, upstream_path, out = sys.argv[1], sys.argv[2], pathlib.Path(sys.argv[3])

# Player progress and runtime state: never part of the world (the release's progress-policy.json;
# unchanged from 0.33 to 0.35).
EXCLUDED = {t.lower() for t in """
Account AccountXCrafting AccountXCustomParam AccountXMoney CharacterXDataQuest CharacterXMasterLevel
CharacterXOneTimeDrop DBHouseCharsXPerms DBHousePermissions DBIndoorItem DBOutdoorItem DOLCharacters
DOLCharactersBackup DOLCharactersBackupXCustomParam DOLCharactersXCustomParam FactionAggroLevel Guild
GuildAlliance GuildRank HouseConsignmentMerchant Inventory ItemUnique PlayerBoats PlayerXEffect Quest Task
TimeXLevel bot_profiles bot_settings househookpointitem offline_auction_escrow offline_auction_ledger
offline_auction_listings offline_world_bots realm_exchange_sales
Appeal AuditEntry Ban BugReport KeepCaptureLog News PlayerInfo ServerInfo SinglePermission Voting
offline_bot_commands offline_local_options offline_runtime_status serverstats
offline_classic165_removed_mobs offline_classic165_restored_mobs
""".split()}
IGNORED_COLUMNS = {"lasttimerowupdated"}

o = sqlite3.connect(f"file:{src_path}?mode=ro", uri=True)
u = sqlite3.connect(f"file:{upstream_path}?mode=ro", uri=True)


def tables(db):
    return {r[0].lower(): r[0] for r in db.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")}


def columns(db, table):
    return [(r[1], r[5]) for r in db.execute(f'PRAGMA table_info("{table}")')]  # (name, pk position)


def q(v):
    if v is None:
        return "NULL"
    if isinstance(v, (int, float)):
        return repr(v)
    if isinstance(v, bytes):
        return "X'" + v.hex() + "'"
    return "'" + str(v).replace("'", "''") + "'"


def insert(table, cols, row):
    return f'INSERT OR REPLACE INTO "{table}" ({",".join(chr(34) + c + chr(34) for c in cols)}) VALUES ({",".join(q(v) for v in row)});\n'


ot, ut = tables(o), tables(u)
out.mkdir(parents=True, exist_ok=True)
summary = []

for key in sorted(ot):
    if key in EXCLUDED:
        continue
    table = ot[key]
    ocols = columns(o, table)
    lines = []

    if key not in ut:
        ddl = o.execute("SELECT sql FROM sqlite_master WHERE type='table' AND name=?", (table,)).fetchone()[0]
        lines.append(ddl.replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS", 1) + ";\n")
        for idx in o.execute("SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=? AND sql IS NOT NULL", (table,)):
            lines.append(idx[0].replace("CREATE INDEX", "CREATE INDEX IF NOT EXISTS", 1).replace("CREATE UNIQUE INDEX", "CREATE UNIQUE INDEX IF NOT EXISTS", 1) + ";\n")
        names = [c for c, _ in ocols]
        rows = o.execute(f'SELECT {",".join(chr(34) + c + chr(34) for c in names)} FROM "{table}"').fetchall()
        lines += [insert(table, names, r) for r in rows]
        summary.append((table, "new table", len(rows), 0, 0))
    else:
        utable = ut[key]
        ucols = {c.lower() for c, _ in columns(u, utable)}
        common = [c for c, _ in ocols if c.lower() in ucols]
        compare = [c for c in common if c.lower() not in IGNORED_COLUMNS]
        pk = [c for c, p in sorted(ocols, key=lambda x: x[1]) if p > 0 and c in common]
        sel = ",".join(chr(34) + c + chr(34) for c in common)
        ci = [common.index(c) for c in compare]
        orows = o.execute(f'SELECT {sel} FROM "{table}"').fetchall()
        urows = u.execute(f'SELECT {sel} FROM "{utable}"').fetchall()

        def norm(row):
            # SQLite text keys are case-insensitive here (COLLATE NOCASE ids); compare loosely.
            return tuple(v.lower() if isinstance(v, str) else v for v in (row[i] for i in ci))

        if pk:
            pi = [common.index(c) for c in pk]
            okey = lambda r: tuple(r[i].lower() if isinstance(r[i], str) else r[i] for i in pi)
            omap = {okey(r): r for r in orows}
            umap = {okey(r): r for r in urows}
            deleted = [k for k in umap if k not in omap]
            changed = [r for k, r in omap.items() if k not in umap or norm(umap[k]) != norm(r)]
            if deleted:
                for i in range(0, len(deleted), 400):
                    chunk = deleted[i:i + 400]
                    if len(pk) == 1:
                        lines.append(f'DELETE FROM "{utable}" WHERE "{pk[0]}" IN ({",".join(q(k[0]) for k in chunk)});\n')
                    else:
                        for k in chunk:
                            cond = " AND ".join(f'"{c}" = {q(v)}' for c, v in zip(pk, k))
                            lines.append(f'DELETE FROM "{utable}" WHERE {cond};\n')
            lines += [insert(utable, common, r) for r in changed]
            if deleted or changed:
                summary.append((table, "rows", len(changed), len(deleted), len(orows)))
        else:
            if sorted(map(norm, orows), key=repr) != sorted(map(norm, urows), key=repr):
                lines.append(f'DELETE FROM "{utable}";\n')
                lines += [insert(utable, common, r) for r in orows]
                summary.append((table, "replaced (no primary key)", len(orows), len(urows), len(orows)))

    if lines:
        (out / f"200-{key}.sql").write_text(
            f"-- {WORLD} world: {table}. Generated by tools/world/derive-world-delta.py; do not edit by hand.\n"
            + "".join(lines))

for t, kind, a, b, total in summary:
    print(f"{t:40} {kind:28} +/~{a:7}  -{b:7}")
print(f"{len(summary)} tables differ")
