#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
if [[ ! -x .venv/bin/python ]]; then
  python3 -m venv .venv
fi
if [[ ! -f .venv/installed.txt ]]; then
  .venv/bin/python -m pip install -r requirements.txt
  touch .venv/installed.txt
fi
exec .venv/bin/python run.py "$@"
