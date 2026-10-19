"""Online SQLite backup with seven daily snapshots. Run as the service owner."""
from contextlib import closing
import datetime
import os
import pathlib
import sqlite3

os.umask(0o077)
root = pathlib.Path(__file__).resolve().parents[1]
folder = root / 'backups'
folder.mkdir(mode=0o700, exist_ok=True)
today = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d')
part = root / 'tmp' / 'usage-backup.sqlite'
part.parent.mkdir(mode=0o700, exist_ok=True)
for suffix in ['', '-wal', '-shm']:
    pathlib.Path(str(part) + suffix).unlink(missing_ok=True)
with closing(sqlite3.connect(f'file:{root}/data/usage.sqlite?mode=ro', uri=True)) as source:
    with closing(sqlite3.connect(part)) as target:
        source.backup(target)
        # Produce a standalone file, not a database dependent on temporary WAL sidecars.
        target.execute('PRAGMA journal_mode=DELETE')
        if target.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
            raise RuntimeError('Backup integrity check failed')
part.replace(folder / f'usage-{today}.sqlite')
for old in sorted(folder.glob('usage-????-??-??.sqlite'), reverse=True)[7:]:
    old.unlink()
print('Private SQLite backup complete, integrity verified.')
