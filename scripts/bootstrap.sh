#!/bin/sh
# TYVO setup check (macOS). Safe to run every time: it skips whatever is already in place.
#
#   sh bootstrap.sh ["<Unity project>"]
#
# 1. Finds Python 3.8+; installs it with Homebrew only if none is found.
# 2. With a project: runs `tyvo.py ensure`, which installs the Unity package only if it is missing or out of date.
# The last line printed is PYTHON=<path>: use that interpreter for tyvo.py.
set -e
HERE=$(cd "$(dirname "$0")" && pwd)

find_python() {
  for exe in python3 python /opt/homebrew/bin/python3 /usr/local/bin/python3; do
    if command -v "$exe" >/dev/null 2>&1; then
      out=$("$exe" -c 'import sys; print(sys.executable if sys.version_info >= (3, 8) else "")' 2>/dev/null || true)
      if [ -n "$out" ]; then echo "$out"; return 0; fi
    fi
  done
  return 1
}

if PY=$(find_python); then
  echo "TYVO: Python already installed: $PY"
else
  if ! command -v brew >/dev/null 2>&1; then
    echo "Python 3 is not installed and Homebrew is not available. Install Python 3 from https://www.python.org/downloads/ and run this again." >&2
    exit 1
  fi
  echo "TYVO: Python 3 not found, installing with Homebrew..."
  brew install python
  PY=$(find_python) || { echo "Python install finished but python3 was not found. Open a new terminal and run this again." >&2; exit 1; }
  echo "TYVO: installed $PY"
fi

if [ -n "$1" ]; then
  "$PY" "$HERE/tyvo.py" --project "$1" ensure
fi
echo "PYTHON=$PY"
