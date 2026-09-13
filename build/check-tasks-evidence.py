#!/usr/bin/env python3
"""
X4: tasks.md cannot claim evidence that does not exist.

    python3 build/check-tasks-evidence.py          # exit 1 on a dangling claim

WHY. On 2026-08-29 the status record marked 251 items done, including nine interfaces with zero
implementations; the truth-up fixed it, and by the evening two notes were stale again. A status
file is only worth reading if the code it names is there. So: every `[x]` item that names a path
or a type in backticks must resolve to something under src/ or tests/ - a directory, a .cs file,
or a class whose file name matches the last segment. Prose claims ("12 tests") are not checked;
paths are, because they are the evidence.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TASKS = os.path.join(ROOT, ".kiro", "specs", "epacs-offline-installer", "tasks.md")

# A backticked token that looks like a code path: Project.Name/Sub/Thing, with or without .cs
PATH_TOKEN = re.compile(r"`((?:Installer|SharedKernel|ManifestVerifier|BackupRestore|SupportBundle|Sync)[A-Za-z.]*/[A-Za-z0-9_./-]+)`")

PROJECT_DIRS = {}
for base in ("src", "tests", "harness/src", "harness/tests"):
    full = os.path.join(ROOT, base)
    if os.path.isdir(full):
        for d in os.listdir(full):
            PROJECT_DIRS.setdefault(d, os.path.join(full, d))

FILE_INDEX = {}
for base in ("src", "tests"):
    for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, base)):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for fn in filenames:
            if fn.endswith(".cs"):
                FILE_INDEX.setdefault(fn[:-3], []).append(os.path.join(dirpath, fn))


def resolves(token):
    token = token.rstrip("/")
    project, _, rest = token.partition("/")
    base = PROJECT_DIRS.get(project)
    if base is None:
        return False
    candidate = os.path.join(base, rest)
    if os.path.isdir(candidate) or os.path.isfile(candidate) or os.path.isfile(candidate + ".cs"):
        return True
    # `Installer.Core/SiteConfig/SiteConfigLoader` names a class: any file of that name under the project
    last = rest.split("/")[-1].removesuffix(".cs")
    for path in FILE_INDEX.get(last, []):
        if path.startswith(base + os.sep):
            return True
    return False


def main():
    text = open(TASKS, encoding="utf-8").read()
    dangling = []
    checked = 0
    for n, line in enumerate(text.split("\n"), 1):
        if "[x]" not in line:
            continue
        for token in PATH_TOKEN.findall(line):
            checked += 1
            if not resolves(token):
                dangling.append((n, token))
    print("tasks.md: %d path claims on [x] items checked" % checked)
    if dangling:
        print("\nDANGLING - an [x] item names code that does not exist:")
        for n, token in dangling:
            print("  line %d: `%s`" % (n, token))
        return 1
    print("OK - every [x] claim resolves.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
