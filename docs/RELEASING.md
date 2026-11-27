# Windows 发布流程

本文档说明如何为 Fluent PDF 构建并发布自包含的 Windows x64 安装包。目前只维护 `win-x64` 发布，不生成其他平台的安装包。

## 发布组成

| 文件 | 用途 |
| --- | --- |
| `src/pdf_fluent.csproj` | 唯一版本号来源 |
| `installer/PDFFluent.iss` | Inno Setup 安装包配置 |
| `.github/workflows/release-win-x64.yml` | 发布、打包、GitHub Release 及 R2 自动化 |
| `cliff.toml` | 根据 commit 自动生成 Release 正文的分类与模板 |

发布工作流依次执行：

1. 校验 Git 标签与项目版本号一致。
2. 测试步骤当前注释，发布不会自动运行测试。
3. 发布自包含的 `win-x64` 应用（启用 ReadyToRun / Composite R2R）。
4. 使用 Inno Setup 编译安装包。
5. 上传 GitHub Actions 构建产物。
6. 创建对应的 GitHub Release 并附加安装包。
7. 上传安装包到 R2，成功后发布更新清单，最后仅保留版本号最高的三个安装包。

普通分支推送不会触发发布，只有推送 `v*` 标签才会触发。

## 自动发布说明

版本校验通过后，工作流使用 git-cliff 2.14.2，根据当前标签与上一个稳定版本标签之间的
commit 自动生成 Release 正文。首次发布包含当前标签之前的全部提交；重跑旧标签时仍生成该标签的说明。
分类与展示格式统一维护在根目录 `cliff.toml`，不依赖 PR 或 PR 标签。

提交格式使用 `<type>[可选 scope]: <说明>`，例如：

```text
feat(reader): 新增高亮颜色选择
fix(update): 修复更新后启动路径
perf(render): 减少页面渲染分配
```

正文按新功能、问题修复、性能优化、代码重构、文档、测试、构建与维护、回退、其他变更分类，
保留提交链接及 Full Changelog 比较链接。未按约定命名的提交进入“其他变更”，不被静默丢弃。
`!` 或 `BREAKING CHANGE:` 标记的不兼容变更会突出显示。正文使用原始提交说明，不自动翻译。

生成的 `release-notes.md` 只写入 CI 的临时目录，再通过 `body_path` 交给 Release action；
不提交到仓库，也不需要为每个版本维护 Markdown 文件。发布前仍只需更新版本号、提交、推送标签。
GitHub 原生 `generate_release_notes` 已关闭，避免重复生成基于 PR 的内容。

## R2 更新源

工作流使用 GitHub Environment `CLOUDFLARE_TOKEN`，其中必须配置 Environment secrets：

- `API_URL`：带桶名的完整 S3 地址，例如 `https://<account-id>.r2.cloudflarestorage.com/pdf-fluent`。
- `API_TOKEN`：拥有该桶对象读写权限的原始 R2 API Token；不是 S3 Secret Access Key。

公开下载域名为 `https://pdf-fluent.yichen.xyz`。R2 公开域名不能列举桶内对象，因此应用读取
`https://pdf-fluent.yichen.xyz/config.json` 检查更新，不再依赖 GitHub Atom。发布工作流自动生成：

```json
{
  "schemaVersion": 1,
  "version": "0.11.0",
  "downloadUrl": "https://pdf-fluent.yichen.xyz/Fluent%20PDF%20Setup%200.11.0%20win-x64.exe",
  "size": 123456789,
  "sha256": "<安装包 SHA-256，64 位十六进制>",
  "publishedAt": "2026-11-27T00:00:00.0000000+00:00"
}
```

清单在对应安装包上传及大小验证成功后才上传，上传失败不会执行旧版本清理。
重跑旧标签不会回退清单。安装包按版本号降序保留三个，`config.json` 和其他对象不参与删除。
安装包有长期缓存，因此已发布的版本号和文件内容应保持不变；修改后使用新版本号。
清单使用 `Cache-Control: no-store, max-age=0`，客户端也请求跳过缓存。
如果域名配置了强制缓存规则，请为 `/config.json` 设置绕过缓存，避免旧清单指向已淘汰的安装包。

## 应用内安装更新

设置中的“下载并更新”会从公开域名流式下载安装包，同时检查大小和 SHA-256。
下载完成后先确认未保存的 PDF 高亮；取消或保存失败时保持应用打开，并回收本次下载文件。
确认关闭后，独立 Windows PowerShell 辅助进程等待阅读器完全退出，再启动 Inno Setup 静默更新。
使用 `/SILENT` 并正常显示安装器窗口：跳过安装向导，显示 Inno 自带的安装进度条；
PowerShell 辅助进程仍隐藏运行。不要改为 `/VERYSILENT` 或隐藏安装器窗口，否则安装进度将不可见。
不传入 `/DIR`，由 Inno Setup 的 `UsePreviousAppDir=yes` 自动识别已有安装目录；
未安装时使用安装器默认目录。只提升安装器权限，安装成功后辅助进程从 HKLM 的 64 位卸载注册表中，
按安装器 `AppId` 查找 `InstallLocation`，再以原用户权限启动该目录中的 `pdf_fluent.exe`。
注册表中的 AppId 必须与 `installer/PDFFluent.iss` 保持一致。不会根据调用者的 EXE 路径决定安装或启动位置，
因此从 Debug 构建（包括 `dotnet` 启动）触发时也会更新正式安装目录，完成后启动正式版。
Windows 的 UAC 确认仍需用户操作；取消 UAC、安装失败或无法启动时会提示用户手动打开应用。
不要求或触发系统自动重启；如果安装器报告必须重启 Windows，则提示后暂不启动阅读器。

辅助进程会重试删除安装包、自身脚本和交接标记。安装日志保留在
`%TEMP%\FluentPDF-Updates\<本次更新 ID>\update.log`，便于定位安装故障。
安装包下载到同一目录中的 `installer.exe`，不使用 Windows 的“下载”文件夹；
实际临时目录由 `Path.GetTempPath()` 决定，通常为 `%LOCALAPPDATA%\Temp`。
在资源管理器地址栏输入 `%TEMP%\FluentPDF-Updates` 即可查看各次更新。
安装结束并等待辅助进程清理后，正常应只剩 `update.log`；目录和日志保留不代表安装包未删除。
可在 PowerShell 中检查是否有残留安装包（无输出表示当前没有）：

```powershell
Get-ChildItem -LiteralPath (Join-Path ([System.IO.Path]::GetTempPath()) 'FluentPDF-Updates') `
    -Filter installer.exe -Recurse -File -ErrorAction SilentlyContinue |
    Select-Object FullName, Length, LastWriteTime
```

清理对每个文件最多尝试 10 次，失败后间隔 500 毫秒；文件持续被占用等情况下仍可能残留，
当前 `update.log` 不记录清理结果，因此应以文件是否实际存在为准。
企业策略禁用 Windows PowerShell 时，辅助进程无法就绪，阅读器会保持打开并提示更新失败。
当前方案沿用既有 Inno Setup 安装标识和路径，属于全量安装包更新，不提供增量更新或自动回滚。

首次发布此功能后，旧版仍使用原来的 GitHub 下载页面；安装一次包含此功能的新版后，
后续版本才会使用 R2 清单和应用内下载安装流程。发布前仍需更新项目版本号，并让标签指向包含改动的提交。

设计依据：[R2 公开桶](https://developers.cloudflare.com/r2/buckets/public-buckets/)、
[Velopack 的发布清单模式](https://docs.velopack.io/distributing/overview)、
[Inno Setup 命令行参数](https://jrsoftware.org/ishelp/topic_setupcmdline.htm)。

## 启动性能约定

自包含 Windows 发布默认启用 ReadyToRun 和 Composite R2R。配置条件以
`src/pdf_fluent.csproj` 为准；不要通过关闭 R2R 绕过 PDFiumCore 回调委托的生命周期问题。
启动日志采集、回调保活要求和回归验证方法统一见 [启动性能排查](STARTUP-PERFORMANCE.md)。

发布保持 `PublishSingleFile=false` 和 `PublishTrimmed=false`。

## 版本号约定

版本号只维护在 `src/pdf_fluent.csproj`：

```xml
<Version>0.1.0</Version>
```

当前工作流接受 `主版本.次版本.修订号` 形式的稳定版本，例如 `0.2.0` 或 `1.0.0`，暂不接受预发布后缀。

.NET SDK 会根据 `Version` 自动生成程序集版本和文件版本，无需维护单独的 C# 版本文件。应用需要显示版本时可以读取程序集信息：

```csharp
public static string AppVersion =>
    typeof(App).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
```

Git 标签表示一次明确的发布操作，格式为 `v` 加项目版本号。例如项目版本为 `0.2.0` 时，标签必须是 `v0.2.0`。两者不一致时工作流会主动失败，不会生成版本混乱的安装包。

不要修改 `installer/PDFFluent.iss` 中已有的 `AppId`。Windows 和 Inno Setup 使用它识别同一应用的升级与卸载。

## 发布新版本

以下示例发布 `0.2.0`。

### 1. 更新版本号

将 `src/pdf_fluent.csproj` 中的版本修改为：

```xml
<Version>0.2.0</Version>
```

版本号不应回退，也不要重复使用已经发布过的版本和标签。

### 2. 提交并推送源码

```bash
git add -A
git commit -m "Release 0.2.0"
git push origin master
```

此时只更新源码，不会创建安装包。

### 3. 创建并推送发布标签

```bash
git tag -a v0.2.0 -m "Fluent PDF v0.2.0"
git push origin v0.2.0
```

推送标签后，在 GitHub 仓库的 **Actions** 页面查看 `Release Fluent PDF for Windows x64` 工作流。

### 4. 检查发布结果

工作流成功后应得到：

- GitHub Release：`Fluent PDF v0.2.0`
- 安装包：`Fluent PDF Setup 0.2.0 win-x64.exe`
- Actions 构建产物：`Fluent-PDF-0.2.0-win-x64`，保留 30 天

发布前建议下载 Release 中的安装包，在干净的 Windows x64 环境验证安装、启动、PDF 打开和卸载。安装包已包含 .NET 运行时，目标机器不需要另外安装 .NET SDK 或运行时。

## 本地构建安装包

### 前置条件

- Windows x64
- .NET 10 SDK
- Inno Setup 6
- PowerShell

Inno Setup 可通过 winget 安装：

```powershell
winget install --id JRSoftware.InnoSetup --exact --source winget
```

### 测试并发布应用

在仓库根目录运行：

```powershell
dotnet test pdf_fluent.slnx --configuration Release

dotnet publish src/pdf_fluent.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false `
  -p:PublishReadyToRun=true `
  -p:PublishReadyToRunComposite=true `
  -p:DebugType=embedded `
  --output artifacts/publish/win-x64
```

保留完整发布目录，不启用裁剪或单文件发布，以确保 Avalonia 和 PDFium 原生依赖完整可用。ReadyToRun 与 Composite R2R 的配置同时也写在 `src/pdf_fluent.csproj` 中，因此省略这两个参数时也会按项目默认值发布。

### 编译安装包

```powershell
$project = [xml](Get-Content -Raw "src/pdf_fluent.csproj")
$version = $project.SelectSingleNode("/Project/PropertyGroup/Version").InnerText.Trim()
$sourceDir = (Resolve-Path "artifacts/publish/win-x64").Path

New-Item -ItemType Directory -Force -Path "artifacts/installer" | Out-Null
$outputDir = (Resolve-Path "artifacts/installer").Path
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"

& $iscc `
  "/DAppVersion=$version" `
  "/DSourceDir=$sourceDir" `
  "/DOutputDir=$outputDir" `
  "installer/PDFFluent.iss"
```

安装包输出到 `artifacts/installer`。`artifacts` 已被 Git 忽略，不应提交生成结果。

## 安装选项

安装程序名称为 **Fluent PDF**，提供以下选项：

- **桌面快捷方式**：可选，默认不勾选。
- **开始菜单快捷方式**：可选，默认勾选，同时包含卸载入口。
- **PDF 文件关联**：安装时自动完成，不提供勾选项，也不再打开 Windows 默认应用设置。
- **安装完成后启动**：可选，默认不勾选。

应用默认安装到 `Program Files\Fluent PDF`，因此安装和卸载需要管理员权限。

## PDF 文件关联说明

安装程序会注册 Fluent PDF 的 `.pdf` 打开能力，并将 `.pdf` 的默认程序标识静默设置为 `PDFFluent.Document`，无需用户勾选或确认。`ChangesAssociations=yes` 会通知 Windows 刷新文件关联。

安装程序不会直接覆盖 `.pdf` 的 `UserChoice`。Windows 10/11 会校验当前用户的默认应用选择；如果用户此前已通过系统设置选择其他 PDF 应用，该用户级选择仍可能优先于安装程序写入的系统级关联。

应用显示名称由程序集产品信息、`Applications\pdf_fluent.exe` 友好名称和默认应用注册共同提供，统一为 **Fluent PDF**。安装程序会清理旧版 `PDF Fluent` 的注册项和快捷方式；Windows 若仍缓存旧名称，重新登录或重启资源管理器后再检查。

双击 PDF 时，Windows 将文件路径作为启动参数传给应用。首个实例打开参数中的全部 PDF 并忽略其他类型的参数；已经运行时，后续进程通过仅当前用户可访问的命名管道转发参数并退出，原窗口会激活并在同一个 `MainViewModel` 中新增标签。

PDF 类型的 `DefaultIcon` 指向安装目录中的 `Assets\pdf.ico`，关联后的 PDF 文件使用独立的文档图标；应用、快捷方式和“打开方式”仍使用 `logo.ico`。`pdf.ico` 由 `src/Assets/pdf.svg` 生成，包含多种尺寸。如果旧图标被缓存，重新登录或重启资源管理器后再检查。

## 常见问题

### 工作流提示标签与版本不一致

确认标签为 `v<Version>`。例如 `<Version>0.2.0</Version>` 对应 `v0.2.0`，不能使用 `0.2.0`、`v0.2` 或其他形式。

### 修改源码后没有触发发布

这是预期行为。只有推送标签才会发布，修改并推送 `csproj` 本身不会创建 Release。

### GitHub Release 已存在

不要复用已发布版本的标签。递增 `Version`，提交后创建对应的新标签。重新运行同一个成功版本的发布工作流前，应先确认现有 Release 和安装包是否需要保留。

### 找不到 ISCC.exe

确认 Inno Setup 6 已安装，并检查实际安装路径。GitHub 工作流和文档中的本地命令默认使用：

```text
C:\Program Files (x86)\Inno Setup 6\ISCC.exe
```

### 安装包体积较大

自包含发布会包含 .NET 运行时和 PDFium 原生依赖，因此体积明显大于依赖框架的发布。这是预期结果，不应通过裁剪或移除 `runtimes` 目录来缩小安装包。
