#!/usr/bin/env python3
"""bump_tools.py: look for newer versions of the tools pinned in the Dockerfile and update the pins.

Run by the weekly "Tool updates" workflow, which opens a pull request with the result; the
image build on that pull request is the test. Nothing is installed or merged here.

    python3 .github/scripts/bump_tools.py            # update the pins, print a Markdown summary
    python3 .github/scripts/bump_tools.py --dry-run  # only print what would change

The Dockerfile's ARG lines are the pins. The build workflow's env block repeats them (the tests and the image
build args read it there), so both are updated together. FFmpeg is the BtbN release branch build (8.1, 9.0...),
so a new FFmpeg release branch shows up as soon as BtbN builds it.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCKERFILE = ROOT / "Muxarr.Web/Dockerfile"
# Other files that repeat a pin as "NAME: 'value'" (a workflow env block), kept in step.
ALSO_PINNED_IN = [".github/workflows/build-and-deploy.yml"]
USER_AGENT = "Remuxarr tool updates (+https://github.com/yannb2/remuxarr)"


@dataclass
class Tool:
    arg: str              # the ARG in the Dockerfile
    name: str
    source: str           # page URL, or owner/repo on GitHub
    pattern: str = ""     # regex whose group 1 is a version (a page, or release asset names)
    github: str = ""      # "release": the latest release's tag; "assets": asset names of release `tag`
    tag: str = ""
    notes: str = ""       # release notes link for the pull request


TOOLS = [
    Tool("FFMPEG_VERSION", "FFmpeg", "BtbN/FFmpeg-Builds", r"ffmpeg-n(\d+\.\d+)-latest-linux64-gpl-\d",
         github="assets", tag="latest", notes="https://ffmpeg.org/download.html#releases"),
    Tool("MKVTOOLNIX_VERSION", "MKVToolNix", "https://mkvtoolnix.download/latest-release.xml",
         r"<version>\s*(\d+\.\d+(?:\.\d+)?)\s*</version>", notes="https://mkvtoolnix.download/doc/NEWS.md"),
    Tool("DOVI_TOOL_VERSION", "dovi_tool", "quietvoid/dovi_tool", r"^(\d+\.\d+\.\d+)$", github="release",
         notes="https://github.com/quietvoid/dovi_tool/releases"),
    # Built from crates.io (alass-cli); the GitHub release tag is the same version.
    Tool("ALASS_VERSION", "alass", "kaegi/alass", r"^(\d+\.\d+\.\d+)$", github="release",
         notes="https://github.com/kaegi/alass/releases"),
]


def version_key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", version))


def newest(versions: list[str], hold_major: int | None = None) -> str | None:
    """The highest version, keeping the same major version when hold_major is set."""
    allowed = [v for v in versions if hold_major is None or version_key(v)[0] == hold_major]
    return max(allowed, key=version_key, default=None)


def read_args(text: str) -> dict[str, str]:
    """ARG NAME=value lines with a value: the pins."""
    return dict(re.findall(r"^ARG ([A-Z0-9_]+)=(\S+)$", text, re.MULTILINE))


def set_arg(text: str, arg: str, value: str) -> str:
    return re.sub(rf"^ARG {arg}=\S+$", f"ARG {arg}={value}", text, count=1, flags=re.MULTILINE)


def set_env(text: str, arg: str, value: str) -> str:
    """NAME: 'value' (or "value", or bare) in a workflow env block."""
    return re.sub(rf"^(\s+{arg}:\s*)(['\"]?)[^'\"\s]+\2\s*$", rf"\g<1>\g<2>{value}\g<2>", text, flags=re.MULTILINE)


def fetch(url: str) -> str:
    headers = {"User-Agent": USER_AGENT}
    if url.startswith("https://api.github.com/") and os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = f"Bearer {os.environ['GITHUB_TOKEN']}"
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
        return response.read().decode("utf-8", "replace")


def upstream(tool: Tool) -> list[str]:
    if tool.github == "release":
        tag = json.loads(fetch(f"https://api.github.com/repos/{tool.source}/releases/latest"))["tag_name"].lstrip("vV")
        # With a pattern, a release whose tag does not look like the tool's version (libdovi-3.4.0) is skipped.
        return re.findall(tool.pattern, tag) if tool.pattern else [tag]
    if tool.github == "assets":
        release = json.loads(fetch(f"https://api.github.com/repos/{tool.source}/releases/tags/{tool.tag}"))
        names = " ".join(asset["name"] for asset in release["assets"])
        return re.findall(tool.pattern, names)
    return re.findall(tool.pattern, fetch(tool.source))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--dockerfile", type=Path, default=DOCKERFILE)
    args = parser.parse_args()

    text = args.dockerfile.read_text()
    pins = read_args(text)
    rows, failed = [], []
    for tool in TOOLS:
        current = pins.get(tool.arg)
        if current is None:
            failed.append(f"{tool.name}: no ARG {tool.arg} in the Dockerfile")
            continue
        try:
            found = upstream(tool)
        except (urllib.error.URLError, TimeoutError, KeyError, ValueError) as error:
            failed.append(f"{tool.name}: could not check ({error})")
            continue
        latest = newest(found)
        if latest is None:
            failed.append(f"{tool.name}: no version found at {tool.source}")
        elif version_key(latest) > version_key(current):
            rows.append((tool, current, latest))
            text = set_arg(text, tool.arg, latest)

    if rows:
        print("| Tool | Now | New | Notes |\n|---|---|---|---|")
        for tool, current, latest in rows:
            print(f"| {tool.name} | {current} | **{latest}** | [release notes]({tool.notes}) |")
    else:
        print("No newer versions found.")
    for line in failed:
        print(f"\n- {line}")

    if rows and not args.dry_run:
        args.dockerfile.write_text(text)
        for path in ALSO_PINNED_IN:
            other = (ROOT / path).read_text()
            for tool, _, latest in rows:
                other = set_env(other, tool.arg, latest)
            (ROOT / path).write_text(other)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"changed={'true' if rows else 'false'}\n")
            out.write(f"tools={', '.join(f'{t.name} {v}' for t, _, v in rows)}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
