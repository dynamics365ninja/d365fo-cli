#!/usr/bin/env bash
# d365fo CLI - one-line installer (macOS / Linux)
#
#   curl -fsSL https://raw.githubusercontent.com/dynamics365ninja/d365fo-cli/main/install.sh | bash
#
# Off-platform (scenario B in docs/SETUP.md): read / search / scaffold work
# everywhere; build/sync/test/bp need a Windows D365FO VM regardless of how
# the CLI got installed. There is no package registry yet (see
# docs/MIGRATION_FROM_MCP.md) - this script clones the repo, publishes a
# self-contained binary with 'dotnet publish', and puts it on PATH.
# 'd365fo init' is the setup wizard. Safe to re-run.
#
# Env vars (no flags - this is piped through bash):
#   D365FO_CLI_DIR=/path             where to clone / look for an existing checkout
#   D365FO_CLI_YES=1                 non-interactive: pass --no-wizard to 'd365fo init'
#   D365FO_CLI_NO_WIZARD=1           install only, skip 'd365fo init' entirely
#   D365FO_CLI_RUN_EXTRACT=1         also run 'index build' + 'index extract' (can take minutes)

set -euo pipefail

MIN_DOTNET_MAJOR=10
REPO_URL='https://github.com/dynamics365ninja/d365fo-cli.git'

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
ok()   { printf '\033[32m  + %s\033[0m\n' "$1"; }
note() { printf '\033[33m  * %s\033[0m\n' "$1"; }
fail() { printf '\033[31m  x %s\033[0m\n' "$1"; exit 1; }

case "$(uname -s)" in
  Darwin) OS=osx ;;
  Linux)  OS=linux ;;
  *) fail "Unsupported OS: $(uname -s). See docs/SETUP.md for manual steps." ;;
esac
case "$(uname -m)" in
  x86_64|amd64) ARCH=x64 ;;
  arm64|aarch64) ARCH=arm64 ;;
  *) fail "Unsupported architecture: $(uname -m)." ;;
esac
RID="${OS}-${ARCH}"
# Published RIDs (see docs/SETUP.md): win-x64, linux-x64, osx-x64, osx-arm64 - no linux-arm64.
case "$RID" in
  linux-x64|osx-x64|osx-arm64) ;;
  *) fail "Unsupported platform: $RID. Supported: linux-x64, osx-x64, osx-arm64." ;;
esac

ensure_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    local major
    major="$(dotnet --version | cut -d. -f1)"
    if [ "$major" -ge "$MIN_DOTNET_MAJOR" ]; then ok ".NET SDK $(dotnet --version)"; return; fi
    note ".NET SDK $(dotnet --version) found, but $MIN_DOTNET_MAJOR+ is required"
  fi
  step "Installing .NET SDK $MIN_DOTNET_MAJOR (official dotnet-install.sh, no root needed)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel "${MIN_DOTNET_MAJOR}.0" --install-dir "$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
  if ! command -v dotnet >/dev/null 2>&1; then
    fail ".NET SDK still not on PATH - add \$HOME/.dotnet to PATH and re-run, or install from https://dotnet.microsoft.com/download."
  fi
  ok ".NET SDK $(dotnet --version)"
}

ensure_git() {
  command -v git >/dev/null 2>&1 || fail 'git is required - install it (apt/brew/yum) and re-run.'
  ok "$(git --version)"
}

find_checkout() {
  local candidates=()
  [ -n "${D365FO_CLI_DIR:-}" ] && candidates+=("$D365FO_CLI_DIR")
  candidates+=("$HOME/d365fo-cli")
  for dir in "${candidates[@]}"; do
    [ -d "$dir/.git" ] && { echo "$dir"; return; }
  done
  if [ -n "${D365FO_CLI_DIR:-}" ] && [ -d "$D365FO_CLI_DIR" ] && [ -n "$(ls -A "$D365FO_CLI_DIR" 2>/dev/null)" ]; then
    fail "$D365FO_CLI_DIR exists, is not empty, and is not a git checkout. Empty it or point \$D365FO_CLI_DIR elsewhere."
  fi
  echo ""
}

build_and_install() {
  local dir="$1"
  local bin_dir="$HOME/.local/share/d365fo-cli/bin"
  step 'Publishing d365fo (dotnet publish -c Release)'
  (cd "$dir" && dotnet publish src/D365FO.Cli -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:PublishTrimmed=true -o "$bin_dir")
  chmod +x "$bin_dir/d365fo"
  ok "Installed to $bin_dir"

  local profile="$HOME/.profile"
  [ -n "${ZSH_VERSION:-}" ] && profile="$HOME/.zshrc"
  [ -n "${BASH_VERSION:-}" ] && [ -f "$HOME/.bashrc" ] && profile="$HOME/.bashrc"
  if ! grep -qs "d365fo-cli/bin" "$profile" 2>/dev/null; then
    { echo ''; echo '# d365fo-cli'; echo "export PATH=\"$bin_dir:\$PATH\""; } >> "$profile"
    note "Added $bin_dir to PATH in $profile - restart your shell, or run: export PATH=\"$bin_dir:\$PATH\""
  fi
  export PATH="$bin_dir:$PATH"

  if [ -n "${D365FO_CLI_NO_WIZARD:-}" ]; then
    note 'Skipping the setup wizard (D365FO_CLI_NO_WIZARD set).'
    echo ''
    echo "Next: $bin_dir/d365fo init --persist-profile"
    return
  fi

  step "Running 'd365fo init' (interactive wizard in a real terminal; detects PackagesLocalDirectory)"
  init_args=(--persist-profile)
  [ -n "${D365FO_CLI_RUN_EXTRACT:-}" ] && init_args+=(--run-extract)
  [ -n "${D365FO_CLI_YES:-}" ] && init_args+=(--no-wizard)
  "$bin_dir/d365fo" init "${init_args[@]}"

  step "Running 'd365fo doctor'"
  "$bin_dir/d365fo" doctor

  echo ''
  echo 'Useful commands (after restarting your shell, or from $bin_dir):'
  echo '  d365fo doctor                              health check'
  echo '  d365fo index build; d365fo index extract   populate the index'
  echo '  d365fo --help                               command list'
  echo ''
  echo 'build/sync/test/bp still need a Windows D365FO VM - see docs/SETUP.md.'
}

echo ''
echo 'd365fo CLI - installer'
echo ''

step 'Checking prerequisites'
ensure_dotnet
ensure_git

checkout="$(find_checkout)"
if [ -n "$checkout" ]; then
  note "Existing checkout found at $checkout - updating it in place."
  step "Updating $checkout"
  git -C "$checkout" pull --ff-only
  build_and_install "$checkout"
else
  install_dir="${D365FO_CLI_DIR:-$HOME/d365fo-cli}"
  step "Cloning into $install_dir"
  git clone "$REPO_URL" "$install_dir"
  build_and_install "$install_dir"
fi
