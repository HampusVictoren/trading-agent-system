"""The collector compose runs: what it is pinned to, where it listens, and how much it keeps.

Read from docker-compose.yml, so a change that loosens any of the three fails here on every
pull request rather than in a running stack.
"""

from pathlib import Path

import yaml

COMPOSE_FILE = Path(__file__).resolve().parents[3] / "docker-compose.yml"


def _collector() -> dict:
    return yaml.safe_load(COMPOSE_FILE.read_text(encoding="utf-8"))["services"]["otel-collector"]


def test_the_image_is_pinned_by_version_and_digest():
    image = _collector()["image"]

    name, _, digest = image.partition("@")
    assert digest.startswith("sha256:") and len(digest) == len("sha256:") + 64
    assert name.rsplit(":", 1)[1][0].isdigit(), "a version tag, not latest"


def test_it_is_published_on_loopback_only():
    assert all(port.startswith("127.0.0.1:") for port in _collector()["ports"])


def test_its_log_is_capped():
    # The debug exporter prints every span at detailed verbosity and the service restarts unless
    # stopped; without a cap its log grows for as long as the stack runs.
    logging = _collector()["logging"]

    assert logging["driver"] == "json-file"
    assert logging["options"]["max-size"] == "10m"
    assert logging["options"]["max-file"] == "3"
