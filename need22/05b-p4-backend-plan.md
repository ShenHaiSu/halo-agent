# need22-05b P4 后端工程化施工方案

> 对应诊断：[05a](./05a-p4-backend-diagnosis.md)。只改构建与规范，不改运行时。

## 1. 前置检查

- [ ] `git status` 干净；三处版本号抄表（见 05a §1）。
- [ ] 确认 CI 运行环境（Windows runner + .NET 6.0.428）。

## 2. H1–H2 集中管控

* **H1** 新增 `develop/Directory.Build.props`：统管 `TFM/Nullable/LangVersion/WarnAsError/VersionPrefix`；各 csproj 删除重复项。
* **H2** 新增 `develop/Directory.Packages.props` + `NuGet.config`：`CefSharp/Sqlite/ProtectedData/xunit` 中央版本；`TreatWarningsAsErrors`（包降级即错）。
* **H7** `SelfContained` 对齐：改为与发布一致（`true`）或 `Debug=false/Release=true`。
* **版本**：`installer.iss` 改 `/DMyAppVersion` 传入，与 Assembly 对齐；CI 打包时注入 `git tag`。

## 3. H3–H4 格式化 + CI

* **H3** 新增 `.editorconfig`（C#：4 空格、`var` 规则、换行规则）+ `.config/dotnet-tools.json(csharpier)`。
* **H4** 新增 `.github/workflows/backend.yml`：
  ```yaml
  steps: clean → dotnet build --no-restore → dotnet test
    → dotnet publish -r win-x64 --self-contained true
    → assert PublishSingleFile != true
  ```

## 4. H5–H9 发布与卫生

* **H5** `build/clean.cmd`：清 `bin/obj/artifacts/dist`；CI 首步调用。
* **H6** 签名 + sbom：`signtool` 时间戳（无证书先自签占位，CI 输出签名状态）；`Microsoft.Sbom.Targets` 出 sbom；iss 含版本与 sbom 链接。
* **H8** README 备案：net6-EOL + Win7 保留原因 + 升级触发条件。
* **H9** `MainForm.cs` 孤儿扫描加取消 + 异常进日志（不吞）。

## 5. 提交切分

```text
h1-build-props → h2-packages → h3-format → h4-ci → h5-clean-sign → h8-readme-h9-mainform
```
