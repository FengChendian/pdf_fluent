# Windows 启动性能排查

本指南用于排查 Windows 自包含发布版的启动耗时和 PDF 打开异常。
发布命令见 [Windows 发布流程](RELEASING.md)。

## 启动与发布约定

- 发布保持 `PublishReadyToRun=true`，默认同时启用 Composite R2R。
  项目仅在指定运行时标识时默认启用 R2R；Composite 还要求自包含发布且 R2R 已启用。
  具体条件以 [`src/pdf_fluent.csproj`](../src/pdf_fluent.csproj) 为准。
- 命令行 PDF 的打开和启动更新检查从 `MainWindow.Opened` 开始执行，
  避免在框架初始化阶段同步打开文档而延迟窗口出现。
  入口见 [`App.axaml.cs`](../src/App.axaml.cs)。
- PDFiumCore 原生回调必须在整个原生调用期间持有托管委托的强引用，
  并在调用结束后执行 `GC.KeepAlive`。不要通过关闭 R2R 或排除 `PDFiumCore.dll`
  绕过回调生命周期问题。

## 采集启动日志

先退出已运行的实例，在发布目录的 PowerShell 中执行：

```powershell
$env:FLUENTPDF_STARTUP_TRACE = "1"
$env:FLUENTPDF_STARTUP_TRACE_FILE = "$PWD\startup.log"
.\pdf_fluent.exe
```

末行可追加 PDF 路径，测量从文件启动的场景。日志默认关闭；不指定日志路径时，
写入 `%TEMP%\fluentpdf-startup.log`。每次启用追踪启动都会覆盖目标日志，
多次测量时应分别保存结果。

实现见 [`StartupTrace.cs`](../src/Diagnostics/StartupTrace.cs)。主要观察：

- `Main.enter`：记录进程创建到进入托管入口的时间。
- `App.Initialize`、`MainViewModel`、`MainWindow`：定位主题加载、数据初始化和视图构造耗时。
- `MainWindow.FirstLayout`、`MainWindow.Opened`：观察初始布局和窗口打开的时间。
- `OpenDocumentAsync`、`DocumentTabViewModel.LoadAsync`、页面渲染：定位打开 PDF 的耗时。

日志中的 `+… ms` 从 `Main.enter` 开始累计，括号中是与前一埋点的间隔。
计算从进程创建开始的总耗时时，应加上 `Main.enter` 行的 `since process start`。

对比前后版本时保持机器、发布配置和输入文件一致，分别记录首次启动和重复启动，
不要混用 Debug 运行、发布目录运行或单实例转发的结果。根据日志确定瓶颈后再优化。

## PDF 打开异常的排查

曾出现的 R2R 发布版打开 PDF 后退出，根因是 PDFiumCore 的回调委托被 GC 回收。
`IFSDK_PAUSE.NeedToPauseNow`、`FPDF_FILEWRITE_.WriteBlock` 等生成的属性只保存
原生函数指针，不持有托管委托；后续原生回调可能触发 `Environment.FailFast`。
R2R 改变执行和 GC 时机，暴露了这个问题。

当前修复见 [`PDFiumService.cs`](../src/Services/PDFiumService.cs)：渲染和保存路径均
使用具名委托变量，并在原生调用结束后 `GC.KeepAlive`。后续重构必须保持此生命周期；
仅保活 `IFSDK_PAUSE` 或 `FPDF_FILEWRITE_` 包装对象并不能替代保活委托。

- **进程退出**：检查 Windows 事件查看器的应用程序日志。若出现
  `callback was made on a garbage collected delegate`，检查相关原生回调的保活范围。
- **进程仍在但无响应**：采集进程转储，检查 UI 线程、渲染线程和锁等待，
  区分托管线程阻塞与原生 PDFium 调用停滞。

## 修改启动路径或发布配置后的验证

1. 运行 `dotnet test pdf_fluent.slnx --configuration Release`。
2. 按 [Windows 发布流程](RELEASING.md) 生成自包含发布目录，重复采集启动日志，
   比较初始布局和窗口打开时间；测试 Composite 时可临时关闭 Composite 做单变量对照，
   同时保持 R2R 开启。
3. 重复验证空启动、命令行打开多页 PDF，以及已运行时双击 PDF 的单实例转发。
   可使用 `tests/pdf_fluent.Tests/assets/CUDA_by_Example.pdf`，确认窗口可响应、
   页面能渲染，且没有新的回调委托崩溃。
