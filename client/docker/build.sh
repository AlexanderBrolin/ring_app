#!/usr/bin/env bash
#
# Build and publish the headless game server image (Stage 2, task T51).
#
# FOUR STEPS, IN THIS ORDER: the Unity dedicated-server player, a check of the
# artifact -- its root against the inventory this script is PAIRED WITH (which
# is a superset of what the Dockerfile copies -- see ARTIFACT_ROOT_REQUIRED
# and ARTIFACT_ROOT_OPTIONAL) plus the compiled burst library by name (see
# BURST_PLAYER_PLUGIN) -- the image itself with two tags, and a push of both
# tags unless --no-push was given. Everything that can fail cheaply -- docker
# client, docker daemon, build root, dirty tree, Unity binary -- is checked
# BEFORE the Unity build, which is the only slow step here: discovering a
# missing buildx after ten minutes of Unity would be the script's fault, not
# the operator's.
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
# TWO IMAGES, NOT TWO TAGS OF ONE (--dev, app-88jb Т35). The lag gate of
# Critical Rule 7 needs a server that CARRIES the latency simulator, and the
# release target does not: everything under DEVELOPMENT_BUILD is cut by the
# preprocessor there, so neither the simulator, nor the -ring-latency switch,
# nor the dev overlay exists in it (bd app-per9). --dev therefore swaps the
# Unity entry point and the artifact directory for their Development twins and
# builds into a repository of its OWN, '<repo>-dev'.
#
# IT IS A SEPARATE REPOSITORY BECAUSE THE TAG WAS ALREADY TAKEN. DEV_TAG below
# is the moving tag of the RELEASE server -- the one the LAN host pulls -- so a
# dev image published as ':dev' of the same repository would silently replace
# the server the host runs. The flag is spelled --dev rather than --target for
# the same class of reason: `docker build --target` owns that word.
#
# THE DOCKERFILE IS UNCHANGED BY IT. Its COPY list names root files one by one
# and the entry point already appends "$@" to the player, so the extra player
# arguments the gate needs (-ring-latency 80 5) reach the process at `docker
# run` time without a second Dockerfile. What the Development player DOES add
# to the artifact root is two files of separated debug symbols, and those are
# handled by the inventory below, not by a COPY.
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
#
# ⚠ IT IS A TAG, NOT A BUILD KIND, AND THE TWO ARE NOT THE SAME THING. This tag
# names the RELEASE server the LAN host pulls; a Development build of the server
# is a different IMAGE (see --dev and image_repo below), and it carries its own
# ':dev' inside its own repository. Nothing here ever rewrites
# '<release repo>:dev'.
readonly DEV_TAG='dev'

# The Unity entry point and the directory it writes into. Both are properties of
# `Ring.Editor.BuildCommands.BuildLinuxServer`, which builds to
# "linux-server/RingServer" under RING_BUILD_ROOT -- lower case with a hyphen,
# which is not what the plan's runbook says (errata E-1 of the phase).
#
# NOT `readonly`, AND THAT IS THE ONE REASON THEY ARE NOT: --dev replaces BOTH
# with the Development twins declared right below, and it can only do that after
# the arguments have been read. They are frozen together, once, immediately
# after the argument loop -- so everything downstream still reads a constant,
# and the artifact directory and the build log, which are derived from
# ARTIFACT_SUBDIR further down, follow the choice by themselves.
UNITY_METHOD='Ring.Editor.BuildCommands.BuildLinuxServer'
ARTIFACT_SUBDIR='linux-server'

# The Development twins --dev swaps in. They are a PAIR and are written here as
# one: `BuildLinuxServerDev` builds to "linux-server-dev/RingServer" (Editor/
# BuildCommands.cs), and a flag that moved one without the other would pack the
# release artifact into an image named for the dev one.
readonly DEV_UNITY_METHOD='Ring.Editor.BuildCommands.BuildLinuxServerDev'
readonly DEV_ARTIFACT_SUBDIR='linux-server-dev'

# Suffix the dev image's repository name carries, so the release repository is
# untouchable from this flag.
readonly DEV_REPO_SUFFIX='-dev'

# The artifact root as it is supposed to look, and the reason this inventory
# exists: the Dockerfile copies root files BY NAME (`COPY --from=game
# RingServer UnityPlayer.so FishNet.SDK.Id ./`), which keeps the image
# independent of any ignore file but also means a NEW mandatory file next to
# the executable -- a native plugin shipped by a future package, for instance
# -- would be left out silently and only surface as a runtime failure on the
# LAN host.
#
# THIS INVENTORY IS PAIRED WITH THE COPY INSTRUCTIONS IN Dockerfile: when the
# build output legitimately changes, both are updated together, and the
# decision of what belongs in the image is taken while looking at the diff
# rather than at a crash.
#
# IT COMES IN TWO CLASSES, because "the build output changed" and "this
# machine's build cache was warm" are different events and only the first one
# is worth stopping a build over. An entry whose absence proves nothing about
# the artifact belongs below, not here.
readonly ARTIFACT_ROOT_REQUIRED=(
    'FishNet.SDK.Id'
    'RingServer'
    'RingServer_Data'
    'UnityPlayer.so'
    'libdecor-0.so.0'
    'libdecor-cairo.so'
)

# ENTRIES THAT MAY OR MAY NOT BE THERE AND NEVER SHIP. Burst writes
# '<product>_BurstDebugInformation_DoNotShip' from its post-build step and
# from nowhere else -- `CreateFolderForMiscFiles` and `CollateMiscFiles`, both
# inside `BurstAotCompiler.OnPostBuildPlayerScriptDLLsImpl`
# (com.unity.burst@1.8.30, Editor/BurstAotCompiler.cs:737 and :971; the folder
# name is assembled in Editor/BurstPlatformAotSettings.cs:339-342). An
# incremental player build that reuses the already linked
# lib_burst_generated.so out of Library/Bee/artifacts/LinuxPlayerBuildProgram/
# AsyncPluginsFromLinker never runs that step, so the folder is never written.
#
# MEASURED, NOT ASSUMED (bd app-23p): two builds of commit 0e48049 twenty-
# seven minutes apart, the first with the folder and the second without, both
# shipping a byte-identical lib_burst_generated.so inside RingServer_Data
# (md5 f28582db014ce9f542cb91bf9d9f1232). Its absence therefore says something
# about the build cache and nothing about the artifact; the Dockerfile does
# not copy it in either case.
#
# IT IS LISTED RATHER THAN DROPPED, and that is the whole point of the second
# class: a name nobody knows about is an alarm, so an entry deleted from the
# inventory would start failing builds on the days it IS produced.
#
# THE TWO DEBUG-SYMBOL FILES BELOW ARE THE DEVELOPMENT PLAYER'S, and they are
# OPTIONAL rather than REQUIRED for the same reason the folder above is: the
# RELEASE artifact does not contain them, and requiring them would break the
# production path on the first release build (app-88jb Т35).
#
# MEASURED, NOT ASSUMED, and the plan's own guess was wrong about the names.
# Т35's finding A-I11 expected 'RingServer_BurstDebugInformation_DoNotShip' and
# a wildcard; the two artifacts of commit 3a477f9 say otherwise. Release root
# and dev root differ by EXACTLY these two entries -- the Burst folder is named
# after the PRODUCT ('ring-client-new'), is present in both, and has been
# inventoried since Stage 2. Both are unstripped ELF objects holding separated
# debug info, they sit only in the root (nothing matching deeper), and they are
# 5,888 B and 23,115,168 B; the dev CLIENT artifact shows the same shape with
# 'Ring_s.debug', so the pattern is '<binary>_s.debug' and not a server quirk.
#
# THEY DO NOT SHIP, AND THE DOCKERFILE NEEDED NO EDIT TO KEEP THEM OUT: its
# COPY list names root files one by one, so an entry it does not name is left
# behind by construction. That is the decision this inventory's header asks to
# be taken while looking at the diff -- 23 MB of native debug info is of no use
# to a headless container that carries no debugger, and the image the lag gate
# runs must differ from the release one by the PLAYER, not by its baggage.
readonly ARTIFACT_ROOT_OPTIONAL=(
    'RingServer_s.debug'
    'UnityPlayer_s.debug'
    'ring-client-new_BurstDebugInformation_DoNotShip'
)

# WHAT BURST ACTUALLY SHIPS, checked separately and required -- this is the
# guarantee the inventory above deliberately gives up, put back in its honest
# place. The debug folder is a byproduct of a compile that may or may not have
# run today; this library is the compiled code itself, and it is in the
# artifact either way (the incremental build that skipped the debug folder
# still shipped it byte for byte -- bd app-23p).
#
# THE STATE IT CATCHES IS BURST BEING OFF. `OnPostBuildPlayerScriptDLLsImpl`
# returns at BurstAotCompiler.cs:606-609 when a platform's
# `EnableBurstCompilation` says so -- BEFORE it creates the folder at :737 and
# before any bcl call; `ForceDisableBurstCompilation` cuts the same pipeline
# off even earlier (:141 and :175 under the plugin-generation API this project
# builds with, :200 on the legacy path), with the same outcome. Either way the
# player carries no burst code at all and falls back to managed. On an
# authoritative server that is a performance and float-behavior change nobody
# asked for, and it must stop a build rather than reach the LAN host. If a
# legitimate change ever removes the last burst-compiled job, this line is the
# place where that decision gets taken deliberately.
readonly BURST_PLAYER_PLUGIN='RingServer_Data/Plugins/lib_burst_generated.so'

info() { printf '%s: %s\n' "$PROG" "$*"; }
warn() { printf '%s: warning: %s\n' "$PROG" "$*" >&2; }

# One name per line, in C order, and NOTHING AT ALL for an empty set. The
# straight `printf '%s\n' "${array[@]}"` applies its format once even with no
# arguments left, so an emptied list would feed `comm` a single blank line --
# that is, an entry named "" -- and the comparison would report a phantom.
lines_of() {
    (( $# > 0 )) || return 0
    printf '%s\n' "$@" | LC_ALL=C sort
}

# The same guarantee for a captured block: `$(...)` strips trailing newlines,
# so an empty capture must print nothing rather than one blank line.
lines_in() {
    [[ -n "$1" ]] || return 0
    printf '%s\n' "$1"
}

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
Usage: client/docker/$PROG [--dev] [--no-push]

Builds the Linux headless server player with Unity, packs it into the image
described by client/docker/Dockerfile under two tags -- <commit sha> and
'$DEV_TAG' -- prints the image size and id, and pushes both tags.

Options:
  --dev       build the DEVELOPMENT server player ($DEV_UNITY_METHOD)
              and pack it into a repository of its own, '<repo>$DEV_REPO_SUFFIX'.
              That player is the one carrying the latency simulator and the
              -ring-latency switch the lag gate of Critical Rule 7 needs; the
              release repository and its '$DEV_TAG' tag are never touched.
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
# EMPTY, NOT ZERO, and the difference is deliberate: this variable is read as
# `${dev:+...}` when the image name is assembled, and it is never fed to
# `(( ))` -- an arithmetic test that evaluates to zero is a failed command, and
# under `set -e` that is a way to end a run over a cosmetic word (the same trap
# the note above `commit_note` warns about).
dev=''
while (( $# > 0 )); do
    case "$1" in
        --dev) dev=1 ;;
        --no-push) push=0 ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown argument: '$1'" "run with --help for usage" ;;
    esac
    shift
done
readonly push dev

# The pair moves together or not at all, and it is frozen the moment it is
# settled: from here down the script reads two constants, exactly as it did
# before the flag existed.
if [[ -n "$dev" ]]; then
    UNITY_METHOD="$DEV_UNITY_METHOD"
    ARTIFACT_SUBDIR="$DEV_ARTIFACT_SUBDIR"
fi
readonly UNITY_METHOD ARTIFACT_SUBDIR

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

# The suffix is appended to whatever the base name turned out to be, RING_
# IMAGE_REPO included: the point of the flag is that a Development player never
# lands in the repository a release player lands in, and an operator who
# redirected the base name did not thereby ask for the two to share one.
image_repo="${RING_IMAGE_REPO:-$DEFAULT_IMAGE_REPO}${dev:+$DEV_REPO_SUFFIX}"
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
if [[ -n "$dev" ]]; then build_note='DEVELOPMENT (--dev)'; else build_note='release'; fi
readonly build_note

info "build kind: $build_note"
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
# Step 2: the artifact is still the one the COPY list was written against --
#         its root by inventory, and the burst library by name
# ---------------------------------------------------------------------------

[[ -d "$artifact_dir" ]] || die "the Unity build reported success but left no '$artifact_dir'"

artifact_actual=$(find "$artifact_dir" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort)
artifact_required=$(lines_of "${ARTIFACT_ROOT_REQUIRED[@]}")
artifact_known=$(lines_of "${ARTIFACT_ROOT_REQUIRED[@]}" "${ARTIFACT_ROOT_OPTIONAL[@]}")

# comm compares by the collating order of the locale it runs in, and every
# side here was sorted under C: it has to be told the same.
#
# THE TWO QUESTIONS ARE ASKED AGAINST DIFFERENT LISTS, and that asymmetry is
# the fix: a name nobody knows about is measured against EVERYTHING known,
# while a name that went missing is measured against what must always be
# there. An optional entry that fails to appear is neither.
unexpected=$(LC_ALL=C comm -13 <(lines_in "$artifact_known") \
    <(lines_in "$artifact_actual"))
missing=$(LC_ALL=C comm -23 <(lines_in "$artifact_required") \
    <(lines_in "$artifact_actual"))

if [[ -n "$unexpected" || -n "$missing" ]]; then
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
        "instructions in client/docker/Dockerfile and the inventory arrays in" \
        "this script -- ARTIFACT_ROOT_REQUIRED for anything the artifact must" \
        "always have, ARTIFACT_ROOT_OPTIONAL for what never ships and may be" \
        "skipped by an incremental build."
fi

# The count is of the REQUIRED class alone, not of what ships and not of the
# whole inventory: two of those six entries (the libdecor libraries) are
# required to be there and are still left out of the image on purpose, and the
# optional class is reported on its own line below. What this check proves is
# that the build output is still the one the inventory was written against --
# nothing new appeared next to the executable unnoticed.
#
# WHAT IS PRINTED IS THE ABSENCE, because that is the half that surprises
# somebody later: two artifacts of the same commit differing by a folder is
# exactly the kind of difference that reads as a defect until the log says it
# was expected.
optional_absent=$(LC_ALL=C comm -23 <(lines_of "${ARTIFACT_ROOT_OPTIONAL[@]}") \
    <(lines_in "$artifact_actual"))
info "artifact root as inventoried: ${#ARTIFACT_ROOT_REQUIRED[@]} required entries" \
     "present, none new"
if [[ -n "$optional_absent" ]]; then
    while IFS= read -r entry; do
        info "  optional, not produced by this build: $entry"
    done <<<"$optional_absent"
fi

# The compiled burst library, on the other hand, is not optional at all.
[[ -f "$artifact_dir/$BURST_PLAYER_PLUGIN" ]] || die \
    "no burst library in the artifact: '$BURST_PLAYER_PLUGIN'" \
    "The player build produced no compiled burst code, which means burst" \
    "compilation was off for this build -- not that the cache was warm. The" \
    "image would ship a server running the managed fallback. Check the AOT" \
    "settings and the editor's command line before building again."

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
