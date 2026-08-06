#!/usr/bin/env bash
set -Eeuo pipefail

GITHUB_OWNER="${GITHUB_OWNER:-${ORG:-Intellect-Informatics-Pvt-Ltd}}"
NUGET_SOURCE="${NUGET_SOURCE:-https://nuget.pkg.github.com/${GITHUB_OWNER}/index.json}"
GITHUB_PACKAGES_USERNAME="${GITHUB_PACKAGES_USERNAME:-${GITHUB_ACTOR:-}}"
GITHUB_PACKAGES_PAT="${GITHUB_PACKAGES_PAT:-}"
GITHUB_TOKEN="${GITHUB_TOKEN:-}"

NUGET_CONFIG="${NUGET_CONFIG:-NuGet.Config}"
PACKAGE_OUTPUT_DIR="${PACKAGE_OUTPUT_DIR:-artifacts/nuget}"
CONFIGURATION="${CONFIGURATION:-Release}"
INITIAL_VERSION="${INITIAL_VERSION:-1.0.0}"
NEW_VERSION="${NEW_VERSION:-}"
PUSH_PACKAGES="${PUSH_PACKAGES:-true}"
PACK_ALL="${PACK_ALL:-true}"
MAX_VERSION_PAGES="${MAX_VERSION_PAGES:-20}"

NUGET_API_KEY="${NUGET_API_KEY:-${GITHUB_PACKAGES_PAT:-${GITHUB_TOKEN:-}}}"
PACKAGE_QUERY_TOKEN="${GITHUB_PACKAGES_PAT:-${GITHUB_TOKEN:-}}"

log() {
    printf '%s\n' "$*" >&2
}

die() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 ||
        die "Required command '$1' was not found."
}

for command_name in git dotnet curl jq sort find grep sed awk; do
    require_command "$command_name"
done

[ -f "$NUGET_CONFIG" ] ||
    die "NuGet configuration file not found: $NUGET_CONFIG"

case "$PACK_ALL" in
    true|false) ;;
    *) die "PACK_ALL must be true or false, but was '$PACK_ALL'." ;;
esac

case "$PUSH_PACKAGES" in
    true|false) ;;
    *) die "PUSH_PACKAGES must be true or false, but was '$PUSH_PACKAGES'." ;;
esac

if [ -n "$NEW_VERSION" ] &&
   [[ ! "$NEW_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]]; then
    die "NEW_VERSION '$NEW_VERSION' is not a supported semantic version."
fi

if [ "$PUSH_PACKAGES" = "true" ] &&
   [ -z "$NUGET_API_KEY" ]; then
    die "Set GITHUB_PACKAGES_PAT, GITHUB_TOKEN, or NUGET_API_KEY before publishing packages."
fi

configure_github_packages_source() {
    local token="${GITHUB_PACKAGES_PAT:-${GITHUB_TOKEN:-}}"

    if [ -z "$token" ]; then
        log "No GitHub Packages token is available; authenticated private restores may fail."
        return 0
    fi

    if [ -z "$GITHUB_PACKAGES_USERNAME" ]; then
        die "Set GITHUB_PACKAGES_USERNAME or GITHUB_ACTOR."
    fi

    log "Configuring GitHub Packages source for $GITHUB_PACKAGES_USERNAME."

    dotnet nuget update source github \
        --configfile "$NUGET_CONFIG" \
        --source "$NUGET_SOURCE" \
        --username "$GITHUB_PACKAGES_USERNAME" \
        --password "$token" \
        --store-password-in-clear-text \
        >/dev/null
}

resolve_base_ref() {
    if [ -n "${BASE_REF:-}" ]; then
        printf '%s\n' "$BASE_REF"
        return 0
    fi

    if [ -n "${GITHUB_EVENT_PATH:-}" ] &&
       [ -f "$GITHUB_EVENT_PATH" ]; then
        local before_sha

        before_sha="$(jq -r '.before // empty' "$GITHUB_EVENT_PATH")"

        if [ -n "$before_sha" ] &&
           ! printf '%s' "$before_sha" | grep -Eq '^0+$' &&
           git cat-file -e "${before_sha}^{commit}" 2>/dev/null; then
            printf '%s\n' "$before_sha"
            return 0
        fi
    fi

    if git rev-parse --verify HEAD^ >/dev/null 2>&1; then
        printf '%s\n' "HEAD^"
    fi
}

changed_files() {
    local base_ref
    base_ref="$(resolve_base_ref || true)"

    if [ "$PACK_ALL" = "true" ]; then
        git ls-files
        return 0
    fi

    if [ -n "$base_ref" ]; then
        log "Detecting changes since $base_ref..."
        git diff --name-only "$base_ref" HEAD
    else
        log "No base commit found; considering all tracked files."
        git ls-files
    fi
}

is_repo_wide_build_input() {
    case "$1" in
        *.sln|\
        Directory.Build.props|\
        Directory.Build.targets|\
        Directory.Packages.props|\
        NuGet.Config|\
        nuget.config|\
        global.json|\
        build_push_script.sh|\
        .github/workflows/*)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

is_packable_project() {
    local csproj_file="$1"
    local base_name

    [ -f "$csproj_file" ] || return 1

    if grep -Eiq \
        '<IsPackable>[[:space:]]*false[[:space:]]*</IsPackable>' \
        "$csproj_file"; then
        return 1
    fi

    if grep -Eiq \
        '<PackageId>[[:space:]]*[^<]+[[:space:]]*</PackageId>' \
        "$csproj_file"; then
        return 0
    fi

    if grep -Eiq \
        '<(IsPackable|GeneratePackageOnBuild)>[[:space:]]*true[[:space:]]*</(IsPackable|GeneratePackageOnBuild)>' \
        "$csproj_file"; then
        return 0
    fi

    if grep -Eiq \
        '<Project[^>]+Sdk="[^"]*(Web|Worker)[^"]*"' \
        "$csproj_file"; then
        return 1
    fi

    if grep -Eiq \
        '<OutputType>[[:space:]]*(Exe|WinExe)[[:space:]]*</OutputType>' \
        "$csproj_file"; then
        return 1
    fi

    base_name="$(basename "$csproj_file" .csproj)"

    if [[ "$base_name" =~ ([._-]|^)(Test|Tests|Testing|IntegrationTests|UnitTests)$ ]]; then
        return 1
    fi

    grep -Eiq \
        '<Project[^>]+Sdk="Microsoft[.]NET[.]Sdk(["/]|[.][^"]*")' \
        "$csproj_file"
}

all_package_projects() {
    local csproj_file

    while IFS= read -r -d '' csproj_file; do
        if is_packable_project "$csproj_file"; then
            printf '%s\n' "$csproj_file"
        else
            log "Skipping non-packable project: $csproj_file"
        fi
    done < <(git ls-files -z '*.csproj' | sort -z)
}

project_for_file() {
    local file_path="$1"
    local project_path
    local project_dir
    local best_project=""
    local best_length=0

    if [[ "$file_path" == *.csproj ]] &&
       is_packable_project "$file_path"; then
        printf '%s\n' "$file_path"
        return 0
    fi

    while IFS= read -r project_path; do
        project_dir="$(dirname "$project_path")"

        if [[ "$file_path" == "$project_dir"/* ||
              "$file_path" == "$project_dir" ]]; then
            if [ "${#project_dir}" -gt "$best_length" ]; then
                best_project="$project_path"
                best_length="${#project_dir}"
            fi
        fi
    done < <(all_package_projects)

    if [ -n "$best_project" ]; then
        printf '%s\n' "$best_project"
    fi
}

projects_to_pack() {
    local file_path
    local project_path
    local selected_projects=()

    if [ "$PACK_ALL" = "true" ]; then
        log "PACK_ALL=true; selecting all packable projects."
        all_package_projects
        return 0
    fi

    while IFS= read -r file_path; do
        [ -z "$file_path" ] && continue

        if is_repo_wide_build_input "$file_path"; then
            log "Repository-level build input changed: $file_path"
            all_package_projects
            return 0
        fi

        project_path="$(project_for_file "$file_path")"

        if [ -n "$project_path" ]; then
            log "Package change detected: $file_path -> $project_path"
            selected_projects+=("$project_path")
        fi
    done < <(changed_files)

    if [ "${#selected_projects[@]}" -gt 0 ]; then
        printf '%s\n' "${selected_projects[@]}" | sort -u
    fi
}

read_package_id() {
    local csproj_file="$1"
    local package_id

    package_id="$(
        sed -nE \
            's/.*<PackageId>[[:space:]]*([^<]+)[[:space:]]*<\/PackageId>.*/\1/p' \
            "$csproj_file" |
            head -n 1
    )"

    if [ -n "$package_id" ]; then
        printf '%s\n' "$package_id"
    else
        basename "$csproj_file" .csproj
    fi
}

is_meta_project() {
    local csproj_file="$1"

    grep -Eq \
        '<IsMetaPackage>[[:space:]]*true[[:space:]]*</IsMetaPackage>' \
        "$csproj_file"
}

get_latest_version() {
    local package_id="$1"

    if [ -z "$PACKAGE_QUERY_TOKEN" ]; then
        log "No query token available for $package_id."
        return 0
    fi

    local encoded_package_id
    encoded_package_id="$(jq -rn --arg package_id "$package_id" '$package_id | @uri')"

    local page
    local response
    local http_status
    local response_body
    local page_count
    local all_versions=""

    for ((page = 1; page <= MAX_VERSION_PAGES; page++)); do
        if ! response="$(
            curl -sS \
                -w '\n%{http_code}' \
                -H "Authorization: Bearer $PACKAGE_QUERY_TOKEN" \
                -H "Accept: application/vnd.github+json" \
                -H "X-GitHub-Api-Version: 2022-11-28" \
                "https://api.github.com/orgs/${GITHUB_OWNER}/packages/nuget/${encoded_package_id}/versions?per_page=100&page=${page}"
        )"; then
            die "Failed to query GitHub Packages for $package_id."
        fi

        http_status="${response##*$'\n'}"
        response_body="${response%$'\n'$http_status}"

        case "$http_status" in
            200)
                if ! printf '%s' "$response_body" | jq -e 'type == "array"' >/dev/null; then
                    die "Unexpected GitHub API response for $package_id."
                fi

                page_count="$(printf '%s' "$response_body" | jq 'length')"
                all_versions+=$'\n'"$(printf '%s' "$response_body" | jq -r '.[]?.name // empty')"

                if [ "$page_count" -lt 100 ]; then
                    break
                fi
                ;;
            404)
                if [ "$page" -eq 1 ]; then
                    log "No existing package found for $package_id."
                fi
                break
                ;;
            401)
                die "GitHub Packages authentication failed for $package_id."
                ;;
            403)
                die "The token user cannot read package $package_id."
                ;;
            *)
                die "Package lookup failed for $package_id with HTTP $http_status: $response_body"
                ;;
        esac
    done

    printf '%s\n' "$all_versions" |
        grep -E '^[0-9]+[.][0-9]+[.][0-9]+' |
        sort -V |
        tail -n 1 || true
}

increment_version() {
    local version="$1"

    if [[ "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+) ]]; then
        printf '%s.%s.%s\n' \
            "${BASH_REMATCH[1]}" \
            "${BASH_REMATCH[2]}" \
            "$((BASH_REMATCH[3] + 1))"
    else
        printf '%s\n' "$INITIAL_VERSION"
    fi
}

next_release_version() {
    if [ -n "$NEW_VERSION" ]; then
        printf '%s\n' "$NEW_VERSION"
        return 0
    fi

    local project_path
    local package_id
    local latest_version
    local latest_versions=()

    for project_path in "$@"; do
        package_id="$(read_package_id "$project_path")"
        latest_version="$(get_latest_version "$package_id")"

        if [ -n "$latest_version" ]; then
            latest_versions+=("$latest_version")
        fi
    done

    if [ "${#latest_versions[@]}" -eq 0 ]; then
        printf '%s\n' "$INITIAL_VERSION"
        return 0
    fi

    latest_version="$(
        printf '%s\n' "${latest_versions[@]}" |
            grep -E '^[0-9]+[.][0-9]+[.][0-9]+' |
            sort -V |
            tail -n 1 || true
    )"

    if [ -n "$latest_version" ]; then
        increment_version "$latest_version"
    else
        printf '%s\n' "$INITIAL_VERSION"
    fi
}

pack_project() {
    local csproj_file="$1"
    local package_id="$2"
    local package_version="$3"

    log "Packing $package_id $package_version from $csproj_file..."

    dotnet pack "$csproj_file" \
        --configuration "$CONFIGURATION" \
        --no-restore \
        -p:Version="$package_version" \
        -p:PackageVersion="$package_version" \
        -p:ContinuousIntegrationBuild=true \
        -o "$PACKAGE_OUTPUT_DIR"

    local nupkg_file="$PACKAGE_OUTPUT_DIR/${package_id}.${package_version}.nupkg"

    [ -f "$nupkg_file" ] ||
        die "Expected package was not generated: $nupkg_file"
}

push_package() {
    local nupkg_file="$1"

    log "Pushing $nupkg_file to $NUGET_SOURCE..."

    if ! dotnet nuget push "$nupkg_file" \
        --api-key "$NUGET_API_KEY" \
        --source "$NUGET_SOURCE" \
        --skip-duplicate; then
        die "Failed to push $nupkg_file. Verify package access and token permissions."
    fi
}

main() {
    local selected_projects=()
    local leaf_projects=()
    local meta_projects=()
    local nupkg_files=()
    local project_path
    local package_id
    local release_version
    local nupkg_file

    while IFS= read -r project_path; do
        [ -z "$project_path" ] && continue
        selected_projects+=("$project_path")
    done < <(projects_to_pack)

    if [ "${#selected_projects[@]}" -eq 0 ]; then
        log "No packable NuGet projects were found; nothing to pack."
        return 0
    fi

    log "Projects selected for packaging:"
    printf '  - %s\n' "${selected_projects[@]}" >&2

    for project_path in "${selected_projects[@]}"; do
        if is_meta_project "$project_path"; then
            meta_projects+=("$project_path")
        else
            leaf_projects+=("$project_path")
        fi
    done

    release_version="$(next_release_version "${selected_projects[@]}")"
    log "Using package version $release_version."

    rm -rf "$PACKAGE_OUTPUT_DIR"
    mkdir -p "$PACKAGE_OUTPUT_DIR"

    configure_github_packages_source

    log "Restoring non-meta package projects..."

    for project_path in "${leaf_projects[@]}"; do
        dotnet restore "$project_path" --configfile "$NUGET_CONFIG"
    done

    for project_path in "${leaf_projects[@]}"; do
        package_id="$(read_package_id "$project_path")"
        pack_project "$project_path" "$package_id" "$release_version"
        nupkg_file="$PACKAGE_OUTPUT_DIR/${package_id}.${release_version}.nupkg"
        nupkg_files+=("$nupkg_file")
    done

    log "Restoring meta-package projects against local artifacts..."

    for project_path in "${meta_projects[@]}"; do
        dotnet restore "$project_path" \
            --configfile "$NUGET_CONFIG" \
            -p:RestoreAdditionalProjectSources="$PACKAGE_OUTPUT_DIR"
    done

    for project_path in "${meta_projects[@]}"; do
        package_id="$(read_package_id "$project_path")"
        pack_project "$project_path" "$package_id" "$release_version"
        nupkg_file="$PACKAGE_OUTPUT_DIR/${package_id}.${release_version}.nupkg"
        nupkg_files+=("$nupkg_file")
    done

    for nupkg_file in "${nupkg_files[@]}"; do
        if [ "$PUSH_PACKAGES" = "true" ]; then
            push_package "$nupkg_file"
        else
            log "PUSH_PACKAGES=false; skipping $nupkg_file."
        fi
    done

    log "Script execution completed."
}

main "$@"
