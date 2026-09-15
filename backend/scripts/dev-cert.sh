#!/bin/sh
# Generates a self-signed TLS certificate for localhost development into backend/certs/ (gitignored).
# Usage: sh scripts/dev-cert.sh [hostname]   then:
#   AVAIBE_TLS_CERT=certs/dev-cert.pem AVAIBE_TLS_KEY=certs/dev-key.pem node server.js
set -eu
HOST="${1:-localhost}"
DIR="$(cd "$(dirname "$0")/.." && pwd)/certs"
mkdir -p "$DIR"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 365 \
  -keyout "$DIR/dev-key.pem" -out "$DIR/dev-cert.pem" \
  -subj "/CN=$HOST" -addext "subjectAltName=DNS:$HOST,DNS:localhost,IP:127.0.0.1" >/dev/null 2>&1
chmod 600 "$DIR/dev-key.pem"
echo "Wrote $DIR/dev-cert.pem and $DIR/dev-key.pem (CN=$HOST, valid 365 days)."
echo "Run: AVAIBE_TLS_CERT=$DIR/dev-cert.pem AVAIBE_TLS_KEY=$DIR/dev-key.pem node server.js"
echo "Clients must trust this certificate (or use http://localhost during development)."
