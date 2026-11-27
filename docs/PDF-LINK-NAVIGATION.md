# PDF 链接跳转的调研与集成

## 问题与设计依据

PDF 中的蓝色、下划线属于页面内容或注释外观，不代表阅读器已经具备链接语义。文字也可以是黑色，链接区域也可以覆盖图片或空白。当前页面仍由 PDFium 绘制，新增功能读取链接的真实区域与目标，不根据颜色猜测，也不重新绘制文字。

查阅的上游实现：

- [Chromium PDFiumPage](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/pdf/pdfium/pdfium_page.cc)：`GetLinkTarget` 优先解析直接目的地，再解析 GoTo、URI action；`PopulateWebLinks` 使用 PDFium 识别纯文本网址；目标位置根据目标页转换。Chromium 也没有把所有 PDF action 当作可执行操作。
- [Chromium PDFiumEngine](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/pdf/pdfium/pdfium_engine.cc)：记录按下时的链接，释放时核对目标；文内导航与外部网址交给不同的客户端接口。
- [Mozilla PDF.js PDFLinkService](https://github.com/mozilla/pdf.js/blob/master/web/pdf_link_service.js)：将命名目标解析为显式目标，校验页码，再把页号、目标参数和缩放语义交给 viewer 的滚动接口。这支持在现有阅读器上增加独立的链接导航逻辑。
- [PDFium fpdf_doc.h](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdf_doc.h)、[fpdf_text.h](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdf_text.h)：链接、区域、目的地参数以及纯文本网址识别的公开 API。

本项目沿用这些职责划分，并根据现有高亮编辑交互确定事件优先级。

## 已实现的结构

1. `PDFiumService.GetPageLinks` 单独提取链接元数据，只需要 PDFium 原生 page/text page，不逐字符读取 Unicode 和边界，也不构建整页托管字符模型。`GetPageText` 只服务文字选择、复制、搜索和高亮。
2. `PDFiumService.NativePages.cs` 按需加载、复用原生 page/text page；服务级 LRU 最多保留 8 组，避免浏览或全文搜索使原生页无限增长。文字和链接请求复用同一组句柄，链接结果也在该条目中缓存；关闭/重载文档时先关闭 text page，再关闭 page，最后关闭 document。
3. `PdfLinkPage` 独立保存纯托管区域与目标；ViewModel 对可见页先发布轻量链接结果，再加载既有完整文字模型。全文搜索不请求链接，也不扫描网址。原有字符索引、阅读顺序、行列命中与选区算法不变。`PdfLink` 的多个区域独立命中，多行之间的空白不成为热区。
4. 先收集自动识别的网址，再按注释顺序添加显式链接；命中从后向前检查，使上层显式注释优先。没有可支持目标的注释保留占位区域，避免误开其下方自动识别的网址。
5. `PdfDocumentView.Links.cs` 承担目标预览、手形光标、外部启动和文内滚动。鼠标事件复用 `PdfDocumentView.Text.cs` 的捕获与文本选择流程，不增加逐链接控件或覆盖文本的点击层。

原生注释、网址识别句柄在提取后立即释放；页面与文本页由有界 LRU 统一管理。ViewModel 只保存托管快照，不持有原生句柄。所有新增 PDFium 调用仍位于 `_pageLock` 下，鼠标移动不调用 PDFium、不读取文件。

### 与 Chromium 实现的关系

Chromium 的 `GetTextPage()` 是懒加载并复用原生句柄；统一鼠标命中会查询附近字符、显式链接和表单控件，使光标与操作优先级一致。自动网址由 `CalculateLinks()` 按页计算并缓存。它不要求先把每个字符的 Unicode 和几何复制到另一份应用层模型。

本项目采用其“复用原生页、链接按页缓存、链接不依赖整页字符复制”的结构；保留现有托管拖选算法，以维持多栏选择、高亮与搜索的行为。前端仍使用后台准备的区域快照命中，而不逐鼠标事件同步调用 `FPDFLink_GetLinkAtPoint`，因为本项目 PDFium 渲染和提取共用锁，同步查询可能让 UI 等待渲染。可见页是当前预备链接的粒度，不会在打开文档时扫描整份 PDF。

## 链接与导航语义

| 类型 | 当前行为 |
| --- | --- |
| `/Dest` 直接目标、命名目标 | PDFium 解析为目标页和位置 |
| `/A /S /GoTo` | 跳转到同一 PDF 的目标 |
| `/XYZ` | 使用目标坐标和正缩放值；null 坐标保留相应视图位置，null/0 缩放保留当前缩放 |
| `/Fit`、`/FitH`、`/FitV`、`/FitR` | 按视口适配页面、宽度、高度或指定矩形，并定位到目标 |
| `/FitB`、`/FitBH`、`/FitBV` | 当前以可见页面边界近似内容边界，分别按 Fit、FitH、FitV 处理 |
| URI、纯文本网址 | 允许 http、https、mailto，通过 Avalonia Launcher 交给系统默认应用；失败显示“无法打开链接” |
| 非法页码、缺失目标、其他 action | 不跳转，仍可进行原有文字操作 |

坐标经过现有 `PdfPageImage.Geometry.ToLogical` 消除设备像素对齐的偏移，再按 `LayoutScale * Zoom` 还原页面坐标；链接坐标转换额外考虑 CropBox 原点与页面固有旋转。目标使用目标页自己的 CropBox、旋转和尺寸，不能套用来源页的高度。

文内跳转直接使用 `PdfVirtualizingPanel.ItemTopAt` 的累计位置计算滚动量，无需先渲染目标页、创建目标页控件或提取目标页文本。缩放仍使用现有范围 50%–250%，视口边缘的目标仍受 ScrollViewer 正常滚动范围约束。`/XYZ` 缺失纵坐标时保留来源页内的视口纵位置。

## 与已有交互共存

- 普通模式悬停可跳转区域时使用手形，左下角显示实际目标 URL 或目标页号。
- 左键按下仅记录候选链接；同一链接内释放且位移不超过 3 DIP 才激活。拖动、移出原链接再移回、滚动、Escape、捕获丢失、切换文档均取消候选。
- 链接覆盖文字时，超过点击阈值后进入原有拖选流程，复制结果仍由原有字符索引决定。链接覆盖图片或无文字区域时也可以点击。
- 高亮模式优先创建高亮；已有高亮的选择、拖选和右键编辑优先于链接跳转。
- 右键与中键不触发跳转。没有新增右键菜单，现有菜单主题和首帧不选中约定继续适用。
- 缩放的 compositor 动画期间暂不激活链接，避免视觉位置与已提交布局不同步；动画结束后恢复命中。

## 保持的约束与边界

位图渲染、原稿颜色、256 MiB 的实际像素字节记账、LRU 淘汰、可见页保护、Tab 缓存保留以及 ReadyToRun 发布设置均沿用现有实现。链接元数据仅在可见页或明确请求时加载，不预加载整份 PDF 的位图；搜索请求文本时不会额外获得链接元数据。8 组原生页缓存与位图 LRU 分开，不计入 256 MiB 位图预算；这项复用用少量额外原生内存换取较低的重复解析开销，其内存不属于位图像素预算。

当前不执行 JavaScript、Launch、GoToR、嵌入附件跳转、自定义协议或相对文件路径，不提供链接历史栈、链接专用右键菜单和逐链接键盘焦点。后续可在独立导航服务上增加历史返回和无障碍焦点，不需要改动文本选择模型。

PDFium 的 `FPDFDest_GetView` 不提供 FitH/FitV 参数的 null 标记，因此这两类的 null 位置当前按 API 返回的 0 值处理；XYZ 使用 `FPDFDest_GetLocationInPage` 的独立有效性标记完整保留 null 语义。FitB 系列的内容边界精度可作为后续兼容性改进。

## 自动化验证

新增 `PdfLinkTests`、`PdfLinkInteractionTests` 与 `PdfNativePageReuseTests`。测试即时生成包含真实注释与文本的 PDF，不依赖网络和默认浏览器实际启动。

- 原生解析：直接目标、命名目标、GoTo、URI、自动网址、多段 QuadPoints、目标无效、协议过滤、保存高亮后保留链接。
- 坐标：来源页与目标页不同大小、非零 CropBox、0/90/180/270 度旋转；托管转换与 PDFium `FPDF_PageToDevice` 的结果对照。
- Headless 交互：手形及目标预览、释放才激活、小幅抖动、拖选复制、右键/中键、Escape、真实捕获丢失、高亮编辑优先、切换文档、启动失败。
- Headless 导航：未实现远页定位和虚拟化数量、XYZ 坐标与缩放、null 参数、Fit 系列、100%/150%/200% 显示缩放、缩放动画期间的命中保护。
- 加载与生命周期：完整字符提取被阻塞时仍可悬停、打开链接；搜索不扫描链接；文字和链接共享原生文本页；缓存命中不重算链接；8 页 LRU 上限、按文档释放、取消重试、关闭时完整释放；链接加载失败仍能使用文字功能。

完整回归命令：

```powershell
dotnet test tests/pdf_fluent.Tests/pdf_fluent.Tests.csproj --no-restore --verbosity minimal
```

沙箱内的命名管道权限限制会导致既有单实例通信测试失败，需要在沙箱外复核该测试。

2026-11-27 最终验证：完整 220 项测试全部通过，包含 32 项链接用例、8 项新增加载/原生缓存用例，以及原有文本交互、菜单、缩放、DPI 和位图缓存回归。完整测试在沙箱外复核，以覆盖命名管道通信。

## 性能验证

可重复运行的基准位于 `benchmarks/PdfLinkBenchmark`：

```powershell
dotnet run --project benchmarks/PdfLinkBenchmark/PdfLinkBenchmark.csproj --configuration Release -- tests/pdf_fluent.Tests/assets/CUDA_by_Example.pdf
```

2026-11-27 本机测量：样本前 30 页，每种路径运行 5 次取中位数；每次使用新 service 并预先打开文档，文件缓存和运行时预热。对比链接单独就绪与“完整文字提取后才提供链接”的路径，后者不是旧版本二进制的整体性能测量。

| 路径 | 30 页总耗时 | 当前线程托管分配 |
| --- | --- | --- |
| `GetPageLinks`，原生页首次加载 | 258.92 ms | 192,424 bytes（约 0.18 MiB） |
| `GetPageText` 后再 `GetPageLinks` | 280.89 ms | 36,443,520 bytes（约 34.76 MiB） |
| 同一页缓存命中 10,000 次 | 0.82 ms | 0 bytes |

此样本链接就绪路径约缩短 8%，更明显的收益是该路径无需分配整页托管字符与行列索引。冷加载仍包含 PDFium 原生页面/文本解析，因而不会变成零成本。现有文字功能随后仍需完整字符模型；这些数字不代表整个阅读器的内存或总耗时下降同样比例，也不包含原生内存分配。缓存命中测试测量 service 元数据复用，不代表绘制或端到端鼠标延迟。
