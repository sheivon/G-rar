#!/usr/bin/env bash
# Initialize this folder as a git repo if needed, then push to GitHub.
# Usage: ./push-github.sh ["commit message"]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REMOTE_URL="https://github.com/sheivon/G-rar.git"
BRANCH="main"
COMMIT_MSG="${1:-Sync local G-RAR to GitHub}"

cd "$ROOT"

if ! command -v git >/dev/null 2>&1; then
  echo "error: git is not installed." >&2
  exit 1
fi

# Read the GitHub username from the account signed into VS Code.
vscode_github_login() {
  python3 - <<'PY' 2>/dev/null
import os, sqlite3

candidates = [
    os.path.expanduser("~/.config/Code/User/globalStorage/state.vscdb"),
    os.path.expanduser("~/.config/Code - OSS/User/globalStorage/state.vscdb"),
    os.path.expanduser("~/.config/VSCodium/User/globalStorage/state.vscdb"),
]
skip = {"copilot", "copilot-chat", "copilot-github"}
for db in candidates:
    if not os.path.isfile(db):
        continue
    try:
        con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
        keys = [row[0] for row in con.execute("SELECT key FROM ItemTable")]
        con.close()
    except Exception:
        continue
    for key in keys:
        if not key.startswith("github-") or key.endswith("-usages") or "." in key:
            continue
        login = key[len("github-"):]
        if login and login not in skip:
            print(login)
            raise SystemExit(0)
PY
}

ensure_git_identity() {
  local name email login profile id display
  name="$(git config --get user.name || true)"
  email="$(git config --get user.email || true)"
  if [[ -n "$name" && -n "$email" ]]; then
    echo "Using git identity $name <$email>"
    return 0
  fi

  login="$(vscode_github_login || true)"
  if [[ -z "$login" ]]; then
    echo "error: no git identity and no GitHub account signed into VS Code." >&2
    echo "Sign into GitHub in VS Code, or set:" >&2
    echo "  git config --global user.name \"Your Name\"" >&2
    echo "  git config --global user.email \"you@example.com\"" >&2
    exit 1
  fi

  profile="$(curl -fsSL "https://api.github.com/users/${login}" 2>/dev/null || true)"
  id="$(printf '%s' "$profile" | python3 -c "import json,sys; print(json.load(sys.stdin).get('id') or '')" 2>/dev/null || true)"
  display="$(printf '%s' "$profile" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('name') or d.get('login') or '')" 2>/dev/null || true)"

  name="${name:-${display:-$login}}"
  if [[ -n "$id" ]]; then
    email="${email:-${id}+${login}@users.noreply.github.com}"
  else
    email="${email:-${login}@users.noreply.github.com}"
  fi

  git config --local user.name "$name"
  git config --local user.email "$email"
  echo "Using VS Code GitHub account $login ($name <$email>)"
}

if [[ ! -d .git ]]; then
  echo "Initializing git repository on branch $BRANCH..."
  git init -b "$BRANCH"
fi

ensure_git_identity

if git remote get-url origin >/dev/null 2>&1; then
  current="$(git remote get-url origin)"
  if [[ "$current" != "$REMOTE_URL" && "$current" != "${REMOTE_URL%.git}" && "$current" != "git@github.com:sheivon/G-rar.git" ]]; then
    echo "Updating origin from $current to $REMOTE_URL"
    git remote set-url origin "$REMOTE_URL"
  fi
else
  echo "Adding origin $REMOTE_URL"
  git remote add origin "$REMOTE_URL"
fi

git add -A

if git diff --cached --quiet; then
  echo "No local changes to commit."
else
  git commit -m "$COMMIT_MSG"
fi

if ! git rev-parse --verify HEAD >/dev/null 2>&1; then
  echo "error: nothing to push (no commits yet)." >&2
  exit 1
fi

echo "Fetching $REMOTE_URL..."
git fetch origin "$BRANCH" || git fetch origin

if git rev-parse --verify "origin/$BRANCH" >/dev/null 2>&1; then
  if git merge-base --is-ancestor "origin/$BRANCH" HEAD 2>/dev/null; then
    :
  elif git merge-base HEAD "origin/$BRANCH" >/dev/null 2>&1; then
    echo "Rebasing onto origin/$BRANCH..."
    git pull --rebase origin "$BRANCH"
  else
    echo "Merging unrelated GitHub history (README/LICENSE) into local $BRANCH..."
    git merge "origin/$BRANCH" --allow-unrelated-histories --no-edit
  fi
fi

echo "Pushing to origin/$BRANCH..."
git push -u origin "$BRANCH"
echo "Done: $REMOTE_URL"
