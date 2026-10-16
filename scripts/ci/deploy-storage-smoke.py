#!/usr/bin/env python3
"""Real CD/Compose against disposable storage only. Requires Linux Docker and CI images."""
import json
import importlib.util
from contextlib import closing
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import unittest
from urllib.request import urlopen

REPO = Path(__file__).resolve().parents[2]


def run(*args, **kwargs):
    result = subprocess.run(args, text=True, capture_output=True, **kwargs)
    if result.returncode:
        raise AssertionError(f'Isolated command failed: {args}\n{result.stdout}\n{result.stderr}')
    return result.stdout


class ComposeStorageSmoke(unittest.TestCase):
    def test_root_reader_keeps_wal_sidecars_writable_by_application(self):
        spec = importlib.util.spec_from_file_location('storage', REPO / 'deploy/opengym-storage.py')
        storage = importlib.util.module_from_spec(spec); spec.loader.exec_module(storage)
        with tempfile.TemporaryDirectory(prefix='opengym-owner-ci-') as directory:
            path = Path(directory) / 'nutrition.db'
            with closing(sqlite3.connect(path)) as db, db:
                db.execute('PRAGMA journal_mode=WAL'); db.execute('CREATE TABLE sample(id INTEGER)')
            os.chown(path, 1654, 1654)
            with storage.connect(path) as db: self.assertEqual(db.execute('SELECT COUNT(*) FROM sample').fetchone()[0], 0)
            for suffix in ('-wal', '-shm'):
                sidecar = Path(str(path) + suffix)
                self.assertTrue(sidecar.exists())
                self.assertEqual((sidecar.stat().st_uid, sidecar.stat().st_gid), (1654, 1654))

    def test_real_update_rollback_and_fail_closed_storage(self):
        with tempfile.TemporaryDirectory(prefix='opengym-storage-ci-') as directory:
            root = Path(directory)
            project = root.name
            volume = project + '-nutrition'
            state = root / 'state'; state.mkdir()
            (root / 'data').mkdir(); (root / 'coach-auth').mkdir()
            for media in ('img', 'gif'): (root / 'media' / media).mkdir(parents=True)
            with socket.socket() as sock:
                sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
            base = f'http://127.0.0.1:{port}'
            sha = run('docker', 'image', 'inspect', '-f', '{{index .Config.Labels "org.opencontainers.image.revision"}}', 'opengym-api:ci').strip()
            old = 'b' * 40
            for service in ('api', 'web', 'nutrition'):
                remote = f'ghcr.io/illya228666/opengym-{service}'
                run('docker', 'tag', f'opengym-{service}:ci', remote + ':sha-' + sha)
                run('docker', 'build', '-q', '-t', remote + ':sha-' + old, '-', input=
                    f'FROM opengym-{service}:ci\nLABEL org.opencontainers.image.revision={old}\n')
            (root / '.env').write_text(f'IMAGE_TAG=sha-{old}\nPASSWORD_LOGIN=1\nFIRST_USER_ADMIN=1\nORIGIN={base}\n')
            compose = (REPO / 'deploy/compose.prod.yml').read_text().replace('/opt/opengym', str(root)).replace(
                'opengym_nutrition-data', volume).replace('"8080:80"', f'"127.0.0.1:{port}:80"')
            compose += '\n'  # Candidate must differ from installed configuration to prove config refresh.
            candidate = root / 'candidate'; candidate.write_text(compose.replace('TRUST_PROXY: "1"', 'TRUST_PROXY: "1"\n      DEPLOY_SMOKE_REVISION: "candidate"'))
            active = root / 'compose.prod.yml'; active.write_text(compose)
            bootstrap = root / 'bootstrap'; bootstrap.write_text(compose.replace('RequireExistingDatabase: "true"', 'RequireExistingDatabase: "false"').replace('REQUIRE_EXISTING_DATA: "1"', 'REQUIRE_EXISTING_DATA: "0"'))
            docker_compose = ['docker', 'compose', '--project-name', project, '--project-directory', str(root), '--env-file', str(root / '.env')]
            def up(file, *args): return run(*docker_compose, '-f', str(file), 'up', '-d', '--no-deps', '--pull', 'never', *args)
            def ready():
                for _ in range(60):
                    try:
                        with urlopen(base + '/api/health', timeout=2) as response:
                            if json.load(response)['ok']: return
                    except Exception: pass
                    time.sleep(1)
                self.fail('Isolated stack not ready')
            env = dict(os.environ, NUTRITION_SMOKE_STATE=str(root / 'expected.json'))
            def records():
                ready()
                # Nutrition readiness can lag Node on a container recreation.
                for attempt in range(30):
                    result = subprocess.run(['node', str(REPO / 'scripts/ci/nutrition-smoke.mjs'), base, '--verify-persistence'], env=env, capture_output=True, text=True)
                    if result.returncode == 0: return
                    time.sleep(1)
                self.fail(result.stdout + result.stderr)
            run('docker', 'volume', 'create', volume)
            try:
                up(bootstrap, 'api', 'web', 'nutrition'); ready()
                for _ in range(30):
                    result = subprocess.run(['docker', 'exec', project + '-api-1', 'node', '-e', "fetch('http://nutrition:8080/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"], capture_output=True)
                    if result.returncode == 0: break
                    time.sleep(1)
                run('node', str(REPO / 'scripts/ci/nutrition-smoke.mjs'), base, env=env)
                up(active, 'api', 'web', 'nutrition'); records()
                # Exercise the installed transaction, replacing only paths and external release discovery.
                helper_text = (REPO / 'deploy/opengym-storage.py').read_text().replace('/opt/opengym', str(root)).replace('/var/lib/opengym-deploy', str(state)).replace('opengym_nutrition-data', volume).replace("'opengym'", repr(project)).replace("f'opengym-{service}-1'", "f'" + project + "-{service}-1'")
                helper = root / 'opengym-storage.py'; helper.write_text(helper_text)
                deploy = (REPO / 'deploy/opengym-deploy').read_text().replace('/opt/opengym', str(root)).replace('/var/lib/opengym-deploy', str(state)).replace('/run/opengym-deploy.lock', str(root / 'lock')).replace('/usr/local/sbin/opengym-storage.py', str(helper)).replace('--project-name opengym', '--project-name ' + project).replace('opengym-api-1', project + '-api-1').replace('opengym-web-1', project + '-web-1').replace('opengym-nutrition-1', project + '-nutrition-1').replace('127.0.0.1:8080', f'127.0.0.1:{port}').replace('READY_TIMEOUT_SECONDS=120', 'READY_TIMEOUT_SECONDS=8')
                deploy_file = root / 'deploy'; deploy_file.write_text(deploy)
                bin_dir = root / 'bin'; bin_dir.mkdir()
                real_curl = shutil.which('curl'); real_docker = shutil.which('docker')
                curl = bin_dir / 'curl'
                curl.write_text('#!/usr/bin/env python3\nimport sys,json,os,shutil\na=sys.argv[1:]\nurl=next((x for x in a if x.startswith("https://")), "")\n'
                    f'if "/branches/main" in url: print(json.dumps({{"commit":{{"sha":{sha!r}}}}}))\n'
                    f'elif "/actions/runs?" in url: print(json.dumps({{"workflow_runs":[{{"path":".github/workflows/ci.yml","head_sha":{sha!r},"event":"push","status":"completed","conclusion":"success"}}]}}))\n'
                    f'elif "raw.githubusercontent.com" in url: shutil.copyfile({str(candidate)!r},a[a.index("-o")+1])\n'
                    f'else: os.execv({real_curl!r},[{real_curl!r},*a])\n'); curl.chmod(0o755)
                wrapper = bin_dir / 'docker'
                wrapper.write_text(f'#!/bin/sh\nif [ "$1" = pull ]; then exit 0; fi\nexec {real_docker} "$@"\n'); wrapper.chmod(0o755)
                deploy_env = dict(env, PATH=str(bin_dir) + ':' + os.environ['PATH'])
                run('bash', str(deploy_file), env=deploy_env)
                self.assertEqual(active.read_text(), candidate.read_text())
                self.assertEqual(run('docker', 'inspect', '-f', '{{.Image}}', project + '-nutrition-1').strip(), run('docker', 'image', 'inspect', '-f', '{{.Id}}', 'opengym-nutrition:ci').strip())
                records()
                run('bash', str(deploy_file), '--rollback', env=deploy_env); records()
                # Retry the same isolated candidate with a failing entry point; automatic rollback must retain records.
                (state / 'hold').unlink(); (state / 'failed-releases').unlink()
                candidate.write_text(candidate.read_text().replace('nutrition:\n', 'nutrition:\n    entrypoint: ["/bin/false"]\n'))
                result = subprocess.run(['bash', str(deploy_file)], env=deploy_env, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0); records()
                self.assertFalse((state / 'pending').exists(), result.stdout + result.stderr)
                (state / 'failed-releases').unlink()
                candidate.write_text(compose.replace(volume, volume + '-absent'))
                before = run('docker', 'inspect', '-f', '{{.Id}}', project + '-nutrition-1')
                result = subprocess.run([*docker_compose, '-f', str(candidate), 'up', '-d', '--no-deps', '--pull', 'never', 'nutrition'], capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0, 'Compose must not create a missing external volume')
                result = subprocess.run(['bash', str(deploy_file)], env=deploy_env, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(before, run('docker', 'inspect', '-f', '{{.Id}}', project + '-nutrition-1'))
                records()
                candidate.write_text(compose.replace('      - nutrition-data:/data\n', ''))
                result = subprocess.run(['bash', str(deploy_file)], env=deploy_env, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0, 'Preflight must reject an ephemeral Nutrition container')
                self.assertEqual(before, run('docker', 'inspect', '-f', '{{.Id}}', project + '-nutrition-1'))
                # Changing project name still resolves the same external volume.
                cfg = json.loads(run('docker', 'compose', '--project-name', project + '-renamed', '--project-directory', str(root), '--env-file', str(root / '.env'), '-f', str(active), 'config', '--format', 'json'))
                self.assertEqual(cfg['volumes']['nutrition-data']['name'], volume)
            finally:
                # Explicit isolated containers only; no volume deletion (including in CI).
                for service in ('web', 'api', 'nutrition'):
                    subprocess.run(['docker', 'rm', '-f', project + '-' + service + '-1'], capture_output=True)
                subprocess.run(['docker', 'network', 'rm', project + '_default'], capture_output=True)


if __name__ == '__main__': unittest.main(verbosity=2)
