# tools/

开发与部署辅助脚本。

## push-via-api.sh

**用途**：在 `git push` 不可用时，通过 GitHub **git-data API** 推送分支。

**何时需要**：本机 `git push` 会出现以下任一情况时——
- 挂起无输出（等待凭据交互，`gh` 的 token 与 git 凭据助手不通用）
- `Recv failure: Connection was reset`
- `failed to connect to github.com`

**用法**：
```bash
bash tools/push-via-api.sh
```

**行为**：以远程该分支的当前最新提交为 base，推送本地 `master..HEAD` 的全部改动。
分支不存在时自动回退到 master 作为 base。ref 已存在用 PATCH，不存在用 POST。

**四个必踩的坑**（脚本已处理）：
1. 大 JSON 必须用 `gh api --input <文件>`，用 `-f` 会超命令行长度限制
   （13 个文件的 tree JSON 报 `Argument list too long`）。
2. `git/commits` 的 `parents` 必须是**数组** `["sha"]`，传字符串报 HTTP 422。
3. **不能用本地 SHA 做 base**：经 API 创建的 commit 在本地对象库中不存在，
   `git rev-parse` 报 `bad object`。改用远程分支当前 SHA 作 base，
   收集文件时以本地 `master` 为参照（`LOCAL_REF` 可覆盖）。
4. 分支已存在时 `POST /git/refs` 会失败，必须用 `PATCH /git/refs/heads/{branch}`。

**已知局限**：多次推送会把本地多个提交压成一个（squash）。
对个人 fork 无影响（内容完整），但历史不再是线性的。

## install.ps1

**用途**：把程序部署到固定目录并注册开机自启。

**为什么需要**：开发时自启项会指向
`bin\Release\net10.0-windows\win-x64\publish\XAssistant.exe` ——
一旦清理构建产物或移动项目目录，自启就失效。个人长期使用必须装到稳定路径。

**用法**：
```powershell
# 装到默认位置 C:\Program Files\XAssistant（需管理员权限）
powershell -ExecutionPolicy Bypass -File .\tools\install.ps1

# 装到自定义位置（无需管理员权限）
powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 `
    -InstallDir "$env:LOCALAPPDATA\XAssistant"
```

**参数**：
- `-InstallDir`：安装目录，默认 `C:\Program Files\XAssistant`
- `-SkipStart`：部署后不立即启动

**卸载**：删除安装目录 + 删除注册表项
`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` 下的 `XAssistant`。
