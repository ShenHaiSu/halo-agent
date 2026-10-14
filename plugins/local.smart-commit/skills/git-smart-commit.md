---
name: git-smart-commit
description: 用户要求提交代码、智能提交、拆分commit、commit后检查并push时使用
---

# Smart Commit 智能提交

按 diff 快速分组、拆分为一个或多个本地 commit，审计无敏感物后再 push。

## 1. 先判定 git 仓库

1. 运行 `git rev-parse --is-inside-work-tree`。
2. 非 git 仓库 / 报错：立即停止，不执行任何 diff、add、commit、push，并告诉用户原因。
3. 是 git 仓库才继续。

## 2. 看 diff，只做分组级分析

依次运行，目的是分组，不做全量代码深读：

```bash
git status --short
git diff --stat
git diff
git diff --cached --stat
git log --oneline -5
```

- 同时关注：已暂存、未暂存、未跟踪（untracked）文件。
- 按“模块 / 功能 / 修复类型”快速归类即可，例如：同一功能的前后端改动归一组，无关联的重构、修 bug、文档各自分组。
- 二进制 / 大文件 / 疑似密钥先标记，留到第 5 步处理。

## 3. 拆分并提交到本地

原则：一个 commit 只做一件事。无关联改动必须拆开；可独立回滚、可独立描述的就拆。

流程（从第一组开始逐组执行）：

```bash
git add -- <该组文件路径...>
git commit -m "<title>" -m "<Description>"
```

- 未跟踪文件只有确认属于该组才 `git add`；不确定的先问用户。
- 严格用按路径 `git add -- <path>` 精确暂存，禁止 `git add -A` / `git add .` 一把梭（除非 diff 已确认就是单一分组）。
- 每个 commit 必须有 title + Description：
  - title：一行，≤50 字符，动词开头，说清“做了什么”，如 `修复登录态过期后无限跳转` / `feat(auth): refresh token on 401`。
  - Description：1-3 行，说明背景 / 变更点 / 影响面。不要贴整段 diff。
- 全部未提交内容处理完才算结束，用 `git status --short` 确认。

### 禁止携带共同开发者信息（硬性）

- 禁止在 message 中加 `Co-authored-by`、`Co-Committed-by`、`Signed-off-by`、`Author:`、`Reviewed-by` 等任何他人署名 trailer。
- 禁止 `git commit --trailer` / `-m "Co-authored-by:..."` / 从其他 commit 复制 message 带来他人信息。
- 禁止 `git -c user.name=他人`、`-c user.email=他人`、`--author=他人` 提交。
- 提交前可用 `git config user.name; git config user.email` 确认是本机身份；提交后抽查 `git log -1 --format=%an/%ae%n%B` 确认无上述 trailer。

## 4. 全 commit 后的安全审计（push 前必做）

```bash
git status --short
git log --oneline -5
git show --stat HEAD
# 有多个新 commit 时：
git log --stat -n <本次commit数>
```

按以下清单检查本次所有新 commit（含新增文件内容）：

1. 密钥与凭证：`AKIA`、`sk-`、`ghp_`、`xoxb-`、`-----BEGIN .*PRIVATE KEY-----`、`api[_-]?key`、`password\s*=`、`.env`、`credentials.json`、`id_rsa`、`*.pem` 等。可用 `git grep -n -i -E "apikey|api_key|secret|password|BEGIN .*PRIVATE KEY" HEAD~<n>..HEAD` 辅助。
2. 不应提交的二进制 / 产物：`exe/dll/so/dylib/bin/jar/war/pdb/node_modules/dist/build/coverage/*.log/*.sqlite/*.db`，以及误加的大体积媒体 / 安装包。判定：`git show --numstat` 中 `- -` 的二进制项逐个确认。
3. 不应提交的本地配置：`.idea/`、`.vscode/`（除非项目约定）、本地绝对路径、内网 IP、客户数据。
4. 遗留：`git status --short` 还有未处理文件就先分类——该进本次的回第 3 步补 commit，不该进的保持未跟踪或加 `.gitignore`，绝不顺手塞进最后一个 commit。

## 5. 有问题回炉，没问题 push

- 有问题：根据严重程度 `git reset --soft HEAD~<k>` 退回重分、或 `git rm --cached -- <误加文件>` + 补 `.gitignore` + 重写 message 后重新 commit，然后重走第 4 步。已提交的密钥即使删掉也要提醒用户去服务端轮换（rotate），历史仍可找回。
- 无问题：只 push 当前分支，不切分支、不推全部分支：

```bash
git rev-parse --abbrev-ref HEAD
git remote -v
git push origin HEAD
```

- 无 upstream 时可用 `git push -u origin HEAD`；禁止默认 `--force`，强推必须经用户明确同意。
- push 后报告：每个 commit 的 title + 文件分组 + push 的分支，下一步建议（如提 PR）一句话即可。
