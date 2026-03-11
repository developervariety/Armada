#!/usr/bin/env bash
set -e
armada server stop
./reinstall-tool.sh
armada server start
