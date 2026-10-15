"""Regression tests for the main release's PR provenance wait and fail-closed behavior."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("verify-pr-release.sh")
MERGE = "a" * 40
BASE = "b" * 40
HEAD = "c" * 40
TREE = "d" * 40

MOCK_GIT = """#!/usr/bin/env python3
import os,sys
args=sys.argv[1:]
if args==["rev-list","--parents","-n","1",os.environ["GITHUB_SHA"]]:
    print(os.environ["GITHUB_SHA"],os.environ["FAKE_BASE"],os.environ["FAKE_HEAD"])
elif args==["rev-parse","HEAD^{tree}"]:
    print(os.environ["FAKE_TREE"])
else:
    sys.exit(2)
"""

MOCK_GH = """#!/usr/bin/env python3
import json,os,sys
from pathlib import Path
args=sys.argv[1:]
mode=os.environ["FAKE_MODE"]
head=os.environ["FAKE_HEAD"]
if args[0]=="api" and "/commits/" in args[1] and "/pulls" in args[1]:
    print(json.dumps([{"number":17,"merged_at":"2026-10-15T06:59:27Z",
        "merge_commit_sha":os.environ["GITHUB_SHA"],
        "base":{"ref":"main"},"head":{"repo":{"full_name":os.environ["GITHUB_REPOSITORY"]},
        "sha":head}}]))
elif args[0]=="api" and any("/actions/workflows/ci.yml/runs" in a for a in args):
    counter=Path(os.environ["FAKE_COUNTER"])
    n=int(counter.read_text())+1 if counter.exists() else 1
    counter.write_text(str(n))
    if mode=="missing":
        runs=[]
    else:
        pending=mode=="pending" and n==1
        runs=[{"id":333,"status":"in_progress" if pending else "completed",
            "conclusion":None if pending else "success","head_sha":head,
            "path":".github/workflows/ci.yml"}]
    print(json.dumps({"workflow_runs":runs}))
elif args[:2]==["run","download"]:
    folder=Path(args[args.index("--dir")+1])
    tree="e"*40 if mode=="wrong_tree" else os.environ["FAKE_TREE"]
    (folder/"verified-pr-tree.txt").write_text(
        "tree="+tree+"\\nhead="+head+"\\npr=17\\n")
else:
    sys.exit(2)
"""

class ReleaseGateTests(unittest.TestCase):
    def run_gate(self, mode, expected_success):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            for name, source in (("git", MOCK_GIT), ("gh", MOCK_GH)):
                file = fake_bin / name
                file.write_text(source)
                file.chmod(0o755)
            env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ["PATH"],
                       GITHUB_REPOSITORY="illya228666/openGym", GITHUB_SHA=MERGE,
                       GITHUB_EVENT_NAME="push", GITHUB_REF="refs/heads/main",
                       GH_TOKEN="mock", FAKE_BASE=BASE, FAKE_HEAD=HEAD, FAKE_TREE=TREE,
                       FAKE_MODE=mode, FAKE_COUNTER=str(root / "counter"),
                       GITHUB_STEP_SUMMARY=str(root / "summary"),
                       PR_RELEASE_MAX_WAIT_SECONDS="2", PR_RELEASE_POLL_SECONDS="1")
            p = subprocess.run(["bash", str(SCRIPT)], cwd=root, env=env,
                               text=True, capture_output=True, timeout=12)
            self.assertEqual(p.returncode == 0, expected_success, p.stdout + p.stderr)
            if expected_success:
                self.assertIn("Release provenance validated", (root / "summary").read_text())
                self.assertIn(TREE, (root / "summary").read_text())
            else:
                self.assertIn("Publish blocked", p.stderr)

    def test_immediately_completed(self):
        self.run_gate("complete", True)

    def test_pending_ci_eventually_completes(self):
        self.run_gate("pending", True)

    def test_never_accepts_wrong_tree(self):
        self.run_gate("wrong_tree", False)

    def test_missing_pr_check_fails_closed(self):
        self.run_gate("missing", False)

if __name__ == "__main__":
    unittest.main()
