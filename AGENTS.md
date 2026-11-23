# 开发约定

- Python 脚本统一通过 `uv run python <script.py>` 启动，不要直接调用 `python` 或 `python3`。
- 本地不要尝试使用 Inno Setup / ISCC 编译安装包，当前开发环境未安装 Inno Setup。
- Avalonia 功能验证优先使用 Avalonia Headless 自动化测试，不要依赖桌面截图判断布局、滚动或虚拟化行为；截图仅用于人工视觉检查。
- Avalonia Fluent 主题悬停时会直接修改按钮模板内部 `ContentPresenter` 的背景色；自定义悬停色不能只设置外层 `Button.Background`，还要覆盖 `Button:pointerover /template/ ContentPresenter#PART_ContentPresenter` 的 `Background`。Headless 测试也应检查模板实际绘制画刷，避免只验证按钮属性。

# 右键菜单主题约定

- 阅读器内的高亮右键菜单必须与右上角“三个点”菜单保持一致，统一使用 `MoreActionsMenuPresenterTheme`（白色背景、细边框、12 像素圆角）和 `MoreActionsMenuItemTheme`（8 像素圆角、统一内边距及悬停色），不要保留未经主题化的 `MenuFlyout` 默认外观。
- 代码创建的 `MenuFlyout` 也必须显式设置 `FlyoutPresenterTheme` 和 `ItemContainerTheme`。Headless 回归测试应打开菜单并检查实际 `MenuFlyoutPresenter` 与 `MenuItem` 的画刷、圆角和间距，不能只检查主题属性是否赋值。
- 所有右键菜单及所有“三个点”工具栏菜单（包括主窗口右上角和阅读器工具栏）打开时都不能默认选中第一项，也不能先显示选中再异步清除。`MenuFlyout` 统一使用 `UnselectedMenuFlyout`，在基类 `ShowAtCore` 完成默认聚焦后、返回绘制前同步将 `MenuFlyoutPresenter.SelectedIndex` 设为 `-1`；禁止使用延迟回调，避免首帧闪烁。XAML `ContextMenu` 则在 `Opened` 和 `GotFocus` 中同步清除，并应有 Headless 测试覆盖。

# PDF 位图缓存约定（务必保持）

- 缓存预算 `DocumentTabViewModel.MaxBitmapCacheBytes`（256 MiB）按**真实位图像素字节**（`PixelSize.Width * PixelSize.Height * 4`）记账，不按渲染倍率、页数或估算值。
- 页面重渲染会替换位图，此时扣减缓存必须使用该条目**入缓存时记录的字节数**（`_cacheEntryBytes`），**禁止**读取 `page.Image` 的“当前尺寸”来扣减。原因：`TouchCache` 里 `page.SetImage(...)` 已把 `page.Image` 换成新位图，若 `RemoveCacheEntry` 此时用 `GetImageBytes(page)`，就会“减去新尺寸、再加回新尺寸”，条目永远停留在首次渲染的尺寸；缩放或切到 HiDPI 后新位图可达旧位图的 16 倍（1.25 → 5.0 px/pt），预算上限将永远触发不了淘汰，实际内存可达账面的 2 倍以上（曾在 128 MiB 预算下实测 243.7 MiB / 账面 127.7 MiB）。
- 正确实现见 `src/ViewModels/DocumentTabViewModel.cs` 的 `_cacheEntryBytes`、`TouchCache`、`RemoveCacheEntry`。修改缩放、DPI、重渲染相关代码后必须保持该记账方式。
- 维护不变量：淘汰后 `CachedBitmapBytes` 应等于所有活页面的位图字节之和；`TrimCache` 只淘汰不在 `_realizedPages` 中的页（可见页 + 预取缓冲页固定不淘汰）。
- 缓存回收是事件驱动的，不是定时器：只在渲染完成（`TouchCache`）、页面离开实现集合（`ReleasePage`）、布局同步（`SyncRealizedPages`）时执行 `TrimCache`。因此可见页/缓冲页较多时，位图总量允许短时高于预算；不要据此添加定时 `GC.Collect` 之类的“回收”。
- 排查内存问题时必须区分三类数据：LRU 位图缓存、单次渲染的瞬时缓冲、以及 .NET/Windows 尚未归还的进程工作集；只有第一类受 256 MiB 预算约束。当前单页渲染让 PDFium 直接写入未发布的 `WriteableBitmap`，不再分配原始像素数组；只有兼容的原始缓冲区/批量 API 才会额外持有原始像素数组。
- 回归测试：`DocumentTabViewModelTests.CacheBudgetUsesActualBitmapSizeAfterReRenderingAtHigherScale`、`DocumentTabViewModelTests.SwitchingAwayReleasesAllCachedBitmaps`、`PdfVirtualizationMemoryTests.ZoomingInAfterScrollingKeepsTheBitmapCacheWithinBudget`。改动缓存淘汰逻辑后必须全部通过。

# 启动与发布约定

- 发布必须保持 `PublishReadyToRun=true`；默认同时启用 `PublishReadyToRunComposite=true`。不要把“打开 PDF 卡死”归因于 PDFium 不兼容而关闭 R2R 或排除 `PDFiumCore.dll`。
- PDFiumCore 生成的委托属性（`IFSDK_PAUSE.NeedToPauseNow`、`FPDF_FILEWRITE_.WriteBlock` 等）只保存原生函数指针，不持有托管委托。赋值后必须在整个原生调用期间保留委托的强引用，并在调用结束后 `GC.KeepAlive`；否则 R2R 或 GC 压力下会触发 `Environment.FailFast`（回调了已回收的委托）。
- 命令行 PDF 的打开应放在主窗口首帧之后（`MainWindow.Opened`），不要在 `OnFrameworkInitializationCompleted` 里同步打开文档而拖慢 UI 出现时间。
- 启动耗时排查使用 `FLUENTPDF_STARTUP_TRACE=1`，分析方法和基准数据见 `docs/STARTUP-PERFORMANCE.md`。

# 测试资源备注

- `tests/pdf_fluent.Tests/assets/CUDA_by_Example.pdf`：PDF 功能测试样本。
