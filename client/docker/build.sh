#!/usr/bin/env bash
#
# Build and publish the headless game server image (Stage 2, task T51).
#
# FOUR STEPS, IN THIS ORDER: the Unity dedicated-server player, a check of the
# artifact root against the inventory this script is PAIRED WITH (which is a
# superset of what the Dockerfile copies -- see ARTIFACT_ROOT_ENTRIES), the
# image itself with two tags, and a push of both tags unless --no-push was
# given. Everything that can fail cheaply -- docker client, docker daemon, build
# root, dirty tree, Unity binary -- is checked BEFORE the Unity build, which is
# the only slow step here: discovering a missing buildx after ten minutes of
# Unity would be the script's fault, not the operator's.
#
# RE-RUNNABLE BY CONSTRUCTION. Every step overwrites its own output on a fixed
# path -- the artifact directory, the build log, the two tags -- so a second run
# leaves the same state as the first and nothing accumulates. There is no
# "clean" step on purpose: `rm -rf` aimed at a directory that an environment
# variable names is the classic way to lose the wrong directory, and a stale
# entry left in the artifact root by an older build is precisely what the
# artifact-root check below is for.
#
# IT NEVER AUTHENTICATES AND NEVER CREATES A REPOSITORY. `docker login` is the
# operator's business and what it stores stays in the machine's docker
# credential store: this script neither reads it, nor prints it, nor asks for
# it, and a push without a login fails with docker's own message, which is the
# right message. Docker Hub also creates a MISSING repository as PUBLIC on first
# push, while the approved spec (3.13, decision C21) requires a private one --
# so whether the target exists, and how its visibility is set, is a decision
# taken by a human before the first push. The script only reminds.
#
# THE BUILD COMMAND LIVES HERE, AND ONLY HERE. What the Dockerfile header shows
# is the bare shape of the two-context invocation, for a reader who has to
# understand the file without running anything; the real call below differs from
# it -- `buildx build --load`, two tags, the provenance labels, the repository
# from RING_IMAGE_REPO -- and is the one that is actually used.
#
# NO TIMEOUT AROUND UNITY, ON PURPOSE. A build that also switches the active
# build target takes far longer than a plain one, and a timeout tuned for the
# plain case would kill exactly the run that needed the time. Sending the script
# to the background is the caller's business, not the script's.

set -euo pipefail

readonly PROG="${BASH_SOURCE[0]##*/}"

# Docker Hub repository the two tags are built and pushed to. `<user>/
# ring-server` per spec 3.13; the user is the account `docker login` was run
# with on this workstation. One knob rather than user + name separately, so the
# image name has a single home.
readonly DEFAULT_IMAGE_REPO='brolin/ring-server'

# The moving tag the LAN host and the local smoke run pull. The other tag is the
# commit sha and is computed below.
readonly DEV_TAG='dev'

# The Unity entry point and the directory it writes into. Both are properties of
# `Ring.Editor.BuildCommands.BuildLinuxServer`, which builds to
# "linux-server/RingServer" under RING_BUILD_ROOT -- lower case with a hyphen,
# which is not what the plan's runbook says (errata E-1 of the phase).
readonly UNITY_METHOD='Ring.Editor.BuildCommands.BuildLinuxServer'
readonly ARTIFACT_SUBDIR='linux-server'

# The artifact root as it is supposed to look, and the reason this list exists:
# the Dockerfile copies root files BY NAME (`COPY --from=game RingServer
# UnityPlayer.so FishNet.SDK.Id ./`), which keeps the image independent of any
# ignore file but also means a NEW mandatory file next to the executable -- a
# native plugin shipped by a future package, for instance -- would be left out
# silently and only surface as a runtime failure on the LAN host.
#
# THIS LIST IS PAIRED WITH THE COPY INSTRUCTIONS IN Dockerfile: when the build
# output legitimately changes, both are updated together, and the decision of
# what belongs in the image is taken while looking at the diff rather than at a
# crash.
readonly ARTIFACT_ROOT_ENTRIES=(
    'FishNet.SDK.Id'
    'RingServer'
    'RingServer_Data'
    'UnityPlayer.so'
    'libdecor-0.so.0'
    'libdecor-cairo.so'
    'ring-client-new_BurstDebugInformation_DoNotShip'
)

info() { printf '%s: %s\n' "$PROG" "$*"; }
warn() { printf '%s: warning: %s\n' "$PROG" "$*" >&2; }

die() {
    printf '%s: error: %s\n' "$PROG" "$1" >&2
    shift
    local line
    for line in "$@"; do
        printf '  %s\n' "$line" >&2
    done
    exit 1
}

usage() {
    cat <<EOF
Usage: client/docker/$PROG [--no-push]

Builds the Linux headless server player with Unity, packs it into the image
described by client/docker/Dockerfile under two tags -- <commit sha> and
'$DEV_TAG' -- prints the image size and id, and pushes both tags.

Options:
  --no-push   do everything except the push (full local run)
  -h, --help  this text

Environment:
  UNITY            Unity editor binary. Default: derived from the version in
                   client/ProjectSettings/ProjectVersion.txt, under the Unity
                   Hub's default install root in \$HOME.
  RING_BUILD_ROOT  where Unity writes the player and this script writes its
                   build log. Default: a 'ring/builds' directory under
                   \${XDG_CACHE_HOME:-\$HOME/.cache}. Must be outside every git
                   working tree: the artifact is ~87 MB of binaries and never
                   belongs in the repository.
  RING_IMAGE_REPO  image repository. Default: $DEFAULT_IMAGE_REPO.

Authentication is not this script's business: run \`docker login\` yourself.
EOF
}

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------

push=1
while (( $# > 0 )); do
    case "$1" in
        --no-push) push=0 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown argument: '$1'" "run with --help for usage" ;;
    esac
    shift
done

# ---------------------------------------------------------------------------
# Repository, commit, tags
# ---------------------------------------------------------------------------

# The repository root is found from the location of this file, not from the
# current directory, so the script works when called from anywhere.
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
repo_root=$(git -C "$script_dir" rev-parse --show-toplevel) \
    || die "cannot locate the repository root from '$script_dir'"
readonly script_dir repo_root

readonly docker_dir="$repo_root/client/docker"
readonly project_dir="$repo_root/client"

image_repo="${RING_IMAGE_REPO:-$DEFAULT_IMAGE_REPO}"
readonly image_repo

git_sha=$(git -C "$repo_root" rev-parse HEAD)
git_sha_short=$(git -C "$repo_root" rev-parse --short HEAD)
readonly git_sha git_sha_short

# A dirty tree is a tree the sha tag would misname: the image would carry a
# commit id it does not correspond to. The suffix is the chosen answer rather
# than a refusal, because this phase's development cycle runs images before it
# commits them -- but such an image is never published, see the guard below.
# The check covers the whole working tree, tracked and untracked alike: this
# repository keeps its own scratch (.superpowers) behind a nested .gitignore, so
# a clean tree really does print nothing here.
#
# THE STATUS CALL IS CHECKED, NOT JUST READ. A bare `[[ -n "$(git status)" ]]`
# discards git's exit code, so a git that failed for any reason -- a broken
# index, a lock held by another process -- would produce empty output and read
# as "clean", which is the one direction this guard must never fail in: it would
# let a dirty tree be published under a commit's name.
version_tag="$git_sha_short"
dirty=0
git_status=$(git -C "$repo_root" status --porcelain) \
    || die "git status failed in '$repo_root'" \
           "The dirty-tree guard cannot be evaluated, and an unevaluated guard" \
           "is not a passed one."
if [[ -n "$git_status" ]]; then
    dirty=1
    version_tag="$git_sha_short-dirty"
fi
readonly git_status
readonly version_tag dirty

readonly ref_version="$image_repo:$version_tag"
readonly ref_dev="$image_repo:$DEV_TAG"

if (( dirty )); then
    if (( push )); then
        die "the working tree is dirty, and a dirty image is never published" \
            "The '$git_sha_short' tag would name a commit this image is not built from." \
            "Commit or stash first, or re-run with --no-push to get a local" \
            "image tagged '$version_tag'."
    fi
    warn "the working tree is dirty: tagging '$version_tag', which is a local-only tag"
fi

# ---------------------------------------------------------------------------
# Build root: outside git, always
# ---------------------------------------------------------------------------

if [[ -n "${RING_BUILD_ROOT:-}" ]]; then
    build_root_input="$RING_BUILD_ROOT"
else
    cache_home="${XDG_CACHE_HOME:-}"
    [[ -n "$cache_home" ]] \
        || cache_home="${HOME:?HOME is not set; set RING_BUILD_ROOT instead}/.cache"
    build_root_input="$cache_home/ring/builds"
fi
# -m resolves a path that does not exist yet; the artifact directory is created
# by Unity, not here.
build_root=$(realpath -m -- "$build_root_input")
readonly build_root_input build_root
readonly artifact_dir="$build_root/$ARTIFACT_SUBDIR"
readonly unity_log="$build_root/build-sh-$ARTIFACT_SUBDIR.log"

# The nearest existing ancestor decides: if THAT is inside a working tree, so is
# everything this script would create under it.
probe="$build_root"
while [[ ! -d "$probe" ]]; do
    parent=$(dirname -- "$probe")
    [[ "$parent" != "$probe" ]] || break
    probe="$parent"
done
if enclosing_tree=$(git -C "$probe" rev-parse --show-toplevel 2>/dev/null); then
    die "the build root is inside a git working tree: '$build_root'" \
        "The tree is '$enclosing_tree'. The player is ~87 MB of binaries that" \
        "must never reach a repository, not even as untracked files that the" \
        "next 'git add -A' would sweep in. Point RING_BUILD_ROOT somewhere else."
fi

# ---------------------------------------------------------------------------
# Unity
# ---------------------------------------------------------------------------

# The editor version has exactly one home in this project, ProjectVersion.txt,
# and both the path to the binary and the label on the image are derived from it
# instead of being written down a second time. The binary is never asked for its
# own version: an editor invoked with an argument it does not recognize opens
# the project instead of answering, and a script that can hang is worse than one
# that reads a file. UNITY wins when set -- that is for a Hub kept somewhere
# else, not for a different editor version, which would silently upgrade the
# project on first open.
version_file="$project_dir/ProjectSettings/ProjectVersion.txt"
[[ -f "$version_file" ]] || die "no editor version file at '$version_file'"
unity_version=$(sed -n 's/^m_EditorVersion: //p' "$version_file")
[[ -n "$unity_version" ]] || die "no 'm_EditorVersion:' line in '$version_file'"
if [[ -n "${UNITY:-}" ]]; then
    unity="$UNITY"
else
    hub_root="${HOME:?HOME is not set; set UNITY instead}/Unity/Hub/Editor"
    unity="$hub_root/$unity_version/Editor/Unity"
fi
readonly version_file unity_version unity

[[ -x "$unity" ]] || die "no Unity editor binary at '$unity'" \
    "Set UNITY=<path to the editor binary> if the Hub keeps it elsewhere."

# ---------------------------------------------------------------------------
# Docker: the client's flags, then the daemon behind it
# ---------------------------------------------------------------------------

# `--build-context` is a flag of the buildx CLI (>= 0.8), and the Dockerfile's
# `# syntax=` directive does NOT cover it: that directive pins the frontend the
# daemon parses the file with, while the flag has to be understood by the client
# before the daemon sees anything. So a machine with an old client fails here,
# with a sentence, instead of failing inside docker's argument parser.
#
# The capability is probed rather than computed from a version number: the help
# output of the very command this script runs is the authority on which flags
# that command accepts.
command -v docker >/dev/null 2>&1 || die "docker is not installed"
buildx_version=$(docker buildx version 2>/dev/null) \
    || die "docker buildx is not available" \
           "The image needs 'docker buildx build --build-context' (buildx >= 0.8)."
# The help text is captured whole and matched in the shell rather than piped
# into grep: `grep -q` closes the pipe on the first match, and under `set -o
# pipefail` a producer killed by SIGPIPE would turn a supported client into a
# failed check.
buildx_help=$(docker buildx build --help 2>/dev/null) || buildx_help=''
if [[ "$buildx_help" != *'--build-context'* ]]; then
    die "this docker client does not support --build-context" \
        "Found: $buildx_version. The image needs buildx >= 0.8, because the" \
        "Unity artifact is passed in as a second, named build context, and no" \
        "'# syntax=' directive in the Dockerfile can add a flag to the client."
fi
readonly buildx_version buildx_help

# THE DAEMON IS ASKED SEPARATELY, BECAUSE THE CLIENT ANSWERS WITHOUT IT.
# `buildx version` and `buildx build --help` are local: they print happily on a
# machine where the daemon is down or the user is not in the docker group. Both
# of those are ordinary states on a fresh host, and without this check they
# would surface only at the image step -- that is, after the Unity build, which
# is the one thing this script promises not to make anybody wait for in vain.
docker_server=$(docker version --format '{{.Server.Version}}' 2>/dev/null) \
    || die "the docker daemon does not answer" \
           "The client is fine ($buildx_version), the server is not reachable." \
           "Usual causes: the daemon is not running, or this user is not in the" \
           "'docker' group."
readonly docker_server

# ---------------------------------------------------------------------------
# Step 1: Unity build
# ---------------------------------------------------------------------------

# Plain branches rather than a conditional inside the message: a `(( ))` test
# that evaluates to zero is a failed command, and under `set -e` that is a way
# to end a run over a cosmetic word.
if (( dirty )); then commit_note="$git_sha_short (dirty)"; else commit_note="$git_sha_short"; fi
if (( push )); then push_note='yes'; else push_note='no (--no-push)'; fi

info "repository: $repo_root"
info "commit:     $commit_note"
info "build root: $build_root"
info "unity:      $unity ($unity_version)"
info "docker:     $buildx_version (daemon $docker_server)"
info "tags:       $ref_version, $ref_dev"
info "push:       $push_note"

mkdir -p -- "$build_root"

info "Unity build ($UNITY_METHOD) -- THIS IS THE LONG STEP: minutes on a warm"
info "  Library, considerably longer when the active build target has to switch"
info "  to Linux dedicated server first. No progress reaches this terminal;"
info "  follow it with: tail -f '$unity_log'"

unity_status=0
RING_BUILD_ROOT="$build_root" "$unity" \
    -batchmode -quit \
    -projectPath "$project_dir" \
    -executeMethod "$UNITY_METHOD" \
    -logFile "$unity_log" || unity_status=$?

# The exit code is a trustworthy signal here and is treated as one: every
# failure inside BuildCommands is reported by throwing, and an unhandled
# exception in -executeMethod makes Unity exit 1. Walking on with a stale
# artifact would produce an image that looks fresh and is not.
if (( unity_status != 0 )); then
    warn "Unity exited with code $unity_status; last 40 lines of the log follow"
    tail -n 40 -- "$unity_log" >&2 || true
    die "the Unity build failed (exit $unity_status)" \
        "Full log: $unity_log" \
        "A locked project (the editor is open on it) and a compile error both" \
        "land here, and the log says which."
fi

info "Unity build finished; artifact: $artifact_dir"

# ---------------------------------------------------------------------------
# Step 2: the artifact root is still the one the COPY list was written against
# ---------------------------------------------------------------------------

[[ -d "$artifact_dir" ]] || die "the Unity build reported success but left no '$artifact_dir'"

artifact_actual=$(find "$artifact_dir" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)
artifact_expected=$(printf '%s\n' "${ARTIFACT_ROOT_ENTRIES[@]}" | LC_ALL=C sort)

if [[ "$artifact_actual" != "$artifact_expected" ]]; then
    # comm compares by the collating order of the locale it runs in, and both
    # sides above were sorted under C: it has to be told the same.
    unexpected=$(LC_ALL=C comm -13 <(printf '%s\n' "$artifact_expected") \
        <(printf '%s\n' "$artifact_actual"))
    missing=$(LC_ALL=C comm -23 <(printf '%s\n' "$artifact_expected") \
        <(printf '%s\n' "$artifact_actual"))
    if [[ -n "$unexpected" ]]; then
        printf '%s: new in %s:\n' "$PROG" "$artifact_dir" >&2
        while IFS= read -r entry; do printf '    %s\n' "$entry" >&2; done <<<"$unexpected"
    fi
    if [[ -n "$missing" ]]; then
        printf '%s: gone from %s:\n' "$PROG" "$artifact_dir" >&2
        while IFS= read -r entry; do printf '    %s\n' "$entry" >&2; done <<<"$missing"
    fi
    die "artifact root changed -- revisit the COPY list in Dockerfile" \
        "A new file here is copied into the image only if the Dockerfile names" \
        "it, and a file that is needed but not named fails at run time on the" \
        "host, not here. Decide what each entry is, then update BOTH the COPY" \
        "instructions in client/docker/Dockerfile and ARTIFACT_ROOT_ENTRIES in" \
        "this script."
fi

# The count is of the INVENTORY, not of what ships: the Dockerfile names a
# subset of these entries in its COPY instructions and leaves the rest behind
# on purpose (the Burst debug directory and the two libdecor libraries). What
# this check proves is that the build output is still the one that list was
# written against -- nothing new appeared next to the executable unnoticed.
info "artifact root unchanged: ${#ARTIFACT_ROOT_ENTRIES[@]} known entries, none new, none gone"

# ---------------------------------------------------------------------------
# Step 3: the image
# ---------------------------------------------------------------------------

# TWO CONTEXTS. The main context is client/docker (entrypoint.sh, filtered by
# the .dockerignore that sits there), and the artifact arrives as the named
# context `game`, which is what `COPY --from=game` reads. The single-context
# form cannot work: COPY reads only from a context, and the two sources live on
# opposite sides of the repository boundary.
#
# --load puts the result in the local image store. With the default docker
# driver that is what happens anyway; with a container driver it is what makes
# the difference between an image the next line can measure and push, and a
# build that evaporates.
#
# LABELS, not Dockerfile instructions, because everything they record is known
# here and nowhere else. Without them nothing inside a running container ties it
# back to a commit. `revision` and `version` are the OCI names for exactly this
# pair -- the exact commit, and the human tag (which is where '-dirty' shows up
# as a fact rather than as a string in a tag). `created` is deliberately absent:
# the image config already carries its own creation time, and a label repeating
# it would be a second home for one fact.
labels=(
    --label "org.opencontainers.image.revision=$git_sha"
    --label "org.opencontainers.image.version=$version_tag"
    --label "ring.build.unity-version=$unity_version"
    --label "ring.build.tool=client/docker/$PROG"
)

# The origin URL, when there is one, is the only way the LAN host can learn
# where the image came from. Any user info in front of the host is stripped: an
# https remote can carry a credential in that position, and a label is not a
# place for one.
#
# THE CLASS IS `[^/]*`, NOT `[^/@]*`, AND THE DIFFERENCE IS A LEAKED PASSWORD.
# A password may itself contain '@' -- it is percent-encoded by convention, not
# by rule -- and a class that stops at the first one strips only up to it:
# `https://user:p@ssw0rd@host/r.git` would have become `https://ssw0rd@host/r.git`,
# publishing the tail of the secret in a label that then goes to the registry.
# Matching greedily up to the LAST '@' before the path removes the whole
# userinfo. A remote without userinfo has no '@' before its first '/', so the
# pattern does not match and the URL is left exactly as it is.
if origin_url=$(git -C "$repo_root" remote get-url origin 2>/dev/null); then
    origin_url=$(printf '%s' "$origin_url" | sed -E 's#^([a-zA-Z][a-zA-Z0-9+.-]*://)[^/]*@#\1#')
    labels+=(--label "org.opencontainers.image.source=$origin_url")
fi

info "docker build (context: $docker_dir, game: $artifact_dir)"
docker buildx build \
    --load \
    --file "$docker_dir/Dockerfile" \
    --build-context "game=$artifact_dir" \
    --tag "$ref_version" \
    --tag "$ref_dev" \
    "${labels[@]}" \
    "$docker_dir"

# ---------------------------------------------------------------------------
# Step 4: size, id, push
# ---------------------------------------------------------------------------

image_meta=$(docker image inspect --format '{{.Id}} {{.Size}}' "$ref_version")
image_id="${image_meta%% *}"
size_bytes="${image_meta##* }"
# Docker's own formatting rather than arithmetic of ours; the byte count stays
# next to it because that is the number a size budget is argued with. The first
# line is taken in the shell, for the same SIGPIPE reason as the buildx help.
size_human=$(docker image ls --format '{{.Size}}' "$ref_version")
size_human="${size_human%%$'\n'*}"

info "image id:   $image_id"
info "image size: $size_human ($size_bytes bytes)"
info "tags:       $ref_version, $ref_dev"

if (( ! push )); then
    info "not pushing (--no-push)"
    exit 0
fi

warn "pushing to '$image_repo' -- it must already exist and be PRIVATE:" \
     "Docker Hub creates a missing repository as public, and the spec asks" \
     "for a private one (3.13, C21)."

for tag in "$version_tag" "$DEV_TAG"; do
    info "pushing $image_repo:$tag"
    docker push "$image_repo:$tag"
done

# What the host pulls by is the registry digest, not the local image id, and
# after a successful push docker knows it. Printing it here is what makes the
# deploy on the far side verifiable instead of trusting a moving tag.
digest_format='{{range .RepoDigests}}{{println .}}{{end}}'
digest_lines=$(docker image inspect --format "$digest_format" "$ref_version")
while IFS= read -r digest_line; do
    [[ -n "$digest_line" ]] || continue
    info "pull by digest: $digest_line"
done <<<"$digest_lines"

info "done"
