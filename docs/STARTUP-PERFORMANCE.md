# Windows 冷启动性能分析与优化

本文记录 Fluent PDF 在 Windows x64 自包含发布下的启动耗时拆解、ReadyToRun
异常的真实原因，以及已经落地和可以继续推进的优化措施。所有数字都在同一台
开发机（Windows 10.0.26300、.NET 10.0.14、Avalonia 12.1.4、PDFiumCore
157.0.8100）上测得，只用于比较相对差异，不代表用户机器的绝对值。

## 1. 结论

1. **冷启动慢的主因不是 PDFium 初始化。** `FPDF_InitLibrary()` 实测只有
   2～3 ms，原生 `pdfium.dll` 加载约 6 ms。把 PDFium 初始化延后不会带来明显收益。
2. **主要耗时在 Avalonia 平台初始化、XAML 视图构造/首帧布局和 JIT。**
   未启用 ReadyToRun 时，进程创建到主窗口 `Opened` 约 2.1 s；其中
   Avalonia 平台初始化约 0.55 s、主窗口 XAML 约 0.60 s、首帧布局约 0.49 s。
3. **ReadyToRun 本身可用，之前的“打开 PDF 卡死”是 PDFiumCore 回调委托的
   生命周期 bug。** `IFSDK_PAUSE.NeedToPauseNow` 的 setter 只保存原生函数指针，
   不持有托管委托；委托被 GC 回收后 PDFium 仍回调，运行时直接
   `Environment.FailFast`（异常码 `0x80131623`）。R2R 只是改变了 GC/启动时机，
   把这个潜在 use-after-free 稳定暴露出来。
4. **修复委托生命周期后可以安全启用 R2R。** 实测主窗口出现时间从约
   2.0 s 降到约 1.25 s（单个 R2R），再降到约 1.0 s（Composite R2R）。
5. **对“双击 PDF 启动”场景，还应把打开文档延后到首帧之后。** 原先在
   `OnFrameworkInitializationCompleted` 里立即打开命令行 PDF，会同步创建标签页、
   启动渲染线程并触发布局；实测使首帧晚约 0.3 s。现在改为窗口 `Opened` 后再打开。
6. **首次启动额外慢的一部分来自 Windows Defender/文件缓存。** 把同一份发布
   目录复制到新路径后第一次启动：未启用 R2R 约 2.96 s，Composite R2R 约
   1.42 s；同一路径第二次分别约 2.21 s 和 1.06 s。代码签名、减少安装文件数、
   避免单文件发布可以缓解，但不能完全消除杀毒软件首次扫描。

## 2. 测量方法

应用内置了可选启动追踪，默认不产生任何日志。设置环境变量后正常启动：

```powershell
$env:FLUENTPDF_STARTUP_TRACE = "1"
$env:FLUENTPDF_STARTUP_TRACE_FILE = "$PWD\startup.log"
.\pdf_fluent.exe
```

日志会从 `Process.StartTime` 开始记录 `Main.enter`、Avalonia 初始化、`App.axaml`
加载、`MainViewModel` 构造、`MainWindow` 构造、首帧布局和 `Opened` 等阶段。
需要更细的 CPU 采样时使用 `dotnet-trace`：

```powershell
dotnet-trace collect --profile dotnet-sampled-thread-time `
  --duration 00:00:03 -o startup.nettrace -- .\pdf_fluent.exe
dotnet-trace report .\startup.nettrace topN -n 50
```

## 3. 启动阶段拆解

下表中的“相对进程启动”把 `Main.enter` 之前的时间也算进去；数值在同一台机器
多次运行的波动约为 ±10%。

| 阶段 | 未启用 R2R | 单个 R2R | Composite R2R |
| --- | ---: | ---: | ---: |
| 进程创建 → `Main.enter` | 90 ms | 87 ms | 97 ms |
| `Main.enter` → `App.ctor`（含 `BuildAvaloniaApp` 与平台初始化） | 637 ms | 502 ms | 435～452 ms |
| `App.Initialize`（`App.axaml` + FluentTheme） | 80 ms | 35 ms | 25～31 ms |
| `MainViewModel` 构造 | 95 ms | 82 ms | 76 ms |
| `MainWindow` 构造（含 XAML/子视图） | 604 ms | 294 ms | 179～203 ms |
| 窗口构造完成 → 首帧 `LayoutUpdated` | 493 ms | 254 ms | 134～130 ms |
| 进程创建 → `MainWindow.Opened` | 约 2.10 s | 约 1.32 s | 约 1.00 s |

`MainViewModel` 内部还可以继续拆分：

| 子阶段 | 未启用 R2R | 单个 R2R | Composite R2R |
| --- | ---: | ---: | ---: |
| `new SettingsViewModel()`（含 `HttpClient`） | 13.4 ms | 11.5 ms | 10.5 ms |
| 最近文档 JSON + 缩略图解码 | 72.7 ms | 63.7 ms | 60.9 ms |
| 收藏文档 JSON + 缩略图解码 | 6.5 ms | 5.2 ms | 3.9 ms |
| `PDFiumService` 构造 + `FPDF_InitLibrary` | 2.7 ms | 2.9 ms | 2.6 ms |

结论很明确：

- PDFium 初始化不是瓶颈，**不要为了启动速度把 PDFium 改成完全懒加载**；
  收益只有几毫秒，却会让第一个 PDF 的打开变慢。
- 最大块是 Avalonia 平台初始化与主窗口视图构造/布局；R2R 对这两块都有效。
- 缩略图解码约 60～80 ms，属于可以继续优化、但不是首要问题的量级。

## 4. ReadyToRun 下“打开 PDF 卡死”的根因

### 4.1 现象

启用 `PublishReadyToRun=true` 后启动应用并打开一个 311 页的 PDF，进程在渲染
第 2 页附近直接退出（用户看到的是卡住/闪退）。Windows 事件查看器中
`.NET Runtime` 事件 ID 1025 的内容为：

```text
Description: The application requested process termination through System.Environment.FailFast.
Message: A callback was made on a garbage collected delegate of type
'PDFiumCore!PDFiumCore.Delegates.Func_int___IntPtr::Invoke'.
Stack:
   at PDFiumCore.fpdf_progressive.FPDF_RenderPageBitmapStart(...)
   at pdf_fluent.Services.PDFiumService.RenderPageIntoBuffer(...)
```

### 4.2 为什么委托会被回收

PDFiumCore 是自动生成的 P/Invoke 包装。反编译
`PDFiumCore.IFSDK_PAUSE` 可以看到：

```csharp
public unsafe Func_int___IntPtr NeedToPauseNow
{
    set
    {
        ((__Internal*)__Instance)->NeedToPauseNow =
            value == null ? IntPtr.Zero : Marshal.GetFunctionPointerForDelegate(value);
    }
}
```

`Marshal.GetFunctionPointerForDelegate` 只把委托转换成函数指针，**不会建立
GC 根**。原始代码是：

```csharp
using var pause = new IFSDK_PAUSE { Version = 1 };
pause.NeedToPauseNow = _ => cancellationToken.IsCancellationRequested ? 1 : 0;
```

lambda 在 setter 返回后就没有任何托管引用了。PDFium 在
`FPDF_RenderPageBitmapStart` / `FPDF_RenderPageContinue` 期间仍会回调这个地址；
一旦 GC 在这段时间发生，运行时就会检测到“回调了已回收的委托”并 FailFast。
未启用 R2R 时只是碰巧没有在渲染窗口内触发 GC，并不是代码正确。

### 4.3 修复

修复位于 `src/Services/PDFiumService.cs`：

```csharp
Func_int___IntPtr needToPauseNow = _ =>
    cancellationToken.IsCancellationRequested ? 1 : 0;

using var pause = new IFSDK_PAUSE { Version = 1 };
pause.NeedToPauseNow = needToPauseNow;

try
{
    // FPDF_RenderPageBitmapStart / Continue ...
}
finally
{
    fpdf_progressive.FPDF_RenderPageClose(page);
    fpdfview.FPDFBitmapDestroy(bitmap);
    GC.KeepAlive(needToPauseNow);
    GC.KeepAlive(pause);
}
```

同样的模式还存在于保存高亮的 `FPDF_FILEWRITE_.WriteBlock`，已一并修复：

```csharp
Func_int___IntPtr___IntPtr_ulong writeBlock = (pThis, pData, size) => { ... };
fileWrite.WriteBlock = writeBlock;

ok = fpdf_save.FPDF_SaveAsCopy(document, fileWrite, 0);

GC.KeepAlive(writeBlock);
GC.KeepAlive(fileWrite);
```

要点：

- 局部变量必须在**所有**原生调用结束之后仍然存活，所以 `GC.KeepAlive`
  放在 `finally` 或调用之后，而不是 setter 之后。
- 不要只加 `GC.KeepAlive(pause)`，真正需要保活的是委托对象。
- 不要通过 `PublishReadyToRunExclude` 排除 `PDFiumCore.dll` 来“修复”；
  那只是让时序退回到碰巧不崩的状态，内存压力下仍会崩溃。
- 同样的检查适用于所有 PDFiumCore 生成的委托属性，例如
  `IFSDK_PAUSE`、`FPDF_FILEWRITE_`、字体回调等。

### 4.4 如果修复后仍然卡死

先区分“进程崩溃”和“进程仍在但 UI 卡住”：

1. **进程退出/闪退**：打开“事件查看器 → Windows 日志 → 应用程序”，筛选
   `.NET Runtime` 事件 ID 1025 和 `Application Error` 1000。若仍是
   `callback was made on a garbage collected delegate`，继续检查其他
   PDFiumCore 委托属性；若堆栈在别处，按堆栈定位。
2. **进程还在但窗口无响应**：用任务管理器创建转储，或安装
   `dotnet-dump` 后执行 `dotnet-dump collect -p <pid>`，再用
   `dotnet-dump analyze <dump>` 的 `clrstack -all` 查看 UI 线程和渲染线程。
   重点检查 UI 线程是否等待 `PDFiumService._pageLock`、`Dispatcher` 或
   `SemaphoreSlim`，以及渲染线程是否停在原生 PDFium 调用内。
3. **需要二分 R2R 时**：先设 `-p:PublishReadyToRunComposite=false` 做单变量
   对比；仍有问题时再用 `PublishReadyToRunExclude` 排除单个程序集。不要把
   “排除 PDFiumCore”当作最终修复，除非能证明问题在该程序集且已有对应测试。

## 5. 已经落地的优化

### 5.1 启用 ReadyToRun + Composite R2R

`src/pdf_fluent.csproj`：

```xml
<PublishReadyToRun Condition="'$(RuntimeIdentifier)' != ''">true</PublishReadyToRun>
<PublishReadyToRunComposite Condition="'$(RuntimeIdentifier)' != ''">true</PublishReadyToRunComposite>
```

`RuntimeIdentifier` 条件保证普通 `dotnet build`/`dotnet test` 不受影响；
发布工作流和本地发布命令也显式传入了这两个参数。

为什么选 Composite R2R：

- 单个 R2R 把每个程序集编译成独立原生镜像，启动时仍要加载多个镜像；
- Composite 把它们合成一个 `pdf_fluent.r2r.dll`，减少原生镜像加载次数，
  实测再快约 0.25 s；
- 代价是发布目录增加约 27 MB（安装包压缩后通常增加更少），并且所有程序集
  必须一起参与 R2R。如果未来遇到某个程序集的兼容问题，可以先关闭 composite，
  保留单个 R2R，再用 `PublishReadyToRunExclude` 做二分定位。

### 5.2 首帧之后再打开命令行 PDF

原实现：

```csharp
var viewModel = new MainViewModel();
desktop.MainWindow = new MainWindow { DataContext = viewModel };
_ = OpenDocumentsAsync(desktop.Args);   // 立即开始
```

现在：

```csharp
desktop.MainWindow = mainWindow;

mainWindow.Opened += (_, _) =>
{
    _ = OpenDocumentsAsync(desktop.Args);
    _ = viewModel.CheckForUpdatesOnStartupAsync();
};
```

`Opened` 时首帧已经完成，窗口先显示主页框架，再加载 PDF 标签页和页面渲染。
实测带 PDF 参数启动时，首帧从约 1.22 s 提前到约 0.90 s（Composite R2R）；
后续打开 PDF 的约 0.2 s 页面渲染不再阻塞“界面出现”的感知。
`CheckForUpdatesOnStartupAsync` 也移到这里，避免在首帧前创建 `HttpClient`。

### 5.3 内置启动追踪

`src/Diagnostics/StartupTrace.cs` 提供 `FLUENTPDF_STARTUP_TRACE=1` 的诊断日志。
默认关闭；关闭时每个埋点只是一次静态 bool 判断。日志包含进程启动到
`Main.enter`、Avalonia 平台初始化、XAML、ViewModel、窗口构造和首帧的相对时间，
便于在用户机器上区分“运行时/杀毒软件慢”和“应用代码慢”。

## 6. 还可以继续做的优化

按收益/风险排序：

### P1：把最近文档缩略图解码移出首帧

`MainViewModel` 构造时会对最近 12 个文档逐个 `new Bitmap(stream)`，实测
60～80 ms。可以只保存压缩后的缩略图字节，首帧后在线程池解码，再回到
UI 线程给 `RecentDocumentViewModel.Thumbnail` 赋值。注意：

- `Bitmap` 的创建线程需要验证；如果平台渲染接口有线程亲和性，就在 UI 线程
  用 `DispatcherTimer`/`Background` 优先级分片解码。
- 现有测试会立即断言 `Thumbnail` 非空，改造后需要给测试提供显式
  `DecodeThumbnail()` 或等待异步初始化完成。

### P1：延迟创建隐藏的 SettingsView 和更新检查

`SettingsView` 即使在主页也总是被实例化（Composite R2R 下约 4～6 ms），
`SettingsViewModel` 的 `HttpClient` 约 10 ms。可以把 `SettingsView` 放进
`ContentControl` 的按需模板，或把 `GitHubUpdateCheckService` 改为首次检查时
再创建。收益约 15 ms，风险低，但需要调整 `SettingsViewTests`。

### P2：图标字体与 XAML 体积

`Optris.Icons.Avalonia.FontAwesome.dll` 约 4.35 MB，所有 `<i:Icon>` 在首次布局
时触发字体加载和文本 shaping。若启动预算非常紧，可以把实际用到的十几个图标
替换为 `StreamGeometry`/`Path`，同时移除字体包。这个改动视觉风险低，但工作量
大于 R2R，且需要逐页核对图标。

`MainWindow.axaml` 和 `HomeView.axaml` 已由 Avalonia XAML 编译器编译，不需要
再做“预编译 XAML”。进一步的收益主要来自减少首帧控件数量，而不是解析文本 XAML。

### P2：不要为了启动速度牺牲渲染

- `PublishSingleFile` 首次运行要把原生库解压到临时目录，通常更慢，且更容易
  触发杀毒软件；当前保持 `false` 是正确的。
- `PublishTrimmed`/Native AOT 可能进一步缩短启动时间，但 Avalonia、图标包和
  PDFiumCore 的反射/原生交互需要完整回归；在现阶段不建议直接用于稳定发布。
- `Win32PlatformOptions.RenderingMode` 强制软件渲染可能省掉 D3D11 初始化
  （本次采样中约几十毫秒），但会牺牲滚动/缩放流畅度，不建议作为默认。

### P2：首次启动与安装包

首次启动的额外耗时主要来自：

- Windows Defender 对安装目录中约 200 个 DLL/文件的首次扫描；
- Windows 文件缓存尚未预热；
- 安装器刚解压完文件，磁盘/CPU 仍在忙。

可做的措施：

- 对 `pdf_fluent.exe` 和安装包做 Authenticode 签名，减少 SmartScreen 拦截和
  未知发布者提示；签名不能关闭 Defender 扫描，但能改善首次分发体验。
- 继续在 Inno Setup 中排除 `*.pdb`（当前已排除）；不要把开发机的 PDB 带进
  安装目录。
- 控制安装文件数量：Composite R2R 会增加一个约 79 MB 的 `pdf_fluent.r2r.dll`，
  如果目标用户磁盘/杀毒环境很敏感，可以退回单个 R2R（安装体积更小，启动约慢
  0.25 s）。
- 不建议通过注册表给应用目录加 Defender 排除项；这不应由普通安装器静默完成。

## 7. 验证清单

改动启动路径或发布参数后，至少执行：

1. `dotnet test pdf_fluent.slnx --configuration Release` 全绿（当前 142 个测试）。
2. 用发布参数生成自包含目录，连续启动 5 次，记录 `FLUENTPDF_STARTUP_TRACE=1`
   的 `Opened` 时间，确认没有明显回退。
3. 连续 5 次用命令行传入一个多页 PDF（例如
   `tests/pdf_fluent.Tests/assets/CUDA_by_Example.pdf`），确认进程不退出、
   `Responding=True`，事件查看器没有新的 `.NET Runtime` 1025 事件。
4. 在干净 Windows 虚拟机/另一台机器上安装 Inno Setup 包，验证首次启动、
   第二次启动、从 PDF 文件双击启动、以及已运行时再次双击 PDF 的单实例转发。
5. 如果要调整 R2R 配置，先用 `PublishReadyToRunComposite=false` 做单变量对比；
   不要同时改 trimming、single-file 或渲染模式。

## 8. 一句话总结

**先把 PDFium 回调委托的生命周期修好，再启用 R2R（优先 Composite），
并把命令行 PDF 的打开推迟到首帧之后。** 这三件事分别解决了“R2R 打开 PDF
崩溃”“JIT/镜像加载慢”和“双击 PDF 时首帧被页面加载拖慢”三个不同问题；
继续优化缩略图和隐藏视图只是几十毫秒级的收尾工作。

## 参考

- [.NET ReadyToRun 部署概览](https://learn.microsoft.com/dotnet/core/deploying/ready-to-run)：
  R2R 的适用条件、Composite R2R 的体积/自包含限制以及
  `PublishReadyToRunExclude` 的用途。
- [`dotnet-trace`](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace)：
  采集启动期间的托管线程采样。
- [Avalonia 性能优化](https://docs.avaloniaui.net/xpf/configuration/performance)：
  Avalonia 官方对 ReadyToRun 与启动/渲染优化的说明（XPF 文档，部分建议同样适用）。
