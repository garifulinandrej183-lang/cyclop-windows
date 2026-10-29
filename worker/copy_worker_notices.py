"""Collect installed wheel license texts and build metadata beside the frozen worker."""

from __future__ import annotations

import re
import shutil
import sys
from importlib.metadata import distributions
from pathlib import Path, PurePosixPath


def main() -> None:
    output = Path(sys.argv[1]).resolve()
    licenses = output / "licenses"
    licenses.mkdir(parents=True, exist_ok=True)
    lock = Path(__file__).with_name("requirements.lock").read_text(encoding="utf-8")
    pinned = {name.casefold().replace("_", "-"): version for name, version in
              re.findall(r"(?m)^([A-Za-z0-9_.-]+)==([^\s\\]+)", lock)}

    notices: list[str] = [
        "Cyclop offline translation worker third-party notices",
        "",
        "This file describes the exact Python distributions in worker/requirements.lock.",
        "License statements below come from installed wheel METADATA; verbatim license",
        "files supplied by those wheels are copied to this directory where present.",
        "",
    ]
    for distribution in sorted(distributions(), key=lambda d: (d.metadata.get("Name") or "").casefold()):
        name = distribution.metadata.get("Name") or "unknown"
        normalized = name.casefold().replace("_", "-")
        if normalized not in pinned:
            continue
        version = distribution.version
        license_text = distribution.metadata.get("License-Expression") or distribution.metadata.get("License") or "not declared in wheel metadata"
        projects = distribution.metadata.get_all("Project-URL") or []
        notices.append(f"{name}=={version}\nDeclared license: {license_text}")
        for project in projects:
            notices.append(f"Project: {project}")

        package_dir = licenses / f"{name}-{version}"
        copied = False
        for package_file in distribution.files or ():
            relative = PurePosixPath(str(package_file))
            parts = relative.parts
            if "licenses" in parts:
                suffix = Path(*parts[parts.index("licenses") + 1:])
            elif any(part.endswith(".dist-info") for part in parts[:-1]) and relative.name.upper().startswith(("LICENSE", "COPYING", "NOTICE")):
                suffix = Path(relative.name)
            else:
                continue
            source = Path(distribution.locate_file(package_file))
            if not source.is_file():
                continue
            target = package_dir / suffix
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            copied = True
        notices.append("Wheel license files: copied" if copied else "Wheel license files: not present")
        notices.append("")

    python_license = Path(sys.base_prefix) / "LICENSE.txt"
    if python_license.is_file():
        target = licenses / "Python" / "LICENSE.txt"
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(python_license, target)
        notices.extend(["Python runtime license text: licenses/Python/LICENSE.txt", ""])

    source_notices = Path(__file__).with_name("notices")
    for source in sorted(source_notices.glob("*")):
        if source.is_file():
            target = licenses / "upstream" / source.name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            notices.append(f"Additional upstream license text: licenses/upstream/{source.name}")

    (output / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(notices), encoding="utf-8")


if __name__ == "__main__":
    main()
