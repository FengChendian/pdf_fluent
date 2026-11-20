# Fluent PDF

Fluent PDF 是一款基于 Avalonia 和 PDFium 的桌面 PDF 阅读器，采用 Fluent 设计风格。项目目前提供 Windows x64 自包含安装包。

## 主要功能

- 多标签阅读、最近文档与收藏管理
- 页面虚拟化渲染、连续滚动与缩放
- 目录导航、阅读位置恢复
- 文本选择、复制与全文搜索
- PDF 高亮标注
- DPI 感知的页面渲染和位图缓存

## 技术栈

- .NET 10
- Avalonia 12
- CommunityToolkit.Mvvm
- PDFiumCore
- xUnit 与 Avalonia Headless

## 本地开发

安装 .NET 10 SDK 后，在仓库根目录运行：

```bash
dotnet restore pdf_fluent.slnx
dotnet run --project src/pdf_fluent.csproj
```

运行测试：

```bash
dotnet test pdf_fluent.slnx --configuration Release
```

## Windows 发布

发布配置使用 Inno Setup 和 GitHub Actions，安装包包含 .NET 运行时，并支持可选的桌面快捷方式、开始菜单项及 PDF 文件关联。

版本号统一维护在 `src/pdf_fluent.csproj`，推送对应的 `v*` 标签即可触发发布。完整的本地打包和 GitHub Release 流程见 [Windows 发布流程](docs/RELEASING.md)。
