#!/usr/bin/env python3
"""Install the TYVO skill (Custom font maker by Ali) for Claude Code on this machine (Windows / macOS).

  python install_skill.py          # copy this folder to ~/.claude/skills/tyvo
  python install_skill.py --link   # link instead of copy (edits in this folder are picked up live)
"""
import os
import platform
import shutil
import subprocess
import sys

SRC = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DST = os.path.join(os.path.expanduser("~"), ".claude", "skills", "tyvo")


def main():
    link = "--link" in sys.argv
    os.makedirs(os.path.dirname(DST), exist_ok=True)
    if os.path.lexists(DST):
        if os.path.realpath(DST) == os.path.realpath(SRC):
            print("Already installed (points to this folder):", DST)
            return
        is_junction = getattr(os.path, "isjunction", lambda p: False)(DST)
        if os.path.islink(DST):
            os.unlink(DST)
        elif is_junction:
            os.rmdir(DST)  # removes the junction only, never the target
        else:
            shutil.rmtree(DST)  # an older copied install
    if link:
        if platform.system() == "Windows":
            subprocess.run(["cmd", "/c", "mklink", "/J", DST, SRC], check=True)  # junction: no admin rights needed
        else:
            os.symlink(SRC, DST)
    else:
        shutil.copytree(SRC, DST, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    print(("Linked " if link else "Copied ") + SRC + " -> " + DST)
    print("Restart Claude Code (or open a new session) to load the skill.")


if __name__ == "__main__":
    main()
