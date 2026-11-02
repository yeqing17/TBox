# TBox 技术方案

> v2.0.0 · WinUI 3（Windows App SDK 1.7）+ .NET 8 · 最后更新 2026-11-02

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
├─ App.xaml(.cs)              组合根：单实例、加载数据、应用主题、创建主窗口
├─ MainWindow.xaml(.cs)       面板 UI：标题栏、标签、卡片、搜索、右键菜单、对话框
├─ MainWindow.Docking.cs      贴边 / 胶囊 / 竖条 状态机（partial）
├─ CapsuleWindow.cs           胶囊窗口（独立顶层窗口，承载快捷图标）
├─ Views/DialogWindow.cs      通用对话框窗口（设置/输入/导入/确认）
├─ Models/AppModels.cs        数据模型 + 搜索匹配
├─ Services/
│  ├─ DataStore.cs            JSON 持久化（防抖 + 原子写 + 备份）
│  ├─ ShellIcons.cs           256px 图标提取 + PNG 缓存
│  ├─ Shortcuts.cs            .lnk 解析 / 桌面枚举
│  ├─ Launcher.cs             启动语义 / 管理员 / 定位 / 开机自启
│  └─ IconLoader.cs           缓存 PNG → BitmapImage
├─ Interop/
│  ├─ NativeMethods.cs        user32 基础调用
│  ├─ WindowChrome.cs         子类化 WndProc：WM_NCCALCSIZE、置底
│  ├─ DwmChrome.cs            DWM 圆角 / 消除 1px 边框
│  └─ ShellHost.cs            托盘图标 + 全局热键（隐藏消息窗口）
├─ AppTheme.cs                浅色/深色解析
└─ Assets/tbox.ico
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
- 菜单/对话框/拖动/输入框聚焦时禁止自动收起。

### 5.1 坐标单位（DIP vs 像素）

进程清单是 PerMonitorV2，所以**有两套单位并存**，混用就会在高缩放屏上出错（曾导致 200% 缩放下胶囊被裁掉一半）：

| 属于 DIP | 属于物理像素 |
| --- | --- |
| XAML 里的所有尺寸（`Width`、`Padding`、`CornerRadius`…） | `GetWindowRect` / `GetCursorPos` / `WM_*` 的 `lParam` |
| 代码里的设计常量（胶囊 56 宽、展开条 38 高、吸附距离 16、竖条露边 16…） | `DisplayArea.WorkArea`、`AppWindow.MoveAndResize`、`AppWindow.Position` |

规则：

- 常量按 DIP 书写，交给上面右列之前一律过 `Dpi.Px(值, Dpi.ScaleFor(hwnd))`；反查用 `Dpi.Dip(px, scale)`；
- 命中测试两边都用物理像素：如展开条带宽用 `CapsuleWindow.ExpandTabPixels`（= DIP × scale），不要直接拿 38 去和 `GetWindowRect` 比；
- 窗口像素尺寸是**用户数据**（`data.json` 的 `window.width/height`、`dockWidth`），只在与最小尺寸比较时换成 DIP 下限；
- 跟随实时缩放：`XamlRoot.Changed` 里的 `RasterizationScale` 变化 → 重新 `MoveAndResize`（XAML 自己会重排，但窗口的像素尺寸不会变）；
- 调试：`%LOCALAPPDATA%\TBox\app.log` 里收起日志带 `size=..×.. scale=..`，可直接核对像素尺寸是否等于 DIP × scale。

## 6. 对话框

早期用 `ContentDialog`，遇到两个问题：

1. 它寄居在面板的 XAML 树里，面板只有 362px 宽时被挤扁、显示不全；面板收起时甚至开在屏幕外；
2. 对话框内容需要独立的宽度和滚动。

改为 `Views/DialogWindow.cs`：独立顶层窗口（480 宽），共用无边框 + 亚克力外观，居中显示，
通过 `TaskCompletionSource` 等待用户结果。

两个必踩的坑：

- **Window 对象被 GC 回收时窗口会消失** → 用静态列表持有，关闭时移除；
- 窗口子类化必须**先登记原过程、再替换**，否则替换瞬间到达的 `WM_CREATE` 查不到表项被吞掉，窗口立即关闭。

## 7. 数据

- `%APPDATA%\TBox\data.json`，camelCase、缩进、枚举存字符串；
- 400ms 防抖保存；写 `.tmp` 后原子替换；覆盖前留 `data.bak.json`；
- 解析失败时备份为 `data.corrupt-{时间}.json` 并用默认配置启动；
- **`IsLoaded` 门闩**：未加载完成前禁止保存，避免第二个实例用空数据覆盖真实配置。

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

## 12. 构建与发布

```powershell
# 开发构建
dotnet build src\TBox.WinUI\TBox.WinUI.csproj -c Debug -p:Platform=x64

# 单文件/目录发布（框架依赖，需要 .NET 8 Desktop Runtime + Windows App Runtime 1.7）
dotnet publish src\TBox.WinUI\TBox.WinUI.csproj -c Release -r win-x64 `
    --self-contained false -p:Platform=x64 -o dist
```

命令行参数：

| 参数 | 作用 |
| --- | --- |
| `--dock-right` | 启动即吸附到右边缘 |
| `--flyout` | 启动即弹出（右下角） |
| `--settings` | 启动后打开设置 |

## 13. 已知限制

- 独占全屏程序（全屏游戏）会盖住置顶窗口；
- 非 MSIX 分发，没有系统级安装/更新；
- 胶囊图标固定取当前分组前 3 个，不可自定义；
- 主题"跟随系统"在启动时读取，运行中切换系统主题不会实时刷新；
- 分组不支持拖拽排序；
- 暂无自动化测试（v1 的冒烟测试随 WPF 版本一起移除）。

## 14. 后续

1. 装好 VS「Windows 应用开发」工作负载后，去掉 `Directory.Build.targets` shim，改 MSIX 打包；
2. 胶囊自定义显示项 + 搜索历史；
3. 分组拖拽排序、多选批量操作；
4. 为 `Services`（快捷方式解析、搜索匹配、存储）补回冒烟测试。
