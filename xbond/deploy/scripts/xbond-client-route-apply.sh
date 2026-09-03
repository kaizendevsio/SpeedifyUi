#!/bin/sh
set -eu

CONFIG_PATH="${XBOND_CLIENT_CONFIG:-/etc/xbond/client.toml}"
TUN_IF="${XBOND_TUN_IF:-xbond0}"
TUN_SRC="${XBOND_TUN_SRC:-10.250.0.2}"
CUDY_SETTINGS_PATH="${XBOND_CUDY_SETTINGS_PATH:-/home/xeon-network/.config/XNetwork/cudy-ap-automation-settings.json}"
LOCAL_BYPASS_STATE_PATH="${XBOND_LOCAL_BYPASS_STATE_PATH:-/run/xbond/local-bypass-routes}"
ACTION="${1:-apply}"

case "$ACTION" in
    prepare|apply)
        ;;
    *)
        echo "Usage: $0 [prepare|apply]" >&2
        exit 2
        ;;
esac

is_tunnel_or_control_dev() {
    case "$1" in
        "$TUN_IF"|connectify0|tailscale0|lo|tun*|wg*)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

host_from_value() {
    value="$1"
    value="${value#http://}"
    value="${value#https://}"
    value="${value%%/*}"
    value="${value%%:*}"
    value="${value#[}"
    value="${value%]}"

    case "$value" in
        ''|*[!0-9.]*)
            return 1
            ;;
        *)
            printf '%s\n' "$value"
            ;;
    esac
}

prefix_from_value() {
    value="$1"
    if [ "${value#http://}" = "$value" ] && [ "${value#https://}" = "$value" ]; then
        case "$value" in
            */*)
                host="${value%/*}"
                case "$host" in
                    ''|*[!0-9.]*)
                        return 1
                        ;;
                    *)
                        printf '%s\n' "$value"
                        return 0
                        ;;
                esac
                ;;
        esac
    fi

    host="$(host_from_value "$value" 2>/dev/null || true)"
    if [ -z "$host" ]; then
        return 1
    fi

    printf '%s/32\n' "$host"
}

extract_cudy_management_url() {
    if [ ! -r "$CUDY_SETTINGS_PATH" ]; then
        return 0
    fi

    sed -n 's/.*"ManagementBaseUrl"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$CUDY_SETTINGS_PATH" | head -n 1
}

remove_recorded_local_bypass_routes() {
    if [ ! -r "$LOCAL_BYPASS_STATE_PATH" ]; then
        return 0
    fi

    while IFS= read -r prefix; do
        [ -n "$prefix" ] || continue
        ip route del "$prefix" 2>/dev/null || true
    done < "$LOCAL_BYPASS_STATE_PATH"

    rm -f "$LOCAL_BYPASS_STATE_PATH" 2>/dev/null || true
}

best_default_route() {
    ip -4 route show default |
        awk -v tun_if="$TUN_IF" '
            {
                dev = ""
                metric = 1000000
                for (i = 1; i <= NF; i++) {
                    if ($i == "dev") dev = $(i + 1)
                    if ($i == "metric") metric = $(i + 1)
                }
                if (dev != "" && dev != tun_if && dev != "connectify0" && dev != "tailscale0") {
                    print metric "\t" $0
                }
            }
        ' |
        sort -n |
        head -n 1 |
        cut -f2-
}

route_fields_from_line() {
    gateway=""
    dev=""
    set -- $1
    while [ "$#" -gt 0 ]; do
        case "$1" in
            via)
                shift
                gateway="${1:-}"
                ;;
            dev)
                shift
                dev="${1:-}"
                ;;
        esac
        shift || true
    done

    if [ -z "$dev" ] || is_tunnel_or_control_dev "$dev"; then
        return 1
    fi

    printf '%s\t%s\n' "$gateway" "$dev"
}

physical_route_for_host() {
    host="$1"

    if [ -n "${XBOND_LOCAL_BYPASS_DEV:-}" ]; then
        printf '%s\t%s\n' "${XBOND_LOCAL_BYPASS_GATEWAY:-}" "$XBOND_LOCAL_BYPASS_DEV"
        return 0
    fi

    route="$(ip -4 route get "$host" 2>/dev/null | head -n 1 || true)"
    fields="$(route_fields_from_line "$route" 2>/dev/null || true)"
    if [ -n "$fields" ]; then
        printf '%s\n' "$fields"
        return 0
    fi

    route="$(best_default_route)"
    route_fields_from_line "$route"
}

apply_route_prefix() {
    prefix="$1"
    host="${prefix%/*}"

    fields="$(physical_route_for_host "$host" 2>/dev/null || true)"
    if [ -z "$fields" ]; then
        echo "XBond route apply: no physical route found for local bypass $prefix" >&2
        return 1
    fi

    gateway="$(printf '%s' "$fields" | awk -F '\t' '{ print $1 }')"
    dev="$(printf '%s' "$fields" | awk -F '\t' '{ print $2 }')"

    if [ -z "$dev" ] || is_tunnel_or_control_dev "$dev"; then
        echo "XBond route apply: refusing local bypass $prefix via invalid device '$dev'" >&2
        return 1
    fi

    if [ -n "$gateway" ]; then
        ip route replace "$prefix" via "$gateway" dev "$dev" metric 1
    else
        ip route replace "$prefix" dev "$dev" metric 1
    fi

    printf '%s\n' "$prefix" >> "$LOCAL_BYPASS_STATE_PATH.tmp"
}

apply_local_bypass_routes() {
    remove_recorded_local_bypass_routes
    rm -f "$LOCAL_BYPASS_STATE_PATH.tmp" 2>/dev/null || true

    values="$(
        printf '%s\n' ${XBOND_LOCAL_BYPASS_HOSTS:-}
        printf '%s\n' ${XBOND_LOCAL_MANAGEMENT_HOSTS:-}
        extract_cudy_management_url
    )"

    printf '%s\n' "$values" |
        tr ',;' '\n\n' |
        while IFS= read -r value; do
            [ -n "$value" ] || continue
            prefix="$(prefix_from_value "$value" 2>/dev/null || true)"
            [ -n "$prefix" ] || continue
            apply_route_prefix "$prefix" || true
        done

    if [ -s "$LOCAL_BYPASS_STATE_PATH.tmp" ]; then
        sort -u "$LOCAL_BYPASS_STATE_PATH.tmp" > "$LOCAL_BYPASS_STATE_PATH"
    else
        rm -f "$LOCAL_BYPASS_STATE_PATH" 2>/dev/null || true
    fi

    rm -f "$LOCAL_BYPASS_STATE_PATH.tmp" 2>/dev/null || true
}

server_addr="$(awk -F= '
    /^[[:space:]]*server_addr[[:space:]]*=/ {
        value = $2
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", value)
        gsub(/^"|"$/, "", value)
        print value
        exit
    }
' "$CONFIG_PATH")"

server_host="${server_addr%:*}"

case "$server_host" in
    ''|*[!0-9.]*)
        echo "XBond route apply: server_addr is not an IPv4 host; skipping server /32 route" >&2
        ;;
    *)
        if [ "$ACTION" = "prepare" ]; then
            # Remove a stale tunnel default before xbond-client opens its path sockets.
            # Otherwise fresh sockets may inherit a recursive route to the server through
            # the tunnel they are trying to create.
            remove_recorded_local_bypass_routes
            ip route delete default dev "$TUN_IF" 2>/dev/null || true
        fi

        best_default="$(best_default_route)"

        if [ -z "$best_default" ]; then
            echo "XBond route apply: no physical IPv4 default route found for server endpoint" >&2
            exit 1
        fi

        gateway=""
        dev=""
        set -- $best_default
        while [ "$#" -gt 0 ]; do
            case "$1" in
                via)
                    shift
                    gateway="${1:-}"
                    ;;
                dev)
                    shift
                    dev="${1:-}"
                    ;;
            esac
            shift || true
        done

        if [ -z "$dev" ]; then
            echo "XBond route apply: selected default route has no device: $best_default" >&2
            exit 1
        fi

        if [ -n "$gateway" ]; then
            ip route replace "$server_host/32" via "$gateway" dev "$dev" metric 1
        else
            ip route replace "$server_host/32" dev "$dev" metric 1
        fi
        ;;
esac

if [ "$ACTION" = "apply" ]; then
    apply_local_bypass_routes
    traffic_mode="$(awk -F= '
        /^[[:space:]]*traffic_mode[[:space:]]*=/ {
            value = $2
            gsub(/^[[:space:]]+|[[:space:]]+$/, "", value)
            gsub(/^"|"$/, "", value)
            print value
            exit
        }
    ' "$CONFIG_PATH")"
    case "$traffic_mode" in
        direct-failover|adaptive)
            /usr/local/sbin/xbond-client-egress bootstrap --config "$CONFIG_PATH"
            ;;
        *)
            ip route replace default dev "$TUN_IF" src "$TUN_SRC" metric 1
            ;;
    esac
fi
