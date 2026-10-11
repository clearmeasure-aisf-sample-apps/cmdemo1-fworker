#!/bin/bash
# Writes the connect prompt of one gateway environment to a private file (mode 600), using the dashboard's own generator, so the file is
# exactly what the dashboard's "Create prompt" button produces. The worker credential is minted by the gateway and never printed.
# usage: gen-connect-prompt.sh <tdd|uat|prod> <name> <type> <capacity> <out-file> [gateway-url]
# The operator token is read from the keyring (service factory-worker, kind fworker-dev-token-<env>). Needs curl, node and python3 on THIS
# machine (the operator's); the worker machine needs nothing but what the prompt downloads.
set -euo pipefail
ENVN=$1; NAME=$2; TYPE=$3; CAP=$4; OUT=$5; URL=${6:-}
if [ -z "$URL" ]; then
  case "$ENVN" in
    tdd)  URL=https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io ;;
    prod) URL=https://ca-cmdemo1-prod-fworker-bhj5.happyisland-465ac2bf.southcentralus.azurecontainerapps.io ;;
    *) echo "give the gateway url for $ENVN" >&2; exit 2 ;;
  esac
fi
URL=${URL%/}
TOK=$(secret-tool lookup service factory-worker kind "fworker-dev-token-$ENVN")
mkdir -p "${XDG_CACHE_HOME:-$HOME/.cache}"; T=$(mktemp -d -p "${XDG_CACHE_HOME:-$HOME/.cache}"); trap 'rm -rf "$T"' EXIT
auth() { curl -fsS -H @<(printf 'Authorization: Bearer %s' "$TOK") "$@"; }
curl -fsS "$URL/dashboard" -o "$T/dashboard.html"
auth -X POST "$URL/api/v1/workers/$NAME/credential" -o "$T/cred.json"
auth "$URL/api/v1/dashboard" -o "$T/info.json"
auth "$URL/api/v1/qc" -o "$T/qc.json" || echo '{}' > "$T/qc.json"
T="$T" NAME="$NAME" TYPE="$TYPE" CAP="$CAP" OUT="$OUT" node -e '
const fs=require("fs"),e=process.env,rd=f=>fs.readFileSync(e.T+"/"+f,"utf8");
const m=rd("dashboard.html").match(/\/\*render-start\*\/([\s\S]*)\/\*render-end\*\//);
if(!m){console.error("no render block in the dashboard");process.exit(1)}
const info=JSON.parse(rd("info.json")),cred=JSON.parse(rd("cred.json")),qc=JSON.parse(rd("qc.json"));
const f=new Function(m[1]+"\nreturn buildPrompt;");
const text=f()({env:info.environment,url:info.url,token:cred.token,name:e.NAME,type:e.TYPE,cap:e.CAP,repo:qc.repo_url||"",qcProfile:""});
fs.writeFileSync(e.OUT,text+"\n",{mode:0o600});
'
chmod 600 "$OUT"
echo "wrote $OUT ($(wc -c < "$OUT") bytes, $ENVN, $NAME)"
