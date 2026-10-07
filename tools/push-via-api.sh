#!/usr/bin/env bash
# 通过 GitHub git-data API 推送分支。
# 动机：本机 git 传输层推送会「Connection was reset」，且带 token 的 URL 同样失败。
# git-data API 走 HTTPS API 通道，绕过 git 传输层问题。
# 注意：所有大 JSON 必须走 --input 文件，不能用 -f（命令行长度限制）。
set -euo pipefail

REPO="YYiChen/XAssistant"
LOCAL="C:/Users/32126/WorkBuddy/2026-10-02-16-09-38/XAssistant"
BRANCH="feat/personal-fork"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

cd "$LOCAL"

PY="C:/Users/32126/.workbuddy/binaries/python/versions/3.13.12/python.exe"

# BASE 取「远程该分支当前最新提交」，实现增量推送。
# 若分支尚不存在，回退到 master 的最新提交。
# 注意：不能用本地 SHA 反查 —— 经 git-data API 创建的 commit 在本地对象库中
# 不存在（git rev-parse 会报 bad object）。
if [ -z "${BASE:-}" ]; then
  BASE=$(gh api "repos/$REPO/git/ref/heads/$BRANCH" --jq '.object.sha' 2>/dev/null || true)
fi
if [ -z "$BASE" ]; then
  echo "远程分支 $BRANCH 不存在，base 回退到 master"
  BASE=$(gh api "repos/$REPO/commits/master" --jq '.sha')
fi
echo "      BASE = $BASE"

echo "[1/6] 取 base tree"
BASE_TREE=$(gh api "repos/$REPO/git/commits/$BASE" --jq '.tree.sha')
echo "      $BASE_TREE"

echo "[2/6] 收集改动文件并创建 blob"
# 以本地 master 为参照收集全部改动（所有本地提交都基于 master）。
# 不能用 $BASE 做 diff：它是 GitHub 上的 SHA，本地对象库里不存在
# （经 API 创建的 commit 在本地不可见，git rev-parse 会报 bad object）。
LOCAL_REF="${LOCAL_REF:-master}"
FILES=$(git diff --name-only "$LOCAL_REF" HEAD)
echo "      参照 $LOCAL_REF，共 $(echo "$FILES" | grep -c . ) 个文件"
ENTRIES="[]"
while IFS= read -r f; do
  [ -z "$f" ] && continue

  # 已删除的文件：本地不存在，无法生成 blob。
  # 在 git-data API 中，tree 条目把 sha 设为 null 即表示删除该路径。
  if [ ! -f "$f" ]; then
    ENTRIES=$("$PY" -c "
import json,sys
e=json.loads(sys.argv[1])
e.append({'path':sys.argv[2],'mode':'100644','type':'blob','sha':None})
print(json.dumps(e))
" "$ENTRIES" "$f")
    echo "      $f -> (已删除)"
    continue
  fi

  "$PY" -c "
import base64,json,sys
data=open(sys.argv[1],'rb').read()
json.dump({'content':base64.b64encode(data).decode(),'encoding':'base64'},open(sys.argv[2],'w'))
" "$f" "$TMP/blob.json"
  SHA=$(gh api "repos/$REPO/git/blobs" --method POST --input "$TMP/blob.json" --jq '.sha')
  MODE=$(git ls-tree HEAD "$f" | awk '{print $1}')
  ENTRIES=$("$PY" -c "
import json,sys
e=json.loads(sys.argv[1])
e.append({'path':sys.argv[2],'mode':sys.argv[3],'type':'blob','sha':sys.argv[4]})
print(json.dumps(e))
" "$ENTRIES" "$f" "$MODE" "$SHA")
  echo "      $f -> ${SHA:0:7}"
done <<< "$FILES"

echo "[3/6] 创建 tree"
"$PY" -c "
import json,sys
json.dump({'base_tree':sys.argv[1],'tree':json.loads(sys.argv[2])},open(sys.argv[3],'w'))
" "$BASE_TREE" "$ENTRIES" "$TMP/tree.json"
TREE=$(gh api "repos/$REPO/git/trees" --method POST --input "$TMP/tree.json" --jq '.sha')
echo "      $TREE"

echo "[4/6] 创建 commit"
"$PY" -c "
import json,subprocess,sys
msg=subprocess.run(['git','log','-1','--pretty=%B'],capture_output=True,text=True).stdout
json.dump({'message':msg,'tree':sys.argv[1],'parents':[sys.argv[2]]},open(sys.argv[3],'w'))
" "$TREE" "$BASE" "$TMP/commit.json"
COMMIT=$(gh api "repos/$REPO/git/commits" --method POST --input "$TMP/commit.json" --jq '.sha')
echo "      $COMMIT"

echo "[5/6] 更新分支 ref（存在则 PATCH，不存在则 POST）"
"$PY" -c "
import json,sys
json.dump({'sha':sys.argv[1],'force':False},open(sys.argv[2],'w'))
" "$COMMIT" "$TMP/ref.json"
if gh api "repos/$REPO/git/ref/heads/$BRANCH" >/dev/null 2>&1; then
  gh api "repos/$REPO/git/refs/heads/$BRANCH" --method PATCH \
     --input "$TMP/ref.json" --jq '.ref + "  ->  " + .object.sha'
else
  "$PY" -c "
import json,sys
json.dump({'ref':'refs/heads/'+sys.argv[1],'sha':sys.argv[2]},open(sys.argv[3],'w'))
" "$BRANCH" "$COMMIT" "$TMP/ref.json"
  gh api "repos/$REPO/git/refs" --method POST --input "$TMP/ref.json" \
     --jq '.ref + "  ->  " + .object.sha'
fi

echo "[6/6] 验证"
gh api "repos/$REPO/branches/$BRANCH" --jq '"分支: " + .name + "  最新提交: " + .commit.sha'

echo "推送完成"
