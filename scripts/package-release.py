#!/usr/bin/env python3
"""Package VOD-Fork and generate Jellyfin's repository entry for that exact ZIP."""

import argparse
import hashlib
import json
from pathlib import Path
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

GUID = "16bb0693-4248-478b-a1e5-a4508326245c"
ABI = "12.1.0.0"
OWNER = "MultiChipModule (upstream bakeneko)"
DESCRIPTION = "VOD-Fork adds selectable English YouTube captions to the upstream Jellyfin VOD plugin."
CHANGELOG = "Stop playback retry storms during YouTube cooldowns, enforce configured extraction authentication, and reuse playback resolutions."
DLL = "Jellyfin.Plugin.VOD.dll"
REPOSITORY = "MultiChipModule/VOD-Fork"
ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("publish", type=Path, help="dotnet publish output directory")
    parser.add_argument("output", type=Path, help="artifact destination")
    parser.add_argument("--timestamp", required=True, help="release timestamp in ISO 8601 UTC")
    args = parser.parse_args()
    version = ElementTree.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    artifact_name = f"vod-fork_{version}.zip"
    args.output.mkdir(parents=True, exist_ok=True)
    identity = {
        "guid": GUID,
        "name": "VOD",
        "description": DESCRIPTION,
        "overview": "YouTube English captions for Jellyfin VOD",
        "owner": OWNER,
        "category": "General",
    }
    release = {"version": version, "changelog": CHANGELOG, "targetAbi": ABI, "timestamp": args.timestamp}
    metadata = {**identity, **release, "status": "Active", "autoUpdate": False, "assemblies": [DLL]}

    artifact = args.output / artifact_name
    with ZipFile(artifact, "w", compression=ZIP_DEFLATED) as archive:
        for name, payload in (
            (DLL, (args.publish / DLL).read_bytes()),
            ("LICENSE", (ROOT / "LICENSE").read_bytes()),
            ("meta.json", (json.dumps(metadata, indent=2) + "\n").encode()),
        ):
            # Keep archived timestamps independent of local filesystem time.
            archive.writestr(ZipInfo(name), payload, compress_type=ZIP_DEFLATED)

    content = artifact.read_bytes()
    digest = hashlib.sha256(content).hexdigest()
    (args.output / f"{artifact_name}.sha256").write_text(f"{digest}  {artifact_name}\n", encoding="utf-8")
    release["sourceUrl"] = f"https://github.com/{REPOSITORY}/releases/download/v{version}/{artifact_name}"
    release["checksum"] = hashlib.md5(content, usedforsecurity=False).hexdigest()
    previous = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    versions = next((item["versions"] for item in previous if item["guid"] == GUID), [])
    manifest = [{**identity, "versions": [release, *(item for item in versions if item["version"] != version)]}]
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=4) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
