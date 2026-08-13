#!/bin/sh
set -e

# The shared SQLite volume (/data) is created root-owned by default when Docker first mounts
# it, but the app runs as non-root "appuser" - fix ownership here (while still root) before
# dropping privileges, so InMemory-mode containers (no /data mount) are unaffected.
if [ -d /data ]; then
  chown -R appuser:appgroup /data
fi

exec su-exec appuser dotnet FinMonitor.Api.dll
