"""Small disposable SQLite fixtures exercise the production backup/identity checks."""
import importlib.util
from contextlib import closing
import os
import json
from pathlib import Path
import sqlite3
import shutil
import tarfile
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('storage', Path(__file__).with_name('opengym-storage.py'))
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'data').mkdir()
        (self.root / 'data/db.json').write_text(json.dumps({'users': [{'id': 'alice'}]}))
        (self.root / 'data/secret').write_text('isolated-signing-key')
        (self.root / '.env').write_text('IMAGE_TAG=isolated')
        (self.root / 'compose.prod.yml').write_text('fixture')
        self.db = self.root / 'nutrition.db'
        with closing(sqlite3.connect(self.db)) as db, db:
            db.executescript("CREATE TABLE __EFMigrationsHistory(MigrationId TEXT); INSERT INTO __EFMigrationsHistory VALUES('v1');"
                             "CREATE TABLE Products(UserId TEXT,Id TEXT); INSERT INTO Products VALUES('alice','food');"
                             "CREATE TABLE DiaryEntries(UserId TEXT,Id TEXT); INSERT INTO DiaryEntries VALUES('alice','entry');")
        for name, value in [('ROOT', self.root), ('STATE', self.root), ('database_path', lambda: self.db),
                            ('check_compose', lambda _: None), ('docker', lambda *args: [])]:
            p = patch.object(storage, name, value); p.start(); self.addCleanup(p.stop)
        p = patch.object(storage, 'check_running', lambda: storage.inventory(self.db, self.root / 'data'))
        p.start(); self.addCleanup(p.stop)

    def test_wal_backup_preserves_committed_records_and_detects_corruption(self):
        with closing(sqlite3.connect(self.db)) as live, live:
            live.execute('PRAGMA journal_mode=WAL')
            live.execute("INSERT INTO DiaryEntries VALUES('alice','wal-entry')"); live.commit()
            destination = self.root / 'backup'
            storage.backup(destination)
            self.assertTrue((destination / 'COMPLETE').exists())
            with storage.connect(destination / 'nutrition.db') as saved:
                self.assertEqual(saved.execute('SELECT Id FROM DiaryEntries ORDER BY Id').fetchall(), [('entry',), ('wal-entry',)])
            storage.preserved(destination)
            (destination / 'api-data.tar.gz').write_bytes(b'broken')
            with self.assertRaisesRegex(RuntimeError, 'checksum'):
                storage.verify_backup(destination)

    def test_data_loss_or_owner_change_is_rejected_and_schema_downgrade_blocked(self):
        destination = self.root / 'backup'; storage.backup(destination)
        with closing(sqlite3.connect(self.db)) as live, live:
            live.execute("INSERT INTO __EFMigrationsHistory VALUES('v2')")
        storage.preserved(destination)
        with self.assertRaisesRegex(RuntimeError, 'Schema changed'):
            storage.preserved(destination, rollback=True)
        with closing(sqlite3.connect(self.db)) as live, live:
            live.execute("UPDATE DiaryEntries SET Id='different-entry'")
        with self.assertRaisesRegex(RuntimeError, 'identities disappeared'):
            storage.preserved(destination)
        with closing(sqlite3.connect(self.db)) as live, live:
            live.execute("UPDATE Products SET UserId='unknown'")
        with self.assertRaisesRegex(RuntimeError, 'owners absent'):
            storage.inventory(self.db, self.root / 'data')

    def test_missing_database_is_not_created(self):
        missing = self.root / 'absent.db'
        with self.assertRaises(sqlite3.OperationalError):
            with storage.connect(missing): pass
        self.assertFalse(missing.exists())

    def test_restore_into_separate_directory_retains_identity_and_live_files(self):
        destination = self.root / 'backup'; storage.backup(destination)
        restored = self.root / 'restore'; restored.mkdir()
        shutil.copy2(destination / 'nutrition.db', restored / 'nutrition.db')
        with tarfile.open(destination / 'api-data.tar.gz') as archive:
            archive.extractall(restored, filter='data')
        self.assertEqual(storage.inventory(restored / 'nutrition.db', restored / 'data'),
                         storage.inventory(self.db, self.root / 'data'))
        self.assertTrue(self.db.exists())

    def test_rotation_retains_previous_and_never_touches_incident_or_live_data(self):
        for n in range(17):
            directory = self.root / f'rollback-{n:02}/backup'; directory.mkdir(parents=True)
            (directory / 'COMPLETE').write_text('ok')
            os.utime(directory / 'COMPLETE', (n + 1, n + 1))
        with patch.object(Path, 'resolve', autospec=True, side_effect=lambda p: self.root / 'rollback-00' if p == self.root / 'previous' else p):
            storage.rotate()
        self.assertTrue((self.root / 'rollback-00/backup').exists())
        self.assertTrue((self.root / 'data/db.json').exists())
        self.assertEqual(len(list(self.root.glob('rollback-*/backup'))), 15)


if __name__ == '__main__': unittest.main(verbosity=2)
