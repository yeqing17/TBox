# 多盒子 / 多胶囊预研

> 状态：预研（不改代码） · 基线版本：v2.2.0（当前工作区，含顶边贴边 / 横向胶囊） · 2026-11-04

## 1. 目标与非目标

**目标**：在同一个 TBox 进程里管理多个“盒子”，每个盒子有自己的工具分组、贴边位置、
收起形态和胶囊；先回答“能不能做、怎么做、性能代价多大”。

**非目标**：本阶段不实现多盒子 UI，不改现有 `data.json`；只产出架构选项、数据迁移方案、
性能预算和分阶段落地计划。

## 2. 现状盘点

当前是**单盒子**架构，下面这些状态是“每窗口/全局”的边界：

| 状态 | 位置 | 多盒子时需要 |
| --- | --- | --- |
| 面板窗口、标题栏、搜索、分组/卡片 | `MainWindow.xaml.cs` | 每盒子一个窗口或一个面板实例 |
| `_currentGroup`、`_filter`、`_mode`、拖拽/缩放标志 | `MainWindow.xaml.cs` | 每盒子 |
| `_dockTimer`、`_hoverSince`、`_leaveSince`、`_collapsed`、`_sliding`、`_capsule` | `MainWindow.Docking.cs` | 每盒子；轮询定时器建议合并 |
| `_autoFitReady/_autoFitScale`、`XamlRoot.Changed` 钩子 | `MainWindow.AutoFit.cs` | 每盒子，但只在可见/激活盒子执行 |
| `TBoxData.Groups/Items/Settings/Window` | `Models/AppModels.cs` | 拆分全局设置与每盒子数据 |
| `DataStore.Instance`、400ms 保存定时器、单文件原子写 | `Services/DataStore.cs` | 共享一份存储，加载一次；保存合并 |
| `ShellHost` 托盘图标/全局热键（固定 class、hotkey/tray ID） | `Interop/ShellHost.cs` | 一个所有者统一管理；热键按盒子分配 |
| `WindowChrome._panelHandle`、`KeepAtBottom`（静态） | `Interop/WindowChrome.cs` | **必须改成每窗口**，否则只有一个窗口能保持置底 |
| 原始 WndProc 映射（按 HWND） | `Interop/WindowChrome.cs` | 保持按 HWND；关闭时清理，避免泄漏 |
| `AppTheme.Current`（静态） | `AppTheme.cs` | 建议全局主题；每盒子主题会牵动胶囊/对话框 |
| `CapsuleWindow`（每盒子一个顶层窗口，无 Dispose） | `CapsuleWindow.cs` | 每盒子；增加 Close/Dispose，懒创建 |
| 图标提取/PNG 缓存，`IconLoader` 无内存缓存 | `Services/ShellIcons.cs`、`IconLoader.cs` | 共享缓存，按路径+mtime+尺寸复用 |
| 50ms 停靠轮询、400ms 自动收起 | `MainWindow.Docking.cs` | 合并成一个共享游标轮询 |
| 单实例 Mutex、`App` 只持有一个 `_window` | `App.xaml.cs` | 改为持有一个盒子管理器 |

关键耦合：

- `MainWindow` 构造函数里直接 `_store.Load()`；多窗口会重复加载并替换共享数据，必须改成
  App 启动时加载一次。
- `WindowChrome.KeepAtBottom` 是静态开关，且只对 `_panelHandle` 生效；多个面板同时存在时，
  只有最后注册的那个能保持桌面层。
- 贴边坐标、胶囊位置、DPI 缩放都依赖**窗口自己的** `Scale`/`WorkArea`；每盒子要各自计算，
  不能共用一个全局 `DockSide`。
- 当前的“收起不隐藏、只平移出屏”策略避免了闪屏，但每个盒子都会长期保留一个顶层窗口
  （面板 + 胶囊），这是多盒子内存的主要来源。

## 3. 产品形态

建议先支持 2–4 个盒子，每个盒子：

- 一个名字/图标，可创建、重命名、删除；
- 自己的分组和工具（当前 `groups` 从全局移到盒子内）；
- 自己的贴边位置（左/右/上）、贴边宽度、收起形态、胶囊方向、auto-fit 设置；
- 一个胶囊（可选择懒创建）；同一屏幕边缘允许放多个盒子时，按插入顺序堆叠并留间距，
  不自动合并成一个胶囊（合并会让“这是哪个盒子”变得不清晰）。

全局保留：主题、开机自启、托盘、全局热键策略、对话框窗口。

## 4. 架构选项

### A. 一个进程 + 每盒子一个 `BoxWindow`（推荐）

把现在 `MainWindow` 的“盒子状态”抽成 `BoxController`，窗口是 `BoxWindow`（或继续叫
`MainWindow`），`BoxManager` 持有 1..N 个盒子；`ShellHost`、存储、图标缓存、游标轮询共享。

- 优点：每个盒子可以独立贴边/收起/置顶，交互最自然；复用现有窗口/胶囊代码；进程内通信零成本。
- 代价：N 个顶层 WinUI 窗口的 XAML island/合成/背板开销；z-order、贴边冲突、托盘/热键要统一；
  `WindowChrome` 静态状态必须重构。
- 适合：2–4 个盒子、同屏使用。

### B. 单面板窗口 + 盒子切换器 + 多胶囊

只保留一个展开面板，标题栏加盒子切换；每个盒子仍有一个胶囊窗口。切换盒子时换面板内容。

- 优点：展开面板只一份 XAML，内存最低；实现比 A 小。
- 代价：不能同时展开两个盒子；切换时面板要重排；胶囊仍是多个窗口；用户需要先切盒子再操作。
- 适合：盒子主要作为“分类入口”，不要求同时展开。

### C. 每盒子独立进程

- 优点：崩溃隔离、内存可回收、互不干扰。
- 代价：每个进程都要加载 .NET/WinUI/运行时，内存成倍；托盘/热键/单实例/数据写入要跨进程协调；
  升级和分发复杂。**不推荐**。

### D. 单窗口承载多面板

一个 HWND 内放多个面板。贴边/置顶是按窗口的，无法让两个面板分别贴不同边；**不推荐**。

**结论**：先用 A，并给“同时可见的展开面板/胶囊数量”设上限；如果实测内存超预算，再退到 B
（展开面板单例 + 多胶囊）。

## 5. 数据模型与迁移

建议 `data.json` 升到 v2：

```jsonc
{
  "version": 2,
  "settings": {                 // 全局
    "theme": "System",
    "runAtStartup": false,
    "hotkey": "Ctrl+Alt+T"
  },
  "boxes": [{
    "id": "b1",
    "name": "开发",
    "icon": "dev.png",
    "settings": {               // 每盒子
      "dock": "Top",
      "dockWidth": 420,
      "collapsedStyle": "Capsule",
      "capsuleOrientation": "Auto",
      "autoFitWindow": true,
      "autoFitMaxRows": 4,
      "lastGroupId": "g1"
    },
    "window": { "x": 100, "y": 0, "width": 400, "height": 318 },
    "groups": [{ "id": "g1", "name": "常用", "items": [] }]
  }]
}
```

迁移：v1 的 `settings.groups.window` → 一个默认盒子；`dockWidth` 留在盒子设置里；全局主题/
开机自启/热键提到 `settings`。`DataStore.Normalize` 负责补默认值和枚举校验，v1 文件读取后
立即写回 v2（保留 `.bak`）。

图标：缓存 key 用 `路径 + 修改时间 + 目标尺寸`；不同盒子引用同一路径时共享。存储仍是单文件
原子写；盒子很多/工具很多时再考虑按盒子分文件或用 SQLite，先不要提前复杂化。

## 6. 性能

### 6.1 当前单盒子基线（实测）

环境：Windows 10.0.26300 x64、1920×1080 @100%、**Release**、演示数据 8 个工具、TBox v2.1.0 +
当前工作区。CPU 为 10 秒空闲采样，`systemPct` 按逻辑处理器数折算。

| 场景 | 可见窗口 | Working set | Private | 10s CPU | 说明 |
| --- | --- | --- | --- | --- | --- |
| 悬浮，面板可见 | 1（400×318） | 166.4 MB | 164.1 MB | 0 s（0%） | 无胶囊 |
| 顶边收起，胶囊（亚克力） | 2（面板 400×318 + 胶囊 189×56） | 163.0 MB | 163.6 MB | 0.031 s（0.04%） | 50ms 游标轮询 |
| 顶边收起，胶囊（不透明） | 2 | 162.8 MB | 163.0 MB | 0.016 s（0.02%） | `TBOX_OPAQUE=1` |
| 顶边展开（不透明，受鼠标干扰） | 1–2 | 172.8 MB | 167.6 MB | 0.062 s（0.08%） | 含展开/收起瞬间 |

说明：

- 单次采样，工作集波动约 ±3–5 MB；胶囊窗口增量被噪声盖住，**不能**用这组数直接乘以 N。
- 收起态面板停在屏幕外，合成可能被挂起；亚克力在“面板不可见”时差异很小。可见的多个面板/
  胶囊同时使用亚克力时，GPU/内存代价会明显得多，需要专门 spike。
- 一个 50ms 轮询约 0.04% system；如果每个盒子各起一个定时器，会线性叠加，必须合并。

### 6.2 多盒子必须做的优化

| 项 | 现状 | 多盒子方案 |
| --- | --- | --- |
| 游标轮询 | 每窗口一个 50ms `DispatcherQueueTimer` | `BoxManager` 一个共享定时器；只遍历“已收起”的盒子；没有收起盒子时停表 |
| 窗口 | 面板常驻（收起只移出屏）+ 胶囊 | 每个盒子懒创建胶囊；未贴边/长期不用的盒子可销毁窗口；活跃盒子保留暂停的窗口 |
| 背板 | 每个窗口亚克力 | 仅激活/可见盒子用亚克力；收起/后台盒子用不透明或关闭 backdrop；提供“性能模式” |
| 图标 | 每窗口 `LoadIconsAsync`；`IconLoader` 无内存缓存 | 共享线程安全缓存（路径+mtime）；只加载当前可见盒子；限制并发（如 4）；按显示尺寸解码 |
| auto-fit | 每窗口 `XamlRoot.Changed` + RefreshView 触发 | 只在可见/激活盒子执行；分组变更合并/防抖；隐藏盒子不 resize |
| 数据写入 | 单例 + 400ms 防抖 | 保持单写入器；多盒子共用一个脏标记/定时器；避免每盒子一个写盘任务 |
| 启动 | 加载所有分组/当前盒子的图标 | 先加载默认盒子；其他盒子懒加载；不要启动时预载所有图标 |
| 托盘/热键 | `ShellHost` 每窗口一个 | 一个 `ShellHost`；托盘菜单列出盒子；热键绑定到“当前/指定盒子”，避免 ID 冲突 |

### 6.3 建议预算（待 spike 验证）

- 4 个盒子（其中 1 个展开、其余收起）：空闲 CPU < 1% system，Working set < 350 MB，
  启动到首个窗口可见 < 1.5s。
- 8 个盒子：Working set < 600 MB，空闲 CPU < 2%；超出则限制同时存在的展开面板/胶囊数量，
  或退到“单展开面板 + 多胶囊”方案。
- 单个盒子的图标加载不阻塞 UI 线程；切换盒子时首帧 < 100ms（图标可异步补）。

### 6.4 测量方法

- 进程级：`Get-Process` 的 `WorkingSet64`/`PrivateMemorySize64`/`TotalProcessorTime`（本预研用的方法）。
- 窗口级：`EnumWindows` 统计每盒子面板/胶囊窗口数；对比隐藏/移出屏/销毁三种策略。
- 渲染：WPA/ETW（WinUI/XAML、DWM）看合成与背板开销；GPU 内存用任务管理器/GPUView。
- 压力场景：N=2/4/8，每个盒子 8/30/100 个工具，分别测“全收起、1 个展开、全部展开”。
- 结论以 Release、真实数据目录、冷启动为准；Debug 只用于功能验证。

## 7. 风险与难点

1. **`WindowChrome` 静态状态**：`KeepAtBottom` 和 `_panelHandle` 现在是全局单值，多面板必须
   改成每窗口状态；`OriginalProcedures` 关闭时要移除，避免 HWND 复用/泄漏。
2. **z-order/贴边冲突**：多个贴边面板都置顶；同一边的多个胶囊要堆叠/避让，展开时不能互相遮挡。
3. **激活与自动收起**：一个盒子展开时，另一个盒子的 400ms 收起逻辑不能误判；需要“当前盒子”
   或按窗口的鼠标命中。
4. **托盘/热键**：`ShellHost` 的窗口类名、热键 ID、托盘 ID 是固定的；多盒子要么共用一个 owner，
   要么为每个盒子分配唯一 ID。
5. **WinUI 多窗口生命周期**：所有窗口要被强引用（当前 `MainWindow` 由 App 持有，`DialogWindow`
   用静态列表）；`AppWindow` 在 re-parent 后可能为 null，构造时缓存。
6. **DPI**：每个盒子可能在不同显示器/缩放下；胶囊命中、贴边坐标、auto-fit 都要用各自窗口的
   `Scale`，不能共享全局 `Scale`。
7. **数据一致性**：多个盒子共享一个 `DataStore`，保存要合并；不要让每个 `BoxWindow` 各自
   `Load()`（当前构造函数就是这样）。
8. **主题**：建议全局主题；如果以后要每盒子主题，胶囊/对话框需要按盒子传主题，成本明显。

## 8. 分阶段落地

1. **抽取（不改行为）**：从 `MainWindow` 抽出 `BoxController`/`BoxWindow`，把 `_currentGroup`、
   dock/capsule/auto-fit 状态收进盒子；`WindowChrome` 改每窗口；保持单盒子构建/回归。
2. **数据 v2**：`TBoxData.Boxes`、全局/每盒子设置拆分、v1 迁移、`DataStore` 只加载一次。
3. **管理器**：`BoxManager` 持有 N 个盒子；共享 `ShellHost`、一个游标轮询、托盘菜单/热键；
   先支持 2 个盒子。
4. **共享服务**：图标缓存、对话框、主题；胶囊懒创建/销毁；贴边堆叠规则。
5. **性能 spike**：N=2/4/8 按 §6.4 测；根据结果决定上限，必要时退到“单展开面板 + 多胶囊”。
6. **UX**：新建/重命名/删除盒子、托盘切换、设置（全局 vs 盒子）、跨盒子拖拽（最后做）。

## 9. 结论

多盒子在一个进程里可行，推荐 **A：每盒子一个 `BoxWindow` + `BoxManager` 共享服务**；先把
`WindowChrome`、`DataStore.Load()`、`ShellHost`、50ms 轮询这些“全局假设”拆掉，再做 N=2/4/8
的性能 spike。当前单盒子基线约 160 MB、空闲 CPU < 0.1%，但多窗口不能线性外推；胶囊/面板窗口
和亚克力背板是主要变量，懒创建 + 共享轮询/图标缓存 + 收起态不渲染是必须的优化项。
