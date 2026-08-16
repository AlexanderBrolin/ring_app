#!/bin/sh
# Entry point of the headless game server image (Stage 2, task T50).
#
# WHY exec: without it this shell would stay PID 1, `docker stop` would deliver
# SIGTERM to the shell instead of the player, the player would never see the
# signal, SIGKILL would arrive ten seconds later, the log would be truncated and
# the exit code lost. With exec the player IS PID 1 and shuts down cleanly.
#
# WHY -logFile -: a hyphen means "write the player log to the console" (Unity
# player command line arguments). Without the argument the Linux player writes
# to a file under HOME, and `docker logs` would be silent -- while every check of
# this image (the listening line, match-start, "player accepted") reads the log.
#
# WHAT THIS SCRIPT DOES NOT DO. It does not read, validate or rewrite the match
# configuration: the process itself reads RING_MATCH_CONFIG (a file path, which
# wins) and RING_MATCH_CONFIG_JSON (a body) through MatchConfigLoader, and exec
# preserves the environment, so both arrive untouched. It also knows nothing
# about the port: the port lives in the match config, and a default here would
# be a second copy of a number that already exists in the code.

set -eu

# Run from the directory holding this script, so the image still works if the
# caller overrides the working directory (docker run -w ...).
cd "$(dirname "$0")"

# Anything passed after the image name is appended, which is how extra player
# arguments (for example -timestamps) reach the build without an image rebuild.
exec ./RingServer -logFile - "$@"
