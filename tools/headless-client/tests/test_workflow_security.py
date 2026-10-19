from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github" / "workflows"


def test_workflow_actions_are_pinned_to_full_commit_ids():
    uses = []
    for path in WORKFLOWS.glob("*.yml"):
        for line_number, line in enumerate(path.read_text().splitlines(), start=1):
            if re.match(r"\s*-\s+uses:", line):
                uses.append((path.name, line_number, line))

    assert uses
    for name, line_number, line in uses:
        assert re.search(r"@[0-9a-f]{40}(?:\s+#|\s*$)", line), (
            f"{name}:{line_number} uses a mutable action reference: {line.strip()}"
        )


def test_workflows_do_not_use_pull_request_target():
    assert all("pull_request_target" not in path.read_text() for path in WORKFLOWS.glob("*.yml"))


def test_docker_base_images_are_pinned_to_digests():
    dockerfile = (ROOT / "Dockerfile").read_text()
    bases = [line for line in dockerfile.splitlines() if line.startswith("FROM ")]

    assert bases
    for line in bases:
        assert re.search(r"@sha256:[0-9a-f]{64}(?:\s|$)", line), (
            f"Dockerfile uses a mutable base image reference: {line}"
        )
