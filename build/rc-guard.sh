#!/usr/bin/env bash
set -euo pipefail

BASE_SHA=""
HEAD_SHA=""
OVERRIDE="false"
RULES_FILE="build/rc-guard.rules"

usage() {
  cat <<'EOF'
Usage:
  build/rc-guard.sh --base <sha> --head <sha> --override <true|false> --rules build/rc-guard.rules
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base)
      BASE_SHA="${2:-}"
      shift 2
      ;;
    --head)
      HEAD_SHA="${2:-}"
      shift 2
      ;;
    --override)
      OVERRIDE="${2:-}"
      shift 2
      ;;
    --rules)
      RULES_FILE="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$BASE_SHA" || -z "$HEAD_SHA" ]]; then
  echo "Both --base and --head are required." >&2
  usage >&2
  exit 2
fi

if [[ "$OVERRIDE" != "true" && "$OVERRIDE" != "false" ]]; then
  echo "--override must be 'true' or 'false'." >&2
  exit 2
fi

if [[ ! -f "$RULES_FILE" ]]; then
  echo "Rules file not found: $RULES_FILE" >&2
  exit 2
fi

if ! REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null)"; then
  echo "rc-guard must run inside a git repository." >&2
  exit 2
fi

cd "$REPO_ROOT"

if ! git cat-file -e "${BASE_SHA}^{commit}" 2>/dev/null; then
  echo "Base commit not found locally: $BASE_SHA" >&2
  exit 2
fi

if ! git cat-file -e "${HEAD_SHA}^{commit}" 2>/dev/null; then
  echo "Head commit not found locally: $HEAD_SHA" >&2
  exit 2
fi

declare -a PATH_PREFIX_RULES=()
declare -a NAME_KEYWORD_RULES=()
declare -a IGNORE_PREFIX_RULES=()
declare -a IGNORE_CONTAINS_RULES=()

while IFS= read -r raw_line || [[ -n "$raw_line" ]]; do
  line="${raw_line%$'\r'}"
  [[ -z "$line" ]] && continue
  [[ "$line" =~ ^[[:space:]]*# ]] && continue

  case "$line" in
    PATH_PREFIX:*)
      PATH_PREFIX_RULES+=("${line#PATH_PREFIX:}")
      ;;
    NAME_KEYWORD:*)
      NAME_KEYWORD_RULES+=("${line#NAME_KEYWORD:}")
      ;;
    IGNORE_PREFIX:*)
      IGNORE_PREFIX_RULES+=("${line#IGNORE_PREFIX:}")
      ;;
    IGNORE_CONTAINS:*)
      IGNORE_CONTAINS_RULES+=("${line#IGNORE_CONTAINS:}")
      ;;
    *)
      echo "Unsupported rule line: $line" >&2
      exit 2
      ;;
  esac
done < "$RULES_FILE"

mapfile -t changed_files < <(git diff --name-only --diff-filter=ACMRT "$BASE_SHA" "$HEAD_SHA" --)

declare -A rule_to_files=()
declare -a matched_rules=()
considered_count=0
match_count=0

to_lower() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

append_rule_match() {
  local rule="$1"
  local file="$2"

  if [[ -z "${rule_to_files[$rule]+x}" ]]; then
    rule_to_files["$rule"]="$file"
    matched_rules+=("$rule")
  else
    case $'\n'"${rule_to_files[$rule]}"$'\n' in
      *$'\n'"$file"$'\n'*)
        return
        ;;
      *)
        rule_to_files["$rule"]+=$'\n'"$file"
        ;;
    esac
  fi
}

for raw_path in "${changed_files[@]}"; do
  path="${raw_path//$'\\'/\/}"
  [[ -z "$path" ]] && continue

  ignored="false"
  for prefix in "${IGNORE_PREFIX_RULES[@]}"; do
    if [[ "$path" == "$prefix"* ]]; then
      ignored="true"
      break
    fi
  done
  if [[ "$ignored" == "true" ]]; then
    continue
  fi

  for fragment in "${IGNORE_CONTAINS_RULES[@]}"; do
    if [[ "$path" == *"$fragment"* ]]; then
      ignored="true"
      break
    fi
  done
  if [[ "$ignored" == "true" ]]; then
    continue
  fi

  ((considered_count+=1))

  for prefix in "${PATH_PREFIX_RULES[@]}"; do
    if [[ "$path" == "$prefix"* ]]; then
      append_rule_match "PATH_PREFIX:$prefix" "$path"
      ((match_count+=1))
    fi
  done

  file_name="$(basename "$path")"
  path_lower="$(to_lower "$path")"
  file_lower="$(to_lower "$file_name")"
  for keyword in "${NAME_KEYWORD_RULES[@]}"; do
    keyword_lower="$(to_lower "$keyword")"
    if [[ "$file_lower" == *"$keyword_lower"* || "$path_lower" == *"$keyword_lower"* ]]; then
      append_rule_match "NAME_KEYWORD:$keyword" "$path"
      ((match_count+=1))
    fi
  done
done

if [[ "${#matched_rules[@]}" -eq 0 ]]; then
  RESULT_LINE="No blocked changes detected"
else
  if [[ "$OVERRIDE" == "true" ]]; then
    RESULT_LINE="Blocked changes detected (override)"
  else
    RESULT_LINE="Blocked changes detected"
  fi
fi

echo "## RC Guardrails Summary"
echo
echo "- Result: $RESULT_LINE"
echo "- Base: \`$BASE_SHA\`"
echo "- Head: \`$HEAD_SHA\`"
echo "- Override: \`$OVERRIDE\`"
echo "- Files considered after ignores: \`$considered_count\`"
echo

if [[ "${#matched_rules[@]}" -gt 0 ]]; then
  echo "### Matched Files by Rule"
  echo
  for rule in "${matched_rules[@]}"; do
    echo "- $rule"
    while IFS= read -r file; do
      [[ -z "$file" ]] && continue
      echo "  - \`$file\`"
    done <<< "${rule_to_files[$rule]}"
  done
  echo
fi

if [[ "${#matched_rules[@]}" -gt 0 && "$OVERRIDE" == "true" ]]; then
  echo "OVERRIDE USED"
  exit 0
fi

if [[ "${#matched_rules[@]}" -gt 0 ]]; then
  exit 1
fi

exit 0
