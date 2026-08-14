# syntax=docker/dockerfile:1
# Headless game server image (Stage 2, task T50).
#
# THE PARSER DIRECTIVE ABOVE IS LOAD-BEARING AND MUST STAY ON LINE 1. Any
# comment before it silences it, and this file uses two features the daemon's
# built-in frontend only has in recent versions -- the named context of
# `COPY --from=game` (dockerfile 1.4) and `COPY --chmod` (1.2). Naming the
# frontend keeps this file buildable on a machine whose Docker is older than
# this one's -- a colleague's box, a future CI runner. It does NOT cover the
# `--build-context` FLAG itself: that lives in the buildx CLI (>= 0.8), so
# build.sh (T51) is where the client version gets checked.
#
# It has a price: BuildKit resolves docker/dockerfile:1 from Docker Hub before
# it parses this file, and re-checks for updates on every build. A machine that
# reaches its base-image mirror but not docker.io -- air-gapped, or mirror-only
# -- now fails here, and a locally cached frontend is not a guaranteed
# fallback. The deployment path pays nothing: build.sh builds on the
# workstation (T51) and the LAN host only pulls the finished image (T52). An
# offline rebuild is unblocked with `--build-arg BUILDKIT_SYNTAX=<local ref>`.
#
# TWO BUILD CONTEXTS, ON PURPOSE. The Unity artifact is deliberately built
# outside the repository (RING_BUILD_ROOT, ~87 MB, never in git), while
# entrypoint.sh lives in the repository -- and COPY can only read from a
# context. So the main context is this directory (small: entrypoint.sh only,
# see .dockerignore) and the artifact arrives as the named context `game`.
# Run from the REPOSITORY ROOT -- both paths below are relative to it:
#
#   docker build -f client/docker/Dockerfile \
#     --build-context game="$RING_BUILD_ROOT/linux-server" \
#     -t ring-server:dev client/docker
#
# Named contexts need BuildKit, which is the default builder here. The artifact
# directory is the output of Ring.Editor.BuildCommands.BuildLinuxServer, whose
# subtarget is StandaloneBuildSubtarget.Server -- a headless dedicated-server
# player. Unity itself is never installed into the image.

FROM debian:12-slim

# Runtime dependencies. libc6, libgcc-s1 and zlib1g are measured, not guessed:
# `readelf -d` over every ELF file of the artifact yields these NEEDED entries --
#   libc6     -> libc.so.6, libm.so.6, librt.so.1, libdl.so.2, libpthread.so.0,
#                libanl.so.1 and the ld-linux-x86-64.so.2 interpreter;
#   libgcc-s1 -> libgcc_s.so.1 (RingServer, UnityPlayer.so, the Mono GC);
#   zlib1g    -> libz.so.1 (libmonobdwgc-2.0.so, libMonoPosixHelper.so).
# All three already ship in debian:12-slim as priority-required packages, but
# they are named here anyway: an implicit dependency of the base image is not a
# statement of what this application needs, and the base image may change.
#   libstdc++6      -> not linked by anything in today's artifact; kept because
#                      it is what the approved spec (3.13) lists, and because
#                      dlopen-time dependencies are invisible to readelf.
#   ca-certificates -> TLS trust roots, so that reporting a match result to the
#                      meta service (Stage 5) does not need a rebuild.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        libc6 \
        libgcc-s1 \
        libstdc++6 \
        zlib1g \
    && rm -rf /var/lib/apt/lists/*

# The game never runs as root. The uid/gid are fixed rather than left to
# useradd, so that a host handing the container a match config file through
# RING_MATCH_CONFIG can give it predictable ownership -- and that path is not
# hypothetical: it beats RING_MATCH_CONFIG_JSON in MatchConfigLoader, and a file
# the container cannot read is refused with exit code 2. `useradd` is the
# low-level tool from the passwd package; debian:12-slim carries `adduser` as
# well, and either would do. No login is ever performed inside this image,
# hence nologin.
RUN groupadd --gid 10001 ring \
    && useradd --uid 10001 --gid 10001 --create-home --home-dir /home/ring \
        --shell /usr/sbin/nologin ring

# Made explicit rather than inherited from the passwd entry: the Unity player
# resolves its per-user directories through HOME, and a container whose HOME
# points at an unwritable directory fails in a way that is hard to read.
ENV HOME=/home/ring

WORKDIR /app

# Targeted COPY, not a copy of the whole artifact directory. Two entries of the
# build output are deliberately left behind:
#   ring-client-new_BurstDebugInformation_DoNotShip/ - debug symbols, the name
#     states the intent;
#   libdecor-0.so.0, libdecor-cairo.so - Wayland window decorations. A
#     dedicated-server player draws nothing, and their own dependencies (cairo,
#     pango, wayland, dbus) are not installed, so shipping them would only
#     enlarge the image.
# Filtering here rather than in .dockerignore is what makes the result
# independent of who runs the build and from which directory.
#
# FishNet.SDK.Id is an empty marker file that FishNet's post-build step writes
# into every build; the package asks that it be kept (Runtime/Editor/
# BuildIdentifier.cs) and it costs nothing.
#
# Ownership stays root:root: the process runs as `ring` and has no business
# rewriting its own binaries. Modes come from the artifact, where RingServer is
# already executable and everything is world-readable.
COPY --from=game RingServer UnityPlayer.so FishNet.SDK.Id ./
COPY --from=game RingServer_Data/ ./RingServer_Data/

# The mode is set here instead of relying on the executable bit in git, so the
# image is correct whether or not that bit survived a checkout.
COPY --chmod=0755 entrypoint.sh ./entrypoint.sh

USER ring

# Documentation only -- EXPOSE publishes nothing. The port a match listens on
# comes from the match config (MatchConfig.Port) and is published explicitly at
# run time, `docker run -p <port>:<port>/udp`. The number below is not a new
# constant: it is the dev default in MatchConfigLoader (DevPort), the one the
# runbook already uses.
EXPOSE 7777/udp

# Exec form, so the entrypoint is PID 1 and receives signals; entrypoint.sh then
# hands PID 1 over to the player with `exec`.
#
# STOPSIGNAL is left at Docker's default (SIGTERM) on purpose. The Unity Linux
# player installs its own SIGTERM handler, tears the engine down cleanly and
# exits 0 -- so `docker stop` produces exit code 0, never 143, and the log, not
# the exit code, is what distinguishes a stopped container from a played match:
# a match the server itself ends always prints "ServerBootstrap: exiting with
# code N", and a SIGTERM shutdown never does.
ENTRYPOINT ["/app/entrypoint.sh"]
