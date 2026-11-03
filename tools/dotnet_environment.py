"""The environment every .NET command of the build and the tests runs in.

The repository's own SDK and package folder, English output, and no telemetry, first-run text or developer certificate.
scripts/test.sh sets the same variables for its own dotnet commands.
"""

from __future__ import annotations

import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / "tools/ios-dotnet"
PACKAGES = ROOT / ".tools/nuget/packages"
PYTHON_BIN = ROOT / "tools/python/bin"
SETTINGS = {
    "DOTNET_CLI_UI_LANGUAGE": "en",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    "DOTNET_NOLOGO": "1",
    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
    "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
}


def dotnet_environment(base: dict | None = None, packages: Path = PACKAGES) -> dict:
    """A copy of base (the process environment by default) set up for the repository's dotnet."""
    environment = dict(os.environ if base is None else base)
    environment.update(SETTINGS)
    environment.update(
        DOTNET_ROOT=str(DOTNET),
        NUGET_PACKAGES=str(packages),
        DOTNET_CLI_HOME=str(ROOT / ".tools/dotnet-home"),
    )
    path = environment.get("PATH", "")
    if not path.startswith(f"{PYTHON_BIN}:{DOTNET}:"):
        environment["PATH"] = f"{PYTHON_BIN}:{DOTNET}:{path}"
    return environment
