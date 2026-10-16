#!/usr/bin/env python3
"""Fail-closed storage checks and verified backups for the existing single-host CD."""
import datetime
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tarfile

ROOT = Path('/opt/opengym')
STATE = Path('/var/lib/opengym-deploy')
VOLUME = 'opengym_nutrition-data'
RETAIN_BACKUPS = 14


def docker(*args):
    return json.loads(subprocess.check_output(['docker', *args], text=True))


def database_path():
    volume = docker('volume', 'inspect', VOLUME)[0]
    path = Path(volume['Mountpoint']) / 'nutrition.db'
    if not path.is_file() or path.is_symlink():
        raise RuntimeError('Expected persistent Nutrition database is missing; explicit recovery required')
    return path


def connect(path):
    return sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True, timeout=15)


def inventory(path, api_data):
    users = json.loads((api_data / 'db.json').read_text())['users']
    user_ids = sorted(u['id'] for u in users)
    if not user_ids or not (api_data / 'secret').read_text().strip():
        raise RuntimeError('Expected API accounts/signing key are missing')
    with closing(connect(path)) as db:
        if db.execute('PRAGMA integrity_check').fetchall() != [('ok',)]:
            raise RuntimeError('Nutrition integrity_check failed')
        migrations = [r[0] for r in db.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId')]
        if not migrations:
            raise RuntimeError('Uninitialized Nutrition schema')
        rows = {table: sorted([list(r) for r in db.execute(f'SELECT UserId, Id FROM {table}')])
                for table in ('Products', 'DiaryEntries')}
        owners = {u for entries in rows.values() for u, _ in entries}
        if not owners.issubset(user_ids):
            raise RuntimeError('Nutrition has owners absent from API; investigate identity before deployment')
    return dict(users=user_ids, migrations=migrations, **rows)


def check_compose(file):
    config = docker('compose', '--project-name', 'opengym', '--project-directory', str(ROOT),
                    '--env-file', str(ROOT / '.env'), '-f', str(file), 'config', '--format', 'json')
    services = config['services']
    for service, source, kind in [('api', str(ROOT / 'data'), 'bind'), ('nutrition', 'nutrition-data', 'volume')]:
        mounts = [v for v in services[service].get('volumes', []) if v['target'] == '/data']
        if len(mounts) != 1 or mounts[0]['type'] != kind or mounts[0]['source'] != source or mounts[0].get('read_only'):
            raise RuntimeError('Compose storage contract mismatch: ' + service)
        if kind == 'bind' and mounts[0].get('bind', {}).get('create_host_path', True):
            raise RuntimeError('API bind must not auto-create an empty data directory')
    volume = config['volumes']['nutrition-data']
    if volume.get('name') != VOLUME or volume.get('external') is not True:
        raise RuntimeError('Nutrition requires the stable external volume')
    env = services['nutrition']['environment']
    if env.get('Nutrition__DatabasePath') != '/data/nutrition.db' or str(env.get('Nutrition__RequireExistingDatabase')).lower() != 'true':
        raise RuntimeError('Nutrition must refuse a missing database')
    if str(services['api']['environment'].get('REQUIRE_EXISTING_DATA')) != '1':
        raise RuntimeError('API must refuse an empty store')


def check_running():
    for service, kind, source in [('api', 'bind', str(ROOT / 'data')), ('nutrition', 'volume', VOLUME)]:
        container = docker('inspect', f'opengym-{service}-1')[0]
        mounts = [v for v in container['Mounts'] if v['Destination'] == '/data']
        if len(mounts) != 1 or mounts[0]['Type'] != kind or not mounts[0]['RW']:
            raise RuntimeError('Running storage mount is missing/wrong: ' + service)
        actual = mounts[0].get('Name') if kind == 'volume' else mounts[0]['Source']
        if actual != source:
            raise RuntimeError('Running storage source changed: ' + service)
    return inventory(database_path(), ROOT / 'data')


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def verify_backup(directory):
    checksums = json.loads((directory / 'checksums.json').read_text())
    if not checksums:
        raise RuntimeError('Empty backup manifest')
    for name, expected in checksums.items():
        path = directory / name
        if path.parent != directory or path.is_symlink() or digest(path) != expected:
            raise RuntimeError('Backup checksum mismatch')
    with closing(connect(directory / 'nutrition.db')) as db:
        if db.execute('PRAGMA integrity_check').fetchall() != [('ok',)]:
            raise RuntimeError('Backup database is corrupt')
    with tarfile.open(directory / 'api-data.tar.gz') as archive:
        for member in archive:
            if member.isfile():
                f = archive.extractfile(member)
                while f.read(1024 * 1024):
                    pass
        accounts = json.load(archive.extractfile('data/db.json'))
        if sorted(u['id'] for u in accounts['users']) != json.loads((directory / 'inventory.json').read_text())['users']:
            raise RuntimeError('Backup API identity mismatch')
        if not archive.extractfile('data/secret').read().strip():
            raise RuntimeError('Backup signing key is empty')


def backup(directory):
    check_compose(ROOT / 'compose.prod.yml')
    before = check_running()
    database = database_path()
    required_bytes = database.stat().st_size * 2 + sum(p.stat().st_size for p in (ROOT / 'data').rglob('*') if p.is_file()) * 2
    if shutil.disk_usage(STATE).free < required_bytes + 256 * 1024 * 1024:
        raise RuntimeError('Insufficient free disk for a verified backup')
    directory.mkdir(mode=0o700)
    # SQLite backup API includes committed WAL transactions without copying live sidecars.
    with closing(connect(database)) as source, closing(sqlite3.connect(directory / 'nutrition.db')) as target:
        source.backup(target)
    with tarfile.open(directory / 'api-data.tar.gz', 'w:gz') as archive:
        archive.add(ROOT / 'data', arcname='data')
    for source, name in [(ROOT / '.env', 'env'), (ROOT / 'compose.prod.yml', 'compose')]:
        shutil.copy2(source, directory / name)
    (directory / 'images.json').write_text(json.dumps(docker('inspect', 'opengym-api-1', 'opengym-nutrition-1', 'opengym-web-1')))
    # Re-check identities using the captured API archive, not a concurrently changed live file.
    with tarfile.open(directory / 'api-data.tar.gz') as archive:
        users = sorted(u['id'] for u in json.load(archive.extractfile('data/db.json'))['users'])
    with closing(connect(directory / 'nutrition.db')) as db:
        captured = dict(users=users, migrations=[r[0] for r in db.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId')],
                        **{t: sorted([list(r) for r in db.execute(f'SELECT UserId, Id FROM {t}')]) for t in ('Products', 'DiaryEntries')})
    if any(not set(map(tuple, captured[t])).issubset(set(map(tuple, check_running()[t]))) for t in ('Products', 'DiaryEntries')):
        raise RuntimeError('Store changed incompatibly while backing up')
    if not {u for t in ('Products', 'DiaryEntries') for u, _ in captured[t]}.issubset(users):
        raise RuntimeError('Backup crosses an account change; retry with writes quiesced')
    if any(not set(map(tuple, before[t])).issubset(set(map(tuple, captured[t]))) for t in ('Products', 'DiaryEntries')):
        raise RuntimeError('Backup lost pre-existing identities')
    (directory / 'inventory.json').write_text(json.dumps(captured))
    (directory / 'checksums.json').write_text(json.dumps({p.name: digest(p) for p in directory.iterdir() if p.is_file()}))
    verify_backup(directory)
    (directory / 'COMPLETE').write_text(datetime.datetime.now(datetime.timezone.utc).isoformat())


def preserved(directory, rollback=False):
    verify_backup(directory)
    old = json.loads((directory / 'inventory.json').read_text())
    live = inventory(database_path(), ROOT / 'data') if rollback else check_running()
    if rollback and old['migrations'] != live['migrations']:
        raise RuntimeError('Schema changed: automatic application rollback is unsafe; preserve data and recover explicitly')
    if not set(old['users']).issubset(live['users']):
        raise RuntimeError('API identities disappeared')
    for table in ('Products', 'DiaryEntries'):
        if not set(map(tuple, old[table])).issubset(set(map(tuple, live[table]))):
            raise RuntimeError('Nutrition identities disappeared or changed owner')


def rotate():
    # Only our completed backup subdirectories, never data, images, config or incident backups.
    snapshots = sorted([p for pattern in ('rollback-*', 'recovered-*') for p in STATE.glob(pattern)
                        if not p.is_symlink() and p.is_dir() and (p / 'backup/COMPLETE').is_file()],
                       key=lambda p: (p / 'backup/COMPLETE').stat().st_mtime, reverse=True)
    previous = (STATE / 'previous').resolve()
    for snapshot in snapshots[RETAIN_BACKUPS:]:
        target = snapshot / 'backup'
        if snapshot != previous and target.resolve().parent == snapshot.resolve() and not target.is_symlink():
            shutil.rmtree(target)


if __name__ == '__main__':
    os.umask(0o077)
    command, *args = sys.argv[1:]
    try:
        if command == 'check':
            check_compose(Path(args[0])); check_running()
        elif command == 'config': check_compose(Path(args[0]))
        elif command == 'backup': backup(Path(args[0]))
        elif command == 'verify-backup': verify_backup(Path(args[0]))
        elif command == 'verify': preserved(Path(args[0]))
        elif command == 'rollback-safe': preserved(Path(args[0]), rollback=True)
        elif command == 'rotate': rotate()
        else: raise RuntimeError('Unknown storage operation')
    except Exception as error:
        # Never print rows, IDs, account names, secrets or subprocess config output.
        print('Storage safety check failed: ' + (str(error) if isinstance(error, RuntimeError) else type(error).__name__), file=sys.stderr)
        sys.exit(1)
