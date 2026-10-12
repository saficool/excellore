#!/usr/bin/env sh
set -eu

hook_dir="$(git rev-parse --git-path hooks)"
mkdir -p "$hook_dir"
cp scripts/pre-commit "$hook_dir/pre-commit"
chmod +x "$hook_dir/pre-commit"
echo "Installed pre-commit hook at $hook_dir/pre-commit"
