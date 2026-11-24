"""Build the Thunderstore package from a published GitHub release zip.

Usage: python -I packaging/pack_thunderstore.py <SailwindPlayerModel-vX.Y.Z.zip> <out.zip>

Puts SailwindPlayerModel.dll at the package root next to the manifest, icon and store README from
packaging/thunderstore/. Packed with zipfile: forward slashes, deflate, no data descriptors. Never pack with
Compress-Archive (backslash paths) or tar. Upload this package before co-op's, which depends on it.
"""
import json
import pathlib
import re
import sys
import zipfile

HERE = pathlib.Path(__file__).resolve().parent / "thunderstore"
PLUGIN_FILES = {"BepInEx/plugins/SailwindPlayerModel/SailwindPlayerModel.dll": "SailwindPlayerModel.dll"}


def main(release_zip: str, out_zip: str) -> None:
    manifest = json.loads((HERE / "manifest.json").read_text(encoding="utf-8"))
    match = re.search(r"v(\d+\.\d+\.\d+)\.zip$", release_zip)
    if not match or match.group(1) != manifest["version_number"]:
        sys.exit(f"manifest.json is {manifest['version_number']} but the release zip is {release_zip}")
    if len(manifest["description"]) > 250:
        sys.exit("manifest description is over 250 characters")

    with zipfile.ZipFile(release_zip) as src, zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as dst:
        for name in ("manifest.json", "icon.png", "README.md"):
            dst.writestr(name, (HERE / name).read_bytes())
        for src_name, dst_name in PLUGIN_FILES.items():
            dst.writestr(dst_name, src.read(src_name))

    with zipfile.ZipFile(out_zip) as check:
        for info in check.infolist():
            if "\\" in info.filename or info.flag_bits & 0x08:
                sys.exit(f"bad entry {info.filename!r}")
            print(f"{info.filename}  {info.file_size}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
