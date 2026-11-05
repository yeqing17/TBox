# TBox 技术方案

> v2.3.0 · WinUI 3（Windows App SDK 1.7）+ .NET 8 · 最后更新 2026-11-06

## 1. 结论先行

TBox v2 使用 **WinUI 3 + Windows App SDK 1.7 / .NET 8**，以**非打包（unpackaged）**方式运行。

选 WinUI 3 的理由：

- 用户要求"深入融合系统"的原生观感：DWM 抗锯齿圆角、亚克力背板、Fluent 控件，这些 WinUI 3 开箱即用；
- 后续要走 MSIX、Toast、Shell 集成时，WinUI 3 是官方路径；
- 和参考项目 DeskBox 的形态一致。

代价与约束：

- 本机构建环境缺 VS 的 UWP/MSIX 构建任务，`dotnet build` 需要一层 shim（见 §3）；
- 产物是绿色目录（非 MSIX），分发靠复制/压缩包。

对比 WPF（v1 方案）：WPF 构建零依赖、更轻，但原生圆角/亚克力只能靠 `SetWindowRgn` 硬裁剪（有锯齿）和自绘，达不到"系统面板"的质感。

## 2. 架构

```
src/TBox.WinUI/
├─ App.xaml(.cs)              组合根：单实例、创建并启动 BoxManager
├─ BoxManager.cs              盒子生命周期、活动盒子、托盘菜单、共享服务
├─ DockPoll.cs                所有盒子共用的 50ms 停靠轮询
├─ MainWindow.xaml(.cs)       单个盒子的面板 UI（标题栏显示盒子名）
├─ MainWindow.Docking.cs      贴边 / 胶囊 / 竖条 状态机（partial）
├─ MainWindow.AutoFit.cs      按图标数量计算窗口尺寸（partial）
├─ CapsuleWindow.cs           胶囊窗口（每个贴边盒子一个，独立顶层窗口）
├─ Views/DialogWindow.cs      通用对话框窗口（设置/输入/导入/确认）
├─ Models/AppModels.cs        数据模型 + 搜索匹配
├─ Services/
│  ├─ DataStore.cs            JSON 持久化（防抖 + 原子写 + 备份）
│  ├─ ShellIcons.cs           256px 图标提取 + PNG 缓存
│  ├─ Shortcuts.cs            .lnk 解析 / 桌面枚举
│  ├─ Launcher.cs             启动语义 / 管理员 / 定位 / 开机自启
│  ├─ AppLogo.cs              标题栏/空胶囊共享 logo
│  └─ IconLoader.cs           缓存 PNG → BitmapImage
├─ Interop/
│  ├─ NativeMethods.cs        user32 基础调用
│  ├─ WindowChrome.cs         子类化 WndProc：WM_NCCALCSIZE、置底
│  ├─ DwmChrome.cs            DWM 圆角 / 消除 1px 边框
│  └─ ShellHost.cs            托盘图标 + 全局热键（隐藏消息窗口）
├─ AppTheme.cs                浅色/深色解析
└─ Assets/tbox.ico / tbox-512.png
```

依赖方向：UI → Services/Interop → Models。

## 3. 构建：为什么有一层 shim

Windows App SDK 的 `buildTransitive` 目标会调用 Visual Studio 私有 MSBuild 任务
（`Microsoft.Build.AppxPackage.dll` / `Microsoft.Build.Packaging.Pri.Tasks.dll`）。本机只有 VS Build Tools，
没有 UWP/MSIX 打包工作负载，`dotnet build` 会直接失败在 `MSB4062`。

`Directory.Build.targets` 的做法：

1. 把这些**只用于 MSIX 打包**的目标置空（`_GenerateProjectPriFileCore`、`GetMrtPackagingOutputs`、`_ExpandPriFiles` 等 13 个）；
2. 用 `Microsoft.Windows.SDK.BuildTools` 自带的 `makepri.exe`，只对 `*.xbf` 和 `Assets` 生成 `resources.pri`；
3. `dotnet publish` 后把 XBF 与 PRI 复制到发布目录。

```xml
<Target Name="TBoxGeneratePri" AfterTargets="Build"> ... makepri new ... </Target>
<Target Name="TBoxCopyXamlToPublish" AfterTargets="Publish"> ... copy *.xbf + resources.pri ... </Target>
```

装好 Visual Studio 的「Windows 应用开发」工作负载后，可以删掉这个 targets 文件，恢复标准 WinUI 构建链。

## 4. 原生窗口外观

### 4.1 真正的无边框

`OverlappedPresenter.SetBorderAndTitleBar(false, false)` 只去掉标题栏，**WinUI 的窗口过程仍从
`WM_NCCALCSIZE` 返回 8px 非客户区**，那 8px 用窗口背景色绘制，看起来就是一圈白边。

解决：子类化窗口过程，`WM_NCCALCSIZE`（wParam=TRUE）直接返回 0，客户区铺满窗口：

```csharp
if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero) return IntPtr.Zero;
```

原窗口过程按 HWND 存在字典里（两个窗口共用一个静态字段会导致消息链互相调用 → 栈溢出）。

### 4.2 圆角与边框

- 顶层窗口：`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, ROUND)` 得到**抗锯齿**圆角；
  `DWMWA_BORDER_COLOR = NONE` 去掉 Win11 的 1px 边框。
- 注意：`SetWindowRgn` 做圆角是**硬裁剪**，边缘会有锯齿——v1 踩过这个坑，v2 不再使用。
- 亚克力：`Window.SystemBackdrop = new DesktopAcrylicBackdrop()`。

### 4.3 窗口层级

- 常驻模式：`SetWindowPos(HWND_BOTTOM)` + `WM_WINDOWPOSCHANGING` 强制置底；`WS_EX_TOOLWINDOW` 不进 Alt+Tab。
- 贴边模式：置顶（`HWND_TOPMOST`），否则胶囊会被普通窗口盖住、鼠标够不到。

## 5. 贴边与收起状态机

三种形态：

| 形态 | 面板窗口 | 胶囊窗口 | 交互 |
| --- | --- | --- | --- |
| 常驻（未吸附） | 可见，最底层 | 隐藏 | 拖动、点击、热键 |
| 吸附展开 | 贴边可见，置顶 | 隐藏 | 移开 400ms 收起 |
| 吸附收起 | **停在屏幕外 8px**，内容折叠 | 显示 | 胶囊/竖条 |

关键点：

- **收起不隐藏窗口，只平移出屏**：`Hide()`/`Show()` 会重新建立合成，第一帧是空白 → 展开时闪一下；
- 展开/收起用 160ms 缓动**平移**，不改变窗口尺寸（resize 会重排整棵 XAML 树，同样会闪）；
- 胶囊是**独立窗口**，面板内容完全隐藏，不会有内容从缝隙里透出来；
- 悬停检测由主窗口 50ms 定时器轮询 `GetCursorPos` 完成，不依赖 `PointerEntered/Exited`（会漏事件、状态卡住）；
- 菜单/对话框/拖动/输入框聚焦时禁止自动收起；外部文件拖拽时左键保持按下也会暂停 400ms 收起，
  避免从资源管理器拖文件进来时面板先消失。
- **钉住**（标题栏 📌 / 托盘菜单）：`BoxSettings.Pinned` 为 true 时 `HoldsOpen()` 恒真，忽略收起计时；
  flyout 失焦也不隐藏，贴边盒子启动时直接以展开态恢复。取消钉住后重新开始 400ms 离开计时；
  每个盒子独立保存，切换收起形态不会强行收起已钉住的面板。搜索框展开时标题栏隐藏 📌 以保证最小宽度。
- **位置记忆**：未贴边面板在 X/热键隐藏前 `SavePlacement()` 把当前 `GetWindowRect` 写入
  `Box.Window.X/Y`；托盘/热键唤回时 `RestoreFlyoutPosition()` 按该坐标恢复并夹到当前工作区，
  不再默认回到右下角。`--flyout` 启动仍用 `ShowFlyoutAtTray()` 保持右下角行为。
  贴边盒子隐藏后走胶囊，位置由贴边几何决定，不写回自由位置。
- **托盘/热键语义**：未贴边且 `_mode=Desktop` 时，面板虽然 `IsVisible`，但可能被普通窗口盖住，
  第一次点击应视为“唤出”（`ShowFlyout` 并恢复位置），不能直接 `Hide`；只有 `_mode=Flyout`
  的置顶弹出才在下次点击时隐藏。贴边盒子则在胶囊/展开之间切换。否则“放回桌面”后点一次托盘
  只会把面板隐藏，第二次才显形。
- **标题栏两个收起按钮**：方块箭头 = `Hide()` 到托盘，**不改变 Dock**；贴边盒子再从托盘唤回时
  仍在原贴边位置展开，移开鼠标后自动收成胶囊。X = 贴边时 `HideCollapsed(force:true)` 收成胶囊，
  未贴边时 `Hide()` 到托盘。取消贴边 / 放回桌面只在托盘菜单里显式执行。
  `PollDock` 在 `_mode=Hidden` 时直接返回，避免“隐藏到托盘”的贴边盒子又被轮询收成胶囊。
- 贴边支持左/右/上。顶边保留拖动时的 x 和宽度，只把 `y` 对齐 `WorkArea.Top`；收起时面板停到
  `top - height - 8`（胶囊）或 `top - height + 16`（竖条），胶囊在面板宽度内水平居中。
- 展开/收起统一走二维缓动 `SlideToAsync(fromX, fromY, toX, toY, …)`：左右只动 x，顶边只动 y。
- 胶囊方向由 `CapsuleOrientation` 决定：`Auto` 在左右贴边用竖排、顶边用横排；显式 `Vertical/Horizontal`
  覆盖自动规则。横排胶囊右侧端部是展开区，命中测试统一走 `CapsuleWindow.HitTestExpandTab`。

### 5.1 坐标单位（DIP vs 像素）

进程清单是 PerMonitorV2，所以**有两套单位并存**，混用就会在高缩放屏上出错（曾导致 200% 缩放下胶囊被裁掉一半）：

| 属于 DIP | 属于物理像素 |
| --- | --- |
| XAML 里的所有尺寸（`Width`、`Padding`、`CornerRadius`…） | `GetWindowRect` / `GetCursorPos` / `WM_*` 的 `lParam` |
| 代码里的设计常量（胶囊短边 56、展开区短边 38、吸附距离 16、竖条露边 16…） | `DisplayArea.WorkArea`、`AppWindow.MoveAndResize`、`AppWindow.Position` |

规则：

- 常量按 DIP 书写，交给上面右列之前一律过 `Dpi.Px(值, Dpi.ScaleFor(hwnd))`；反查用 `Dpi.Dip(px, scale)`；
- 命中测试两边都用物理像素：胶囊展开区由 `CapsuleWindow.HitTestExpandTab(x, y)` 判断（内部把 38 DIP 换成像素，竖排查底部、横排查右端），主窗口不要自己算；
- 窗口像素尺寸是**用户数据**（`data.json` 的 `window.width/height`、`dockWidth`），只在与最小尺寸比较时换成 DIP 下限；自适应尺寸只在运行时生效，不写回这对偏好值（§5.2）；
- 贴边位置统一走 `ExpandedX/ExpandedY/ParkedX/ParkedY/StripParkedX/StripParkedY`，最终由 `PlaceDocked(width, height)` 落到屏幕：左右只改 x，顶边只改 y；
- 跟随实时缩放：`XamlRoot.Changed` 里的 `RasterizationScale` 变化 → 重新 `MoveAndResize`（XAML 自己会重排，但窗口的像素尺寸不会变）；
- 调试：`%LOCALAPPDATA%\TBox\app.log` 里收起日志带 `size=..×.. scale=..`，可直接核对像素尺寸是否等于 DIP × scale。

### 5.2 自适应尺寸

`MainWindow.AutoFit.cs` 在启动、切换分组、增删/导入/移动工具、清空搜索和保存设置后重算窗口尺寸。
公式全部按 DIP 书写，最后过 `Dpi.Px`：

- 图标格 96、GridView 横向内边距 16（10+6）、固定 chrome 126（46 标题 + 42 分组 + 28 状态 + 上下内边距 10）；
- 宽度 = `16 + 96 × 列数`，高度 = `126 + 96 × 行数`；
- 列数从 `min(图标数, 4)` 起；行数超过“最大行数”时先加列：悬浮最多 8 列（784 DIP），贴边最多到贴边宽度能放下的列数。仍超出则高度封顶，GridView 内部滚动；
- 悬浮和贴边时宽度都随列数变化（360–784），贴边面板仍贴住屏幕边缘；`dockWidth` 只在关闭自适应时作为左右贴边的恢复宽度；
- 空分组用 360×320 容纳空状态；有图标时高度按可见行数精确计算（1 行 = 222 DIP），不留半行；
- 顶边同样按图标数贴合，横向 x 保留拖动时的位置；`PlaceDocked` 负责把 y 固定在 `WorkArea.Top`。

两个关键约束：

- **自动适配不写回 `window.width/height`**：它们仍是用户手动拖拽出的偏好尺寸，只用于关闭自适应时恢复窗口，不再作为自适应宽度的上限；`dockWidth` 同样只在关闭自适应时作为贴边恢复宽度。`Closing` 和标题栏拖动结束时在自适应开启下跳过尺寸写回；
- **搜索输入过程中不触发**：每敲一个字就改窗口尺寸会打断输入；清空搜索后由 `RefreshView` 重新贴合。

> v2.3 起这些尺寸/贴边/胶囊设置按盒子独立保存，公式不变，只是读写 `Box.Settings`/`Box.Window`（§5.4）。

日志里 `[fit] items=.. cols=.. rows=../.. maxRows=.. -> WxHpx` 可直接核对列数、行数和像素尺寸。

### 5.3 顶边与横向胶囊

顶边吸附的几何：

- 吸附判定在 `ComputeSnapSide` 里取左/右/上三条边中距离最近且小于 `SnapDistance` 的一条；距离相同优先顶边；
- 展开：面板 `x` 不变、`y = WorkArea.Top`；收起胶囊时停到 `y = top - height - 8`，胶囊在面板宽度内居中；
- 顶边竖条：面板停到 `y = top - height + 16`，只露出**面板底部** 16px；`CollapsedBar` 改为
  `Stretch + Bottom`、90×3 的横向指示条，命中检测用顶部 16px 横带；
- `SnapOnDragEnd` 顶边分支保存 `x = clamp(rect.Left)`、`y = top`，不写 `DockWidth`；启动时
  `ApplyDockOnStartup` 用保存的 x（缺失则居中）。

横向胶囊：

- 短边固定 56 DIP，长边 = `18 + 38 × 图标数 + 6 × (图标数 - 1) + 7 + 38`（最多 3 个图标）；
- 右端 38 DIP 是展开区，glyph 用向下箭头；竖排仍然在底部；
- 尺寸由 `ContentWidth/ContentHeight` 决定，`PixelWidth/PixelHeight` 再乘 DPI；`Update(items, orientation)`
  同时切换图标堆叠方向、分隔线、展开条尺寸/边距和根 Padding；
- 图标异步加载完成后，如果胶囊正在显示，`LoadIconsAsync` 会原地刷新一次，避免启动时一直显示占位图。

### 5.4 多盒子架构（v2.3）

第一版上限 **4 个盒子**，采用“每盒子一个面板窗口 + 一个胶囊 + 共享服务”：

| 层 | 负责 | 生命周期 |
| --- | --- | --- |
| `BoxManager` | 加载一次数据、解析主题、创建/删除盒子窗口、活动盒子、托盘菜单、全局热键、主题广播 | 进程级，`App` 持有 |
| `MainWindow` | 一个盒子的分组/工具/搜索/拖拽、贴边/收起、auto-fit | 每盒子一个，创建后常驻 |
| `CapsuleWindow` | 贴边盒子的收起态；按需创建，删除盒子时一起关闭 | 每盒子最多一个 |
| `DockPoll` | 一个 50ms `DispatcherQueueTimer`，遍历已注册窗口 | 有贴边窗口时跑，全部注销后停 |
| `DataStore` | 唯一加载/保存入口、400ms 防抖、原子写 | 进程级单例 |
| `ShellHost` | 一个托盘图标 + 一个全局热键 owner | 进程级 |

**启动顺序**（`BoxManager.Start`）：

1. `InitShell()`：一个 `ShellHost`，热键/托盘只注册一次，日志输出 `[box] shell hwnd=..` 便于自动化；
2. 为所有盒子 `new MainWindow(manager, box)`，窗口在构造函数里完成数据绑定、主题、贴边/auto-fit 定位；
3. 所有窗口 `ShowParked()`：已贴边盒子用 `ShowWindow(SW_SHOWNOACTIVATE)` 露出胶囊/竖条；未贴边盒子保持隐藏；
4. `_suppressActive=true` 期间吞掉停靠窗口排队到达的 `Activated`，避免它们把 `ActiveBoxId` 改成最后创建的盒子；
5. 等待 120ms 让激活事件排空，再显示/弹出活动盒子并 `SetActive`；再等 120ms 重设一次，确保前台窗口与持久化一致；
6. 数据读取警告、`--settings` 只对活动盒子执行。

**活动盒子**：只有**指针激活**（用户点击面板）或从胶囊/竖条展开时才 `BoxManager.SetActive(window)` 并写
`settings.activeBoxId`；`Show/Activate` 等程序化激活不改变活动项。这样启动时系统即使把某个可见竖条
（例如右侧贴边盒子）设为前台，也不会覆盖恢复的活动盒子。托盘左键/热键 `Ctrl+Alt+T` 都作用于活动盒子；
托盘菜单点击某个盒子会先 `SetActive` 再显示它。标题栏 `BoxName` 和窗口 `Title` 都带盒子名。

**创建/删除**：托盘菜单“新建盒子…”弹 `DialogWindow` 输入名称（默认“新盒子 N”，重名拒绝），新盒子
窗口以级联位置出现并成为活动；满 4 个时菜单项用 `MF_GRAYED` 置灰。删除必须先确认、至少保留一个盒子；
删除时从 `Data.Boxes` 移除、关闭面板/胶囊、注销轮询、`WindowChrome.Forget`，再切到剩余的第一个盒子。
菜单/输入/确认对话框期间对**所有**窗口 `SetCollapseSuppressed(true)`，避免贴边盒子在操作途中自动收起。

**共享/每盒子状态拆分**：

| 共享 | 每盒子 |
| --- | --- |
| 主题 `AppTheme.Current`、开机自启、托盘、全局热键、数据文件 | `Groups`/`Items`、`LastGroupId`、`Window`、`Dock`、`DockWidth`、`CollapsedStyle`、`CapsuleOrientation`、`AutoFitWindow`、`AutoFitMaxRows`、`Pinned` |

为避免资源随盒子数线性恶化，v2.3 同时做了：`WindowChrome` 的置底集合从单静态字段改为按 HWND 的
`HashSet`（`SetKeepAtBottom(hwnd, …)`/`Forget(hwnd)`）；`IconLoader` 按 `路径+mtime+物理目标尺寸`
缓存 `BitmapImage`；`ShellIcons.EnsureIcon` 按缓存路径加锁，避免多个窗口同时写同一张 PNG 的 `.tmp`；
停靠轮询合并为一个。代价是所有盒子的面板窗口在启动时都创建（未贴边的隐藏），这是 4 盒内存的主要来源；
胶囊只在盒子收起时创建。

### 5.5 高 DPI 清晰度（2K/4K 打磨）

- **图标按物理像素解码**：`ShellIcons` 仍缓存 256px 源图；`IconLoader.LoadAsync(path, targetPixels)`
  用 `BitmapImage.DecodePixelWidth = 44 × 当前 DPI 缩放` 解码。面板 44 DIP 在 200% 下正好得到 88px，
  不再让合成器把 256px 缩到 88px；缩放变化时 `OnAutoFitScaleChanged` 强制重载所有分组的图标。
- **布局像素对齐**：主面板 `Root`、胶囊和对话框根元素都设 `UseLayoutRounding`，避免 125%/150%
  下出现半像素边、文字和 1px 分隔线发虚。
- **整数尺寸**：胶囊图标 36 DIP、展开区 40 DIP、面板图标 44 DIP 都是 4 的倍数，在 125%/150%/200%
  下仍落在整像素（旧胶囊图标 34 DIP 在 125% 下是 42.5px）。
- **动画节奏**：贴边展开/收起从 18 帧 × 8ms 改为 10 帧 × 16ms（160ms），每帧一次窗口移动，
  与 60Hz 合成节奏对齐，4K 下少掉帧。

## 6. 对话框

早期用 `ContentDialog`，遇到两个问题：

1. 它寄居在面板的 XAML 树里，面板只有 362px 宽时被挤扁、显示不全；面板收起时甚至开在屏幕外；
2. 对话框内容需要独立的宽度和滚动。

改为 `Views/DialogWindow.cs`：独立顶层窗口（480 宽），共用无边框 + 亚克力外观，居中显示，
通过 `TaskCompletionSource` 等待用户结果。标题区（包括顶部空白）注册 Pointer 捕获拖拽，
用 `GetCursorPos` + `AppWindow.Move` 移动窗口；右上角 X 等价于取消/关闭，点击 X 时祖先命中
`ButtonBase`，不会触发标题拖动。

两个必踩的坑：

- **Window 对象被 GC 回收时窗口会消失** → 用静态列表持有，关闭时移除；
- 窗口子类化必须**先登记原过程、再替换**，否则替换瞬间到达的 `WM_CREATE` 查不到表项被吞掉，窗口立即关闭。

## 7. 数据

- `%APPDATA%\TBox\data.json`，camelCase、缩进、枚举存字符串；v2 结构见 PRD §7；
- 顶层 `settings` 只放全局项（主题、开机自启、最小化到托盘、活动盒子 ID），`boxes[]` 每一项携带自己的
  `settings`、`window`、`groups`；
- 400ms 防抖保存；写 `.tmp` 后原子替换；覆盖前留 `data.bak.json`；
- 解析失败时备份为 `data.corrupt-{时间}.json` 并用默认配置启动；
- **`IsLoaded` 门闩**：未加载完成前禁止保存，避免第二个实例用空数据覆盖真实配置；所有窗口共享同一个
  `DataStore`，窗口关闭/删除盒子只调 `MarkDirty`/`SaveNow`，不各自加载。

**v1 → v2 迁移**（`DataStore.Load`）：

1. 先读 JSON 的 `version`；`<2` 且没有 `boxes` 时用私有的 `LegacyTBoxData`/`LegacyAppSettings` 反序列化
   （根节点的 `groups`/`window`、旧 `settings` 里的 dock/capsule/auto-fit）；
2. 生成一个 `BoxData`，名称固定“工具箱”，把旧分组、窗口位置、贴边/胶囊/自适应/上次分组全部搬进盒子；
   `Theme`/`RunAtStartup`/`MinimizeToTray` 提到全局 `AppSettings`；
3. `Normalize()` 补盒子/分组 ID、至少一个盒子和一个分组、修正 `LastGroupId`/`ActiveBoxId`、夹紧枚举和
   `AutoFitMaxRows`；随后立即 `SaveNow()` 写 v2，旧文件成为 `.bak`；
4. `version > 2` 时设 `IsReadOnly=true` 并保留 `LoadWarning`，启动后用对话框告知，绝不回写降级；
5. 载入超过 4 个盒子时只把前 4 个暴露给 UI，多余项保留在 `Data.Boxes` 里不删除。

## 8. 图标与快捷方式

- 图标：`SHGetFileInfo(SYSICONINDEX)` → `SHGetImageList(SHIL_JUMBO)` 取 256px HICON →
  `Icon.ToBitmap().Save(png)`；缓存按 item id，目标文件修改时间更新后重取。
- `.lnk`：晚绑定 `WScript.Shell.CreateShortcut`，读 TargetPath / Arguments / WorkingDirectory；
- 启动：`UseShellExecute=true`，工作目录优先用 lnk 记录的，否则取目标目录；
- 管理员：优先对解析后的 exe 用 `runas`；UAC 取消（`1223`）静默。

## 9. 搜索

`AppItem.Matches(term)`：

```csharp
var haystack = string.Join(" ", Name, Description, Keywords);
return term.Split(' ').All(t => haystack.Contains(t, OrdinalIgnoreCase));
```

即：名称 / 描述 / 关键字三处匹配，多词 AND。

## 10. 主题

- 调色板放在 `App.xaml` 的 `ThemeDictionaries`（键必须是 `Default` 与 `Light`，`HighContrast` 可选）；
- 面板 XAML 用 `{ThemeResource ...}` 引用，切换 `Root.RequestedTheme` 即时生效；
- 代码构建的窗口（胶囊、对话框）读 `AppTheme.IsDark` 取对应色值。

## 11. 踩坑清单

| 现象 | 原因 | 解决 |
| --- | --- | --- |
| `dotnet build` 报 `MSB4062` | 缺 VS 的 AppxPackage 构建任务 | stub 目标 + makepri（§3） |
| XAML 编译器静默退出码 1 | 用了不存在的附加属性 `Panel.ZIndex` | WinUI 是 `Canvas.ZIndex` |
| 同上 | `ThemeDictionaries` 用了 `Dark` 键 | WinUI 用 `Default` 表示深色 |
| 圆角有锯齿 | `SetWindowRgn` 硬裁剪 | 改用 DWM 圆角 |
| 窗口一圈白边 | WinUI 的 8px 非客户区 | 子类化 `WM_NCCALCSIZE` 返回 0 |
| 展开时闪一下 | `Hide/Show` 重建合成 | 窗口停屏外，只平移 |
| 展开/收起抖动 | 每个动画帧都重设 DWM 属性 | chrome 只应用一次，动画只改位置 |
| 子窗口 `GetParent` 返回 0 | popup 窗口返回的是 owner | 用 `GetAncestor(GA_PARENT)` |
| 重新挂载后崩溃 | `AppWindow` 属性在 SetParent 后为 null | 构造时缓存实例 |
| 第二个窗口导致栈溢出 | 原 WndProc 存在静态字段 | 按 HWND 分别保存 |
| 独立窗口秒关 | Window 对象被 GC | 静态列表持有 |
| 搜索框文字贴顶 | `PlaceholderText` 不受 Padding 影响 | 自绘底部占位 TextBlock |
| 高缩放屏上胶囊被裁一半 | XAML 常量是 DIP，却被直接当物理像素传给 `MoveAndResize`（150%/200% 时窗口只有实际所需的一半） | 常量全部按 DIP 书写，经 `Dpi.Px(值, ScaleFor(hwnd))` 换算后再给 `MoveAndResize`/命中测试（§5.1） |
| 启动先闪一个默认窗口再跳到正确位置 | 窗口先 `Activate`，400ms 后才加载 store 并重新定位/改尺寸 | 数据加载、主题、Tabs/图标、贴边或自适应定位、无边框 chrome 全部放到构造函数里，`Activate` 之前就是最终状态 |

## 12. 构建与发布

```powershell
# 开发构建
dotnet build src\TBox.WinUI\TBox.WinUI.csproj -c Debug -p:Platform=x64

# 轻量包（框架依赖 .NET，约 10MB；目标机需要 .NET 8 Desktop Runtime + Windows App Runtime 1.7）
dotnet publish src\TBox.WinUI\TBox.WinUI.csproj -c Release -r win-x64 `
    --self-contained false -p:Platform=x64 -o dist-lite

# 完整包（.NET 自包含，约 100MB；CI 会再放入 Windows App Runtime 安装器和 install-and-run.cmd）
dotnet publish src\TBox.WinUI\TBox.WinUI.csproj -c Release -r win-x64 `
    --self-contained true -p:Platform=x64 -o dist-full
```

CI 的 `build.yml` 同时发布这两个目录，产物分别是
`TBox-<ref>-lite-win-x64.zip` 和 `TBox-<ref>-full-win-x64.zip`；推 tag 时两个都会挂到 Release。

命令行参数：

| 参数 | 作用 |
| --- | --- |
| `--dock-right` | 启动即吸附到右边缘 |
| `--dock-top` | 启动即吸附到上边缘（自动横排胶囊） |
| `--flyout` | 启动即弹出（右下角；托盘/热键唤回则恢复上次位置） |
| `--settings` | 启动后打开设置 |

## 12.1 截图流水线

文档里的界面截图不依赖桌面状态：用 `scripts/print-window.ps1` 对窗口句柄做 `PrintWindow`，
再用 `scripts/compose-screenshot.py` 按窗口圆角做遮罩、合成到纯色背景并加投影。

- 演示数据放在独立目录（`TBOX_DATA`），不碰真实配置；
- `TBOX_OPAQUE=1` 时应用不启用亚克力背板，否则 `PrintWindow` 会把窗口后面的画面采样进来
  （深色截图会带上壁纸的颜色）；
- `scripts/find-window.ps1` 按进程和窗口尺寸定位胶囊/对话框。

## 13. 已知限制

- 独占全屏程序（全屏游戏）会盖住置顶窗口；
- 非 MSIX 分发，没有系统级安装/更新；
- 胶囊固定取当前分组前 3 个工具（显示哪些不可自定义），但方向可配置；
- 主题"跟随系统"在启动时读取，运行中切换系统主题不会实时刷新；主题本身是全局的，不能每盒不同；
- 分组不支持拖拽排序；
- 多盒子最多 **4 个**；所有盒子的面板窗口启动时都会创建（未贴边/隐藏的也占内存），同边多个盒子的胶囊
  没有自动堆叠/避让，跨盒子拖拽工具未实现，详见 [RESEARCH-MULTI-BOX.md](RESEARCH-MULTI-BOX.md)；
- 暂无自动化测试（v1 的冒烟测试随 WPF 版本一起移除）。

## 14. 后续

1. 装好 VS「Windows 应用开发」工作负载后，去掉 `Directory.Build.targets` shim，改 MSIX 打包；
2. 胶囊自定义显示项 + 搜索历史；
3. 分组拖拽排序、多选批量操作；
4. 多盒子第二版：胶囊/面板按需创建与回收、同边胶囊堆叠/避让、跨盒子拖拽，按预研文档继续做 N=8 压力验证；
5. 为 `Services`（快捷方式解析、搜索匹配、存储）补回冒烟测试。
