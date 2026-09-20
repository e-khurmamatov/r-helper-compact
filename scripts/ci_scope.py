"""Select full CI unless every changed path is user/contributor documentation."""

import json
import os
from pathlib import Path, PurePosixPath
import subprocess


DOCUMENTS = {
    "README.md", "README.ru.md", "AGENTS.md", "CONTRIBUTING.md",
    "RELEASING.md", "devices/README.md", "winui/Locales/README.md",
}


def is_documentation(path):
    item = PurePosixPath(path)
    return (
        path in DOCUMENTS
        or (path.startswith("docs/") and item.suffix == ".md")
        or (path.startswith("assets/screenshots/") and item.suffix in {".png", ".jpg", ".webp"})
    )


def requires_checks(paths):
    return not paths or any(not is_documentation(path) for path in paths)


def changed_paths(event_name, event):
    if event_name == "pull_request":
        base = event["pull_request"]["base"]["sha"]
        head = event["pull_request"]["head"]["sha"]
        base = subprocess.check_output(["git", "merge-base", base, head], text=True).strip()
    elif event_name == "push":
        base, head = event["before"], event["after"]
        if not base.strip("0"):
            return []
    else:
        return []  # Scheduled and manual runs always perform full checks.
    # Disable rename detection so both old and new paths are checked.
    raw = subprocess.check_output(["git", "diff", "--name-only", "--no-renames", "-z", base, head, "--"])
    return [path.decode("utf-8", errors="replace") for path in raw.split(b"\0") if path]


if __name__ == "__main__":
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
    run_checks = requires_checks(changed_paths(os.environ["GITHUB_EVENT_NAME"], event))
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"run_checks={str(run_checks).lower()}\n")
    print("Full CI required" if run_checks else "Documentation only: skipping build and code analysis")
