#!/bin/sh
# Zip extraction can drop the executable bit, so restore it before launching.
cd "$(dirname "$0")" || exit 1
chmod +x ./DoriDeck
exec ./DoriDeck
