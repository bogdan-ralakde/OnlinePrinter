#!/bin/sh
# Small safety net for container operators: the relay needs a writable data
# directory, and silently losing it would mean losing queued labels.
set -e

DATA_DIR="${LB_DATA:-/data}"

if [ ! -d "$DATA_DIR" ]; then
    echo "entrypoint: creating data directory $DATA_DIR"
    mkdir -p "$DATA_DIR"
fi

if [ ! -w "$DATA_DIR" ]; then
    echo "entrypoint: ERROR $DATA_DIR is not writable by uid $(id -u)." >&2
    echo "entrypoint: fix the volume permissions (e.g. chown -R 10001:10001 <host path>)." >&2
    exit 1
fi

# A named volume keeps its contents across upgrades; make sure the layout exists.
mkdir -p "$DATA_DIR/channels"

exec "$@"
