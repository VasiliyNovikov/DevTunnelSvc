#!/bin/sh

# Keep the entry point last: a truncated curl | sh download must not start installing.
install_devtunnelsvc() (
    set -eu
    fail() { printf 'devtunnelsvc installer: %s\n' "$*" >&2; exit 1; }
    fetch() {
        curl -q --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
            --retry 2 --max-filesize "$3" --output "$2" "$1"
    }
    enable_linger() {
        uid=$(id -u)
        if linger=$(loginctl show-user "$uid" --property=Linger --value) && [ "$linger" = yes ]; then return 0; fi
        printf 'Enabling systemd lingering for UID %s so user services can run after logout.\n' "$uid"
        loginctl --no-ask-password enable-linger "$uid" ||
            fail "Could not enable systemd lingering. Ask an administrator to run 'loginctl enable-linger $uid', then rerun the installer without sudo."
    }

    [ "$(uname -s)" = Linux ] || fail "Use install.ps1 on Windows; this installer requires Linux."
    case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) fail "Unsupported architecture: $(uname -m). Expected x64 or Arm64." ;;
    esac
    for tool in curl jq sha256sum systemctl loginctl id; do
        command -v "$tool" >/dev/null 2>&1 || fail "Missing required command: $tool."
    done
    case ${HOME:-} in /*) ;; *) fail "An absolute HOME for the signed-in user is required." ;; esac
    [ -z "${SUDO_USER:-}" ] || fail "Run as the user who will use the tunnels, without sudo."

    umask 077
    dir=$HOME/.local/bin
    target=$dir/devtunnelsvc
    repo=VasiliyNovikov/DevTunnelSvc
    asset=devtunnelsvc-linux-$arch
    mkdir -p -- "$dir"
    work=$(mktemp -d "$dir/.devtunnelsvc-install.XXXXXXXX")
    trap 'rm -f -- "$work/release.json" "$work/devtunnelsvc"; rmdir -- "$work"' 0
    trap 'exit 1' HUP INT TERM

    fetch "https://api.github.com/repos/$repo/releases/latest" "$work/release.json" 4194304
    metadata=$(jq -er --arg name "$asset" '
        select(.draft == false and .prerelease == false)
        | .tag_name as $tag | select($tag | test("\\Av(0|[1-9][0-9]*)([.](0|[1-9][0-9]*)){2}\\z"))
        | [.assets[] | select(.name == $name)] | select(length == 1) | .[0]
        | select(.state == "uploaded" and (.size | type == "number" and . > 0 and . <= 268435456 and . == floor))
        | select(.digest | test("\\Asha256:[0-9a-f]{64}\\z"))
        | "\($tag) \(.size) \(.digest[7:])"
    ' "$work/release.json") || fail "No verified stable release metadata for $asset."
    read -r tag size digest <<EOF
$metadata
EOF

    [ ! -L "$target" ] || fail "Refusing to overwrite a symbolic link: $target"
    if [ -e "$target" ]; then
        [ -f "$target" ] || fail "Not a regular file: $target"
        candidate=$target
    else
        candidate=$work/devtunnelsvc
        fetch "https://github.com/$repo/releases/download/$tag/$asset" "$candidate" "$size"
    fi
    actual=$(sha256sum < "$candidate")
    if [ "$(wc -c < "$candidate")" -ne "$size" ] || [ "${actual%% *}" != "$digest" ]; then
        [ "$candidate" != "$target" ] || fail "$target differs from $tag; it was not changed. For a trusted existing installation, use 'devtunnelsvc service stop' and 'devtunnelsvc update'."
        fail "Downloaded $asset failed the release size or SHA-256 check."
    fi
    if [ "$candidate" != "$target" ]; then
        chmod 755 "$candidate"
    elif [ ! -x "$candidate" ]; then
        chmod u+x "$candidate"
    fi
    version=$("$candidate" --version) || fail "Cannot run the verified binary. Check the Linux system dependencies in README.md."
    [ "$version" = "${tag#v}" ] || fail "Executable version does not match $tag."
    enable_linger
    if [ "$candidate" != "$target" ]; then ln -T -- "$candidate" "$target"; fi
    printf 'Installed devtunnelsvc %s at %s\n' "$version" "$target"
    case :${PATH:-}: in
        *:"$dir":*) ;;
        *) printf 'Add %s to PATH in your shell profile to run devtunnelsvc by name.\n' "$dir" ;;
    esac

    "$target" service install
    status=$("$target" service start)
    printf '%s\n' "$status"
    IFS= read -r first_line <<EOF
$status
EOF
    case "$first_line" in
        'Service running; signed out.') "$target" login ;;
        'Service running; signed in.') ;; # Preserve existing credentials and active mappings.
        *) fail "Unexpected service status. Run '$target status' to inspect it." ;;
    esac
)

install_devtunnelsvc
