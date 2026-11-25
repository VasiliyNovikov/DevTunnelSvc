#!/bin/sh
set -eu
installer=$(dirname "$0")/../install.sh
fixture=$(mktemp -d "${TMPDIR:-/tmp}/devtunnelsvc-install-test.XXXXXXXX")
readonly fixture
trap 'rm -rf -- "$fixture"' 0
export fixture
unset SUDO_USER
mkdir "$fixture/helpers"
export PATH="$fixture/helpers:$PATH"

# Only the transport and released CLI are fakes. The shell installer, JSON parsing, hashing,
# file placement, and decisions about service startup and login are exercised as shipped.
cat > "$fixture/helpers/curl" <<'STUB'
#!/bin/sh
set -eu
while [ "$#" -gt 1 ]; do
    if [ "$1" = --output ]; then out=$2; shift 2; else shift; fi
done
case "$1" in
    https://api.github.com/repos/VasiliyNovikov/DevTunnelSvc/releases/latest) src="$fixture/release.json" ;;
    "https://github.com/VasiliyNovikov/DevTunnelSvc/releases/download/v1.2.3/$asset") src="$fixture/cli" ;;
    *) exit 91 ;;
esac
cp "$src" "$out"
STUB
cat > "$fixture/helpers/uname" <<'STUB'
#!/bin/sh
case "$1" in -s) echo Linux ;; -m) echo "$machine" ;; *) exit 92 ;; esac
STUB
printf '#!/bin/sh\nexit 93\n' > "$fixture/helpers/systemctl"
cat > "$fixture/cli" <<'STUB'
#!/bin/sh
set -eu
printf '%s\n' "$*" >> "$fixture/calls"
case "$*" in
    --version) echo 1.2.3; exit ;;
esac
[ "$0" = "$HOME/.local/bin/devtunnelsvc" ] || exit 94
case "$*" in
    'service install') ;;
    'service start')
        [ ! -f "$HOME/refuse-start" ] || { echo 'fixture startup failed' >&2; exit 7; }
        if [ -f "$HOME/signed" ]; then echo 'Service running; signed in.'; else echo 'Service running; signed out.'; fi ;;
    login) touch "$HOME/signed" ;;
    *) exit 95 ;;
esac
STUB
chmod +x "$fixture/helpers/curl" "$fixture/helpers/uname" "$fixture/helpers/systemctl"

make_release() {
    jq -n --arg name "$asset" --arg digest "sha256:$1" --argjson size "$(wc -c < "$fixture/cli")" \
        '{tag_name:"v1.2.3", draft:false, prerelease:false, assets:[{$name,$digest,$size,state:"uploaded"}]}' > "$fixture/release.json"
}
run() { cat "$installer" | sh > "$fixture/output" 2>&1 || { cat "$fixture/output" >&2; return 1; }; }
digest=$(sha256sum < "$fixture/cli")
for machine in x86_64 aarch64; do
    case "$machine" in x86_64) arch=x64 ;; aarch64) arch=arm64 ;; esac
    asset=devtunnelsvc-linux-$arch
    HOME="$fixture/$arch home"
    export machine asset HOME
    : > "$fixture/calls"
    make_release "${digest%% *}"
    run
    cmp "$fixture/cli" "$HOME/.local/bin/devtunnelsvc"
    printf '%s\n' --version 'service install' 'service start' login | cmp - "$fixture/calls"
    : > "$fixture/calls"
    chmod 600 "$HOME/.local/bin/devtunnelsvc"
    run
    printf '%s\n' --version 'service install' 'service start' | cmp - "$fixture/calls"
done

rm "$HOME/signed"
touch "$HOME/refuse-start"
: > "$fixture/calls"
if cat "$installer" | sh > "$fixture/output" 2>&1; then exit 1; fi
grep -q 'fixture startup failed' "$fixture/output"
printf '%s\n' --version 'service install' 'service start' | cmp - "$fixture/calls"
printf 'existing install' > "$HOME/.local/bin/devtunnelsvc"
: > "$fixture/calls"
if cat "$installer" | sh > "$fixture/output" 2>&1; then exit 1; fi
grep -q 'was not changed' "$fixture/output"
[ "$(cat "$HOME/.local/bin/devtunnelsvc")" = 'existing install' ] && [ ! -s "$fixture/calls" ] || exit 1
HOME="$fixture/bad digest"
make_release "$(printf '%064d' 0)"
if cat "$installer" | sh > "$fixture/output" 2>&1; then exit 1; fi
grep -q 'SHA-256' "$fixture/output"
[ ! -e "$HOME/.local/bin/devtunnelsvc" ] && [ ! -s "$fixture/calls" ] || exit 1
printf 'Linux streamed installer checks passed.\n'
