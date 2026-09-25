"""Extract CI-built archives after validating all members before writing any files."""

import os
from pathlib import Path, PurePosixPath
import stat
import sys
import tarfile
import zipfile

MAX_FILES = 10000
MAX_BYTES = 2 * 1024 * 1024 * 1024


def validate_members(members):
    if not members or len(members) > MAX_FILES:
        raise ValueError("Invalid archive member count")
    total = 0
    seen = set()
    for name, size, regular in members:
        path = PurePosixPath(name.replace("\\", "/"))
        if (not name or path.is_absolute() or ".." in path.parts or ":" in name
                or "\\" in name or "\x00" in name or not regular):
            raise ValueError("Unsafe archive member")
        normalized = str(path)
        if normalized in seen:
            raise ValueError("Duplicate archive member")
        seen.add(normalized)
        total += size
        if size < 0 or total > MAX_BYTES:
            raise ValueError("Archive exceeds the extraction limit")


def extract_package(archive, destination):
    archive = Path(archive)
    destination = Path(destination)
    if destination.exists() or destination.is_symlink():
        raise ValueError("Extraction requires a new destination directory")
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as source:
            entries = source.infolist()
            validate_members([(entry.filename, entry.file_size,
                               stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR))
                              for entry in entries])
            destination.mkdir(mode=0o700)
            source.extractall(destination)
    elif archive.name.endswith(".tar.gz"):
        with tarfile.open(archive, "r:gz") as source:
            entries = source.getmembers()
            validate_members([(entry.name, entry.size, entry.isfile() or entry.isdir()) for entry in entries])
            destination.mkdir(mode=0o700)
            source.extractall(destination, members=entries, filter="data")
    else:
        raise ValueError("Unsupported archive format")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: extract-package.py ARCHIVE NEW_DESTINATION")
    os.umask(0o077)
    extract_package(sys.argv[1], sys.argv[2])
