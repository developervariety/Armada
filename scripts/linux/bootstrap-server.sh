#!/usr/bin/env bash
#
# bootstrap-server.sh — provision a fresh Armada server from scratch.
#
# Target: Ubuntu 24.04 / 26.04, run as root on a clean box.
# Idempotent: safe to re-run. Existing secrets are kept unless you choose to replace them.
#
#   sudo ./bootstrap-server.sh
#
# Everything interactive happens in PHASE 1. After that it runs unattended.
# Secrets are read with `read -rs` (never echoed), written with umask 077, and
# never printed to stdout or the shell history.
#
set -euo pipefail
umask 077

# All install locations are overridable so the script carries no site-specific
# paths. Defaults match the documented layout.
ARMADA_USER="${ARMADA_USER:-armada}"
ARMADA_HOME="${ARMADA_HOME:-/home/${ARMADA_USER}}"
SRV_ROOT="${ARMADA_SRV_ROOT:-/srv/${ARMADA_USER}}"
DOCKER_DIR="${SRV_ROOT}/docker"
REPO_DIR="${ARMADA_REPO_DIR:-${SRV_ROOT}/RiderProjects/armada}"

# Published SSH host key fingerprints. Verified before trusting — never blind TOFU.
GITHUB_ED25519_FP='SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU'
GITLAB_ED25519_FP='SHA256:eUXGGm1YGsMAS7vkcx6JOJdOGHPem5gQp4taiCfCLB8'

RED=$'\e[31m'; GRN=$'\e[32m'; YLW=$'\e[33m'; BLD=$'\e[1m'; RST=$'\e[0m'
say()  { printf '%s==>%s %s\n' "$BLD" "$RST" "$*"; }
ok()   { printf '  %s✓%s %s\n' "$GRN" "$RST" "$*"; }
warn() { printf '  %s!%s %s\n' "$YLW" "$RST" "$*"; }
die()  { printf '  %s✗%s %s\n' "$RED" "$RST" "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "run as root (sudo $0)"

ask()     { local p="$1" d="${2:-}" v; read -rp "  $p${d:+ [$d]}: " v; printf '%s' "${v:-$d}"; }
ask_secret() {                      # never echoes, never stored in history
  local p="$1" v
  read -rsp "  $p: " v; echo >&2
  printf '%s' "$v"
}
ask_yn()  { local p="$1" d="${2:-y}" v; read -rp "  $p [${d}/$([[ $d == y ]] && echo n || echo y)]: " v; v="${v:-$d}"; [[ ${v,,} == y* ]]; }
gen_pw()  { tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-48}"; }

###############################################################################
say "PHASE 1 — questions (everything after this is unattended)"
###############################################################################

GIT_NAME="$(ask 'Git commit author name' 'armada')"
GIT_EMAIL="$(ask 'Git commit author email' '')"
[[ -n $GIT_EMAIL ]] || die "commit email is required — it decides attribution on GitHub/GitLab"

echo
echo "  GitHub — needs a PAT with scopes: repo, read:org (add 'workflow' if"
echo "  armada edits .github/workflows/)."
GH_USER="$(ask 'GitHub username' '')"
GH_TOKEN="$(ask_secret 'GitHub PAT')"
[[ -n $GH_USER && -n $GH_TOKEN ]] || die "GitHub username and PAT are both required"

echo
echo "  GitLab — needs a PAT with scopes: api, read_repository, write_repository."
echo "  ('api' is required for merge-request creation and the merge queue.)"
GL_USER="$(ask 'GitLab username' '')"
GL_TOKEN="$(ask_secret 'GitLab PAT')"
[[ -n $GL_USER && -n $GL_TOKEN ]] || die "GitLab username and PAT are both required"

echo
OPENAI_API_KEY="$(ask_secret 'OPENAI_API_KEY (blank to skip)')" || true

echo
POSTGRES_DB="$(ask 'Postgres database name' 'armada')"
POSTGRES_USER="$(ask 'Postgres username' 'armada')"
if ask_yn 'Generate a random Postgres password?' y; then
  POSTGRES_PASSWORD="$(gen_pw 64)"; ok "generated (64 chars)"
else
  POSTGRES_PASSWORD="$(ask_secret 'Postgres password')"
fi

if ask_yn 'Generate a random Armada API key + remote-control password?' y; then
  ARMADA_API_KEY="$(gen_pw 32)"; REMOTE_PW="$(gen_pw 24)"; ok "generated"
else
  ARMADA_API_KEY="$(ask_secret 'Armada API key')"
  REMOTE_PW="$(ask_secret 'Remote-control password')"
fi

echo
ADMIN_IP="$(ask 'Your IP to whitelist from SSH rate-limiting (blank to skip)' '')"
INSTALL_SSHD_HARDENING=$(ask_yn 'Raise sshd MaxStartups to avoid lockouts from rapid connections?' y && echo 1 || echo 0)

echo
say "Summary (no secrets shown)"
cat <<EOF
    user:            ${ARMADA_USER} (uid 1000)
    commit identity: ${GIT_NAME} <${GIT_EMAIL}>
    github:          ${GH_USER}          (PAT: ${#GH_TOKEN} chars)
    gitlab:          ${GL_USER}          (PAT: ${#GL_TOKEN} chars)
    postgres:        ${POSTGRES_USER}@${POSTGRES_DB}  (pw: ${#POSTGRES_PASSWORD} chars)
    openai key:      $([[ -n ${OPENAI_API_KEY:-} ]] && echo "${#OPENAI_API_KEY} chars" || echo "skipped")
EOF
ask_yn 'Proceed?' y || die "aborted"

###############################################################################
say "PHASE 2 — system packages"
###############################################################################
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
  ca-certificates curl git gnupg jq python3 openssh-client uidmap >/dev/null
ok "base packages"

# Docker (official repo — distro packages lag badly)
if ! command -v docker >/dev/null; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  . /etc/os-release
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -qq
  apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
fi
ok "docker $(docker --version | awk '{print $3}' | tr -d ,)"

# The admiral image is rebuilt on the server on every deploy, so the builder
# cache grows without limit unless a garbage-collection policy bounds it. Cap it
# here rather than relying on a manual prune, which is forgotten until the disk
# fills. Raise the ceiling only if a normal build stops hitting its cache.
if [ ! -f /etc/docker/daemon.json ]; then
  install -m 0755 -d /etc/docker
  cat > /etc/docker/daemon.json <<'DAEMONJSON'
{
  "builder": {
    "gc": {
      "enabled": true,
      "defaultKeepStorage": "20GB"
    }
  }
}
DAEMONJSON
  systemctl restart docker
  ok "docker builder cache capped at 20GB"
else
  warn "/etc/docker/daemon.json exists — check that builder.gc caps the cache"
fi

# gh — NOTE: must exist on the HOST too, not just in the image. Without it,
# host-side GitHub operations fail with "gh: not found".
if ! command -v gh >/dev/null; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg \
    | dd of=/etc/apt/keyrings/githubcli-archive-keyring.gpg status=none
  chmod a+r /etc/apt/keyrings/githubcli-archive-keyring.gpg
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" \
    > /etc/apt/sources.list.d/github-cli.list
  apt-get update -qq && apt-get install -y -qq gh >/dev/null
fi
ok "gh $(gh --version | head -1 | awk '{print $3}')"

if ! command -v glab >/dev/null; then
  apt-get install -y -qq glab >/dev/null 2>&1 || {
    GLAB_VER=$(curl -fsSL https://api.github.com/repos/gitlab-org/cli/releases/latest | jq -r .tag_name | tr -d v)
    curl -fsSL "https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VER}/downloads/glab_${GLAB_VER}_linux_amd64.deb" -o /tmp/glab.deb
    dpkg -i /tmp/glab.deb >/dev/null && rm -f /tmp/glab.deb
  }
fi
ok "glab $(glab --version | head -1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+')"

###############################################################################
say "PHASE 3 — user and directories"
###############################################################################
if ! id -u "$ARMADA_USER" >/dev/null 2>&1; then
  useradd -m -u 1000 -s /bin/bash "$ARMADA_USER" 2>/dev/null \
    || useradd -m -s /bin/bash "$ARMADA_USER"
fi
usermod -aG docker "$ARMADA_USER"
A_UID=$(id -u "$ARMADA_USER"); A_GID=$(id -g "$ARMADA_USER")
ok "user ${ARMADA_USER} uid=${A_UID} gid=${A_GID}"

# Every path the compose file bind-mounts must pre-exist, or Docker creates it
# as a root-owned directory and the container (running as uid 1000) can't write.
for d in \
  "${ARMADA_HOME}/.armada" "${ARMADA_HOME}/.mux" "${ARMADA_HOME}/.mux-tmp" \
  "${ARMADA_HOME}/.config/glab-cli" "${ARMADA_HOME}/.config/gh" \
  "${ARMADA_HOME}/.config/git" "${ARMADA_HOME}/.config/cursor" \
  "${ARMADA_HOME}/.config/opencode" "${ARMADA_HOME}/.local/share/opencode" \
  "${ARMADA_HOME}/.local/state" "${ARMADA_HOME}/.claude" "${ARMADA_HOME}/.codex" \
  "${ARMADA_HOME}/.gemini" "${ARMADA_HOME}/.cursor" "${ARMADA_HOME}/.cache" \
  "${ARMADA_HOME}/bin" "${ARMADA_HOME}/.ssh" \
  "${SRV_ROOT}/RiderProjects" "${SRV_ROOT}/AI-Memory" "${DOCKER_DIR}"
do
  mkdir -p "$d"
done
[[ -f "${ARMADA_HOME}/.claude.json" ]] || echo '{}' > "${ARMADA_HOME}/.claude.json"
chown -R "${A_UID}:${A_GID}" "$ARMADA_HOME" "$SRV_ROOT"
chmod 700 "${ARMADA_HOME}/.ssh"
ok "directory tree"

###############################################################################
say "PHASE 4 — SSH key + verified host keys"
###############################################################################
KEY="${ARMADA_HOME}/.ssh/id_ed25519"
if [[ ! -f $KEY ]]; then
  sudo -u "$ARMADA_USER" ssh-keygen -t ed25519 -C "${ARMADA_USER}-server" -f "$KEY" -N '' -q
  ok "generated $(ssh-keygen -lf "${KEY}.pub" | awk '{print $2}')"
else
  ok "key exists — kept"
fi

KH="${ARMADA_HOME}/.ssh/known_hosts"; touch "$KH"
for h in github.com gitlab.com; do
  case $h in github.com) want=$GITHUB_ED25519_FP;; gitlab.com) want=$GITLAB_ED25519_FP;; esac
  scan=$(ssh-keyscan -t ed25519 "$h" 2>/dev/null)
  got=$(printf '%s' "$scan" | ssh-keygen -lf - 2>/dev/null | awk '{print $2}')
  [[ $got == "$want" ]] || die "host key MISMATCH for $h (got $got, want $want) — possible MITM, refusing"
  ssh-keygen -R "$h" -f "$KH" >/dev/null 2>&1 || true
  printf '%s\n' "$scan" >> "$KH"
  ok "$h host key verified"
done
chown "${A_UID}:${A_GID}" "$KH"; chmod 600 "$KH"

###############################################################################
say "PHASE 5 — git config and credentials"
###############################################################################
# Global git config lives in the XDG *directory*, never as a bind-mounted FILE.
# A file bind-mount pins an inode: `git config` replaces the file, and the
# container keeps reading the stale original until it is restarted.
GITCFG="${ARMADA_HOME}/.config/git/config"
cat > "$GITCFG" <<EOF
[user]
	name = ${GIT_NAME}
	email = ${GIT_EMAIL}
[credential]
	helper = store --file=${ARMADA_HOME}/.armada/git-credentials
[init]
	defaultBranch = main
[safe]
	directory = *
EOF
# A stale ~/.gitconfig would take precedence over the XDG path — remove it.
[[ -f "${ARMADA_HOME}/.gitconfig" ]] && mv "${ARMADA_HOME}/.gitconfig" "${ARMADA_HOME}/.gitconfig.superseded"
chown "${A_UID}:${A_GID}" "$GITCFG"; chmod 600 "$GITCFG"
ok "git config at ${GITCFG}"

# Credentials live under .armada/ because that is a *directory* mount, so the
# store helper can create its .lock file. In a file-mounted location the helper
# fails with "unable to get credential storage lock: Permission denied".
CREDS="${ARMADA_HOME}/.armada/git-credentials"
touch "$CREDS"; chmod 600 "$CREDS"
tmp=$(mktemp); trap 'rm -f "$tmp"' EXIT
grep -vE '(github|gitlab)\.com$' "$CREDS" 2>/dev/null > "$tmp" || true
{ printf 'https://%s:%s@github.com\n' "$GH_USER" "$GH_TOKEN"
  printf 'https://%s:%s@gitlab.com\n' "$GL_USER" "$GL_TOKEN"; } >> "$tmp"
cat "$tmp" > "$CREDS"
chown "${A_UID}:${A_GID}" "$CREDS"; chmod 600 "$CREDS"
ok "credential store seeded (${CREDS})"

# CLI auth — token via stdin so it never appears in argv or shell history.
printf '%s' "$GH_TOKEN" | sudo -u "$ARMADA_USER" -H gh auth login --hostname github.com --with-token
ok "gh authenticated as $(sudo -u "$ARMADA_USER" -H gh api user --jq .login)"

printf '%s' "$GL_TOKEN" | sudo -u "$ARMADA_USER" -H glab auth login --hostname gitlab.com --stdin >/dev/null 2>&1 || true
GL_WHO=$(sudo -u "$ARMADA_USER" -H glab api user 2>/dev/null | jq -r .username || echo '?')
ok "glab authenticated as ${GL_WHO}"
# NOTE: `glab auth status` can print "Invalid token provided in configuration
# file" even when the token is valid. Trust this instead:
#   glab api personal_access_tokens/self
GL_SCOPES=$(sudo -u "$ARMADA_USER" -H glab api personal_access_tokens/self 2>/dev/null | jq -rc .scopes || echo '[]')
[[ $GL_SCOPES == *api* ]] && ok "glab scopes ${GL_SCOPES}" \
  || warn "glab token lacks 'api' scope ${GL_SCOPES} — MR creation will fail"

###############################################################################
say "PHASE 6 — .env"
###############################################################################
ENVF="${DOCKER_DIR}/.env"
cat > "$ENVF" <<EOF
ARMADA_UID=${A_UID}
ARMADA_GID=${A_GID}
POSTGRES_USER=${POSTGRES_USER}
POSTGRES_DB=${POSTGRES_DB}
POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
EOF
[[ -n ${OPENAI_API_KEY:-} ]] && echo "OPENAI_API_KEY=${OPENAI_API_KEY}" >> "$ENVF"
chown "${A_UID}:${A_GID}" "$ENVF"; chmod 600 "$ENVF"
ok "${ENVF}"

###############################################################################
say "PHASE 7 — docker-compose.yml"
###############################################################################
COMPOSE="${DOCKER_DIR}/docker-compose.yml"
[[ -f $COMPOSE ]] && cp -a "$COMPOSE" "${COMPOSE}.bak.$(date +%Y%m%dT%H%M%SZ)"
cat > "$COMPOSE" <<EOF
# Generated by bootstrap-server.sh — edit with care.
#
# Bind-mount rule: mount DIRECTORIES, never individual FILES.
# A file bind-mount pins an inode. Any tool that rewrites the file (git config,
# credential helpers, most config writers) creates a NEW inode, and the
# container silently keeps reading the old one until restarted. It also blocks
# lock-file creation beside the mounted file.
services:
  postgres:
    image: postgres:16
    container_name: armada-postgres
    restart: unless-stopped
    env_file: [.env]
    environment:
      POSTGRES_USER: \${POSTGRES_USER}
      POSTGRES_PASSWORD: \${POSTGRES_PASSWORD}
      POSTGRES_DB: \${POSTGRES_DB}
    volumes:
      - ./postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U \\"\$\${POSTGRES_USER}\\" -d \\"\$\${POSTGRES_DB}\\""]
      interval: 5s
      timeout: 5s
      retries: 30

  armada:
    build:
      context: ${REPO_DIR}
      dockerfile: src/Armada.Server/Dockerfile
    image: local/armada-server:bootstrap
    container_name: armada-admiral
    restart: unless-stopped
    depends_on:
      postgres: {condition: service_healthy}
    env_file: [.env]
    environment:
      HOME: ${ARMADA_HOME}
      XDG_CONFIG_HOME: ${ARMADA_HOME}/.config
      DOTNET_CLI_HOME: ${ARMADA_HOME}/.armada/dotnet
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "1"
      DOTNET_NOLOGO: "1"
    user: "\${ARMADA_UID}:\${ARMADA_GID}"
    # Required by the bwrap sandbox agents run inside. Note this is a large
    # privilege grant: with SYS_ADMIN plus unconfined seccomp/apparmor the
    # container is close to root-equivalent on the host. Docker is providing
    # packaging here, not a strong security boundary.
    security_opt: [seccomp=unconfined, apparmor=unconfined]
    cap_add: [SYS_ADMIN, NET_ADMIN]
    post_start:
      - command: ["/bin/sh", "-lc", "ip link show can0 >/dev/null 2>&1 || ip link add can0 type vcan; ip link set can0 up"]
        user: root
    ports:
      - "0.0.0.0:7890:7890"
      - "127.0.0.1:7891:7891"
    volumes:
      - ${ARMADA_HOME}/.armada:${ARMADA_HOME}/.armada
      - ${ARMADA_HOME}/.mux:${ARMADA_HOME}/.mux
      - ${ARMADA_HOME}/.mux:/home/ubuntu/.mux
      - ${ARMADA_HOME}/.mux-tmp:${ARMADA_HOME}/.mux-tmp
      - ${ARMADA_HOME}/.ssh:${ARMADA_HOME}/.ssh:ro
      - ${ARMADA_HOME}/bin:${ARMADA_HOME}/bin:ro
      # git config as a DIRECTORY (was a file mount — caused stale-inode bugs)
      - ${ARMADA_HOME}/.config/git:${ARMADA_HOME}/.config/git
      # rw, so 'glab auth login' can run inside the container too (was :ro)
      - ${ARMADA_HOME}/.config/glab-cli:${ARMADA_HOME}/.config/glab-cli
      - ${ARMADA_HOME}/.config/gh:${ARMADA_HOME}/.config/gh
      - ${ARMADA_HOME}/.config/cursor:${ARMADA_HOME}/.config/cursor
      - ${ARMADA_HOME}/.config/opencode:${ARMADA_HOME}/.config/opencode
      - ${ARMADA_HOME}/.local/share/opencode:${ARMADA_HOME}/.local/share/opencode
      - ${ARMADA_HOME}/.local/state:${ARMADA_HOME}/.local/state
      - ${ARMADA_HOME}/.claude:${ARMADA_HOME}/.claude
      - ${ARMADA_HOME}/.codex:${ARMADA_HOME}/.codex
      - ${ARMADA_HOME}/.gemini:${ARMADA_HOME}/.gemini
      - ${ARMADA_HOME}/.cursor:${ARMADA_HOME}/.cursor
      - ${ARMADA_HOME}/.cache:${ARMADA_HOME}/.cache
      # Known remaining file-mount: Claude Code hardcodes ~/.claude.json.
      # Kept deliberately; if it ever goes stale, restart the container.
      - ${ARMADA_HOME}/.claude.json:${ARMADA_HOME}/.claude.json
      - ${SRV_ROOT}/RiderProjects:${SRV_ROOT}/RiderProjects
      - ${SRV_ROOT}/AI-Memory:${SRV_ROOT}/AI-Memory:ro
EOF
chown "${A_UID}:${A_GID}" "$COMPOSE"
ok "${COMPOSE}"

###############################################################################
say "PHASE 8 — armada settings.json"
###############################################################################
SET="${ARMADA_HOME}/.armada/settings.json"
if [[ -f $SET ]]; then
  cp -a "$SET" "${SET}.bak.$(date +%Y%m%dT%H%M%SZ)"
  jq --arg k "$ARMADA_API_KEY" --arg p "$POSTGRES_PASSWORD" --arg u "$POSTGRES_USER" \
     --arg d "$POSTGRES_DB" --arg r "$REMOTE_PW" \
     '.apiKey=$k | .database.password=$p | .database.username=$u | .database.databaseName=$d
      | .database.hostname="postgres" | .database.type="Postgresql" | .remoteControl.password=$r' \
     "$SET" > "${SET}.tmp" && mv "${SET}.tmp" "$SET"
  ok "patched existing settings.json (backup kept)"
else
  cat > "$SET" <<EOF
{
  "apiKey": "${ARMADA_API_KEY}",
  "admiralPort": 7890,
  "mcpPort": 7891,
  "dataDirectory": "${ARMADA_HOME}/.armada",
  "ghCliPath": "gh",
  "glabCliPath": "glab",
  "database": {
    "type": "Postgresql",
    "hostname": "postgres",
    "port": 5432,
    "username": "${POSTGRES_USER}",
    "password": "${POSTGRES_PASSWORD}",
    "databaseName": "${POSTGRES_DB}",
    "schema": "public"
  },
  "remoteControl": { "enabled": false, "password": "${REMOTE_PW}" }
}
EOF
  warn "created a MINIMAL settings.json — armada fills its own defaults on first run"
fi
chown "${A_UID}:${A_GID}" "$SET"; chmod 600 "$SET"

###############################################################################
say "PHASE 9 — sshd hardening"
###############################################################################
# The original box locked us out mid-session from rapid successive SSH
# connections. fail2ban was NOT installed there, so it was sshd's own
# MaxStartups throttle (or an upstream edge firewall) that refused connections.
if [[ $INSTALL_SSHD_HARDENING == 1 ]]; then
  cat > /etc/ssh/sshd_config.d/10-armada.conf <<'EOF'
# Tooling opens several short-lived connections in bursts; the default
# MaxStartups 10:30:100 starts refusing well before that is unreasonable.
MaxStartups 60:30:200
MaxSessions 20
EOF
  sshd -t && systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true
  ok "sshd MaxStartups raised"
fi
if [[ -n $ADMIN_IP ]]; then
  if command -v fail2ban-client >/dev/null; then
    mkdir -p /etc/fail2ban
    printf '[DEFAULT]\nignoreip = 127.0.0.1/8 ::1 %s\n' "$ADMIN_IP" > /etc/fail2ban/jail.local
    systemctl restart fail2ban 2>/dev/null || true
    ok "fail2ban whitelist: ${ADMIN_IP}"
  else
    warn "fail2ban not installed — nothing to whitelist (${ADMIN_IP} noted only)"
  fi
fi

###############################################################################
say "PHASE 10 — build and start"
###############################################################################
if [[ ! -d "${REPO_DIR}/.git" ]]; then
  warn "armada source not at ${REPO_DIR}"
  echo "     Clone it, then re-run:"
  echo "       sudo -u ${ARMADA_USER} git clone https://github.com/${GH_USER}/Armada.git ${REPO_DIR}"
  exit 0
fi
cd "$DOCKER_DIR"
sudo -u "$ARMADA_USER" docker compose up -d --build
ok "stack up"

###############################################################################
say "PHASE 11 — verify"
###############################################################################
fails=0
v() { local label="$1"; shift; printf '  %-42s ' "$label"
      if "$@" >/dev/null 2>&1; then printf '%s✓%s\n' "$GRN" "$RST"; else printf '%s✗%s\n' "$RED" "$RST"; fails=$((fails+1)); fi; }

sleep 5
v "postgres healthy"      docker exec armada-postgres pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"
v "admiral running"       docker exec armada-admiral true
v "gh api (container)"    docker exec armada-admiral gh api user
v "glab api (container)"  docker exec armada-admiral glab api user
v "git identity visible"  bash -c "docker exec armada-admiral git config --global user.email | grep -q '@'"
v "credential helper ok"  bash -c "docker exec armada-admiral git config --global credential.helper | grep -q '.armada/git-credentials'"
v "credential lock works" docker exec armada-admiral sh -c "touch ${ARMADA_HOME}/.armada/.locktest && rm -f ${ARMADA_HOME}/.armada/.locktest"

# SSH is informational, not a failure: the key above has to be added to GitHub
# and GitLab by hand first, so a fresh box is expected to report "not yet".
# Also note `ssh -T git@github.com` exits 1 even on SUCCESS ("does not provide
# shell access"), so exit status cannot be used — match the banner text.
for h in github.com gitlab.com; do
  printf '  %-42s ' "ssh auth ${h} (informational)"
  out=$(sudo -u "$ARMADA_USER" ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -T "git@${h}" 2>&1 || true)
  if grep -qiE 'successfully authenticated|Welcome to GitLab' <<<"$out"; then
    printf '%s✓%s\n' "$GRN" "$RST"
  else
    printf '%s—%s not yet (add the deploy key below)\n' "$YLW" "$RST"
  fi
done

echo
if [[ $fails -eq 0 ]]; then
  ok "all checks passed"
else
  warn "${fails} check(s) failed — see above"
fi

cat <<EOF

$(printf '%s' "$BLD")Next steps$(printf '%s' "$RST")
  1. Add this deploy key to GitHub and GitLab (only needed if you switch any
     remote to SSH — armada itself uses HTTPS + the tokens above):

$(cat "${KEY}.pub")

  2. Clone your working repos under ${SRV_ROOT}/RiderProjects/
  3. Admiral: http://<host>:7890   MCP: 127.0.0.1:7891

$(printf '%s' "$BLD")Remember$(printf '%s' "$RST")
  - 'glab auth status' may falsely report an invalid token. Verify with:
      glab api personal_access_tokens/self
  - Never bind-mount a single FILE into the container. Mount its directory.
EOF
