# tools/

开发辅助脚本。

## push-via-api.sh

**用途**：在 `git push` 不可用时，通过 GitHub **git-data API** 推送分支。

**何时需要**：本机 `git push` 会出现以下任一情况时——
- 挂起无输出（等待凭据交互，`gh` 的 token 与 git 凭据助手不通用）
- `Recv failure: Connection was reset`
- `failed to connect to github.com`

**用法**：
```bash
BASE=<base-commit-sha> BRANCH=<分支名> bash tools/push-via-api.sh
```

**两个必踩的坑**（脚本已处理）：
1. 大 JSON 必须用 `gh api --input <文件>`，用 `-f` 会超命令行长度限制
   （13 个文件的 tree JSON 报 `Argument list too long`）。
2. `git/commits` 的 `parents` 必须是**数组** `["sha"]`，
   传字符串报 HTTP 422。
