#!/bin/sh
# Runs the manager (and with it the game server) as PUID:PGID, so files in /data can belong to
# a user on the host. Defaults to the image's built-in app user (1654). When the container is
# started as a non-root user (e.g. `user:` in compose), that user is used as it is.
set -eu

if [ "$(id -u)" != "0" ]; then
    exec "$@"
fi

PUID="${PUID:-1654}"
PGID="${PGID:-$PUID}"

case "$PUID$PGID" in
    *[!0-9]*)
        echo "PUID and PGID must be numeric (got PUID=$PUID PGID=$PGID)." >&2
        exit 1
        ;;
esac

mkdir -p /data

# A new, empty data folder is handed to the runtime user. Existing data is never re-owned
# automatically: it may be shared with other things on the host.
if [ -z "$(ls -A /data 2>/dev/null)" ]; then
    chown "$PUID:$PGID" /data
fi

as_user() {
    setpriv --reuid="$PUID" --regid="$PGID" --clear-groups -- "$@"
}

if ! as_user sh -c 'probe=/data/.write-test.$$ && touch "$probe" && rm -f "$probe"' 2>/dev/null; then
    owner="$(stat -c '%u:%g' /data)"
    echo "Can't write to /data as $PUID:$PGID (the folder belongs to $owner)." >&2
    echo "Either set PUID/PGID to match the data (PUID=${owner%%:*} PGID=${owner##*:})," >&2
    echo "or change the owner on the host: chown -R $PUID:$PGID <your data folder>" >&2
    exit 1
fi

for dir in /data/*/; do
    [ -d "$dir" ] || continue
    owner="$(stat -c '%u' "$dir")"
    if [ "$owner" != "$PUID" ]; then
        echo "Warning: $dir belongs to UID $owner, not PUID $PUID; files in it may not be writable." >&2
    fi
done

echo "Running as UID $PUID, GID $PGID"
export HOME=/tmp
exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups -- "$@"
