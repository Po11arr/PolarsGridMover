#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

rm -rf "$SCRIPT_DIR/PolarsGridMover/bin" "$SCRIPT_DIR/PolarsGridMover/obj"
