# 踩坑日志（pitfalls）

> 定位：只记录**已闭环、有复用价值**的坑。过程文档（needXX / develop）不直接搬进来，提炼后才落这里。
> 该目录纳入版本跟踪（`.gitignore` 已放行 `/docs/pitfalls/**`），其他 `docs/*` 默认忽略。

## 命名规范

`YYYYMMDD-简短标题.md`，例如 `20261019-cefsharp-109-win7-黑屏.md`

## 模板（复制使用）

```md
# 标题
- 日期：
- 环境：Win7 SP1 x86 / Win10 x64 / .NET6 / CefSharp 109.1.110 / Node xx
- 现象：
- 复现步骤：
- 根因：
- 解法（最小改动）：
- 关联提交 / PR：
- 教训（下次如何避开）：
```

## 与过程文档的关系

- `docs/needXX/`、`docs/develop/`、`docs/toolDocs/` = 过程稿，落地 + 验收通过后删除，不进历史。
- 有价值的结论提炼成一篇 pitfalls，而不是整目录保留。
