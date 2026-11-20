# Windows 发布流程

本文档说明如何为 Fluent PDF 构建并发布自包含的 Windows x64 安装包。目前只维护 `win-x64` 发布，不生成其他平台的安装包。

## 发布组成

| 文件 | 用途 |
| --- | --- |
| `src/pdf_fluent.csproj` | 唯一版本号来源 |
| `installer/PDFFluent.iss` | Inno Setup 安装包配置 |
| `.github/workflows/release-win-x64.yml` | 测试、发布、打包及 GitHub Release 自动化 |

发布工作流依次执行：

1. 校验 Git 标签与项目版本号一致。
2. 运行完整测试套件。
3. 发布自包含的 `win-x64` 应用。
4. 使用 Inno Setup 编译安装包。
5. 上传 GitHub Actions 构建产物。
6. 创建对应的 GitHub Release 并附加安装包。

普通分支推送不会触发发布，只有推送 `v*` 标签才会触发。

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
  -p:DebugType=embedded `
  --output artifacts/publish/win-x64
```

保留完整发布目录，不启用裁剪或单文件发布，以确保 Avalonia 和 PDFium 原生依赖完整可用。

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
- **PDF 文件关联**：可选，默认不勾选。
- **安装完成后启动**：可选，默认不勾选。

应用默认安装到 `Program Files\Fluent PDF`，因此安装和卸载需要管理员权限。

## PDF 文件关联说明

安装程序会注册 Fluent PDF 的 `.pdf` 打开能力，使其可以出现在 Windows 的“打开方式”和默认应用列表中。只有用户勾选 PDF 关联任务时，才会尝试将 `.pdf` 的默认 ProgID 设置为 Fluent PDF。双击 PDF 时，Windows 将文件路径作为启动参数传给应用，应用启动后会打开参数中的全部 PDF 文件并忽略其他类型的参数。

应用显示名称由程序集产品信息、`Applications\pdf_fluent.exe` 友好名称和默认应用注册共同提供，统一为 **Fluent PDF**。安装程序会清理旧版 `PDF Fluent` 的注册项和快捷方式；Windows 若仍缓存旧名称，重新登录或重启资源管理器后再检查。

Windows 10/11 可能通过用户的 `UserChoice` 保留原来的默认 PDF 应用，因此无法保证安装程序在所有系统上静默更改默认应用。用户可能仍需在 Windows“默认应用”设置中确认。

当前关联配置会覆盖 `.pdf` 的现有 ProgID，卸载时删除安装程序写入的值，但不会备份并恢复之前的关联。修改关联策略时应特别测试已安装其他 PDF 阅读器的系统。

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
