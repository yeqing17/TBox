# TBox Logo Assets

> 存档：2026-11-04 · 用于 v2.2.0 起的黑白交叠方块标记

| 文件 | 内容 |
| --- | --- |
| `tbox-logo-source.png` | 设计原图：1254×1254 RGBA 透明底，黑色交叠方块 + 白色 T |
| `tbox-logo-512.png` | 生成图：512×512 RGBA，**透明底**，图形占 82% |
| `tbox-logo.ico` | 生成图：16/20/24/32/40/48/64/128/256 九个尺寸，256 帧 PNG 压缩，透明底 |

## 运行时位置

- `src/TBox.WinUI/Assets/tbox.ico`：exe 图标（`ApplicationIcon`）和托盘（`ShellHost`）
- `src/TBox.WinUI/Assets/tbox-512.png`：标题栏 20px / 空胶囊 34px，由 `Services/AppLogo.cs` 共享加载
- `docs/images/app-icon.png`：文档展示

## 重新生成

在仓库根目录执行：

```powershell
python scripts/make_icon.py
```

- 唯一输入是 `docs/logo/tbox-logo-source.png`，不要直接编辑生成物；
- 脚本会把 ICO/512 同步写入 `src/TBox.WinUI/Assets`、`docs/images` 和本目录；
- 裁切先做 `alpha > 8` 阈值 + 3px padding：原图有一圈 alpha 只有 1–8 的极淡光晕，
  直接 `getbbox()` 会让图形偏右下、显小；
- 以黑色图形的 bbox 居中；**背景保持透明，不加白色底板**，图形占画布 82%；
- 透明底在深色任务栏上会只突出白色 T，浅色上则显示黑色方块；这是原设计的自适应效果；
- 16px 因 T 的细节会略软，32/48 清晰；如需更锐的 16px，可单独做一版加粗 T。

## 备注

- README 里的面板/胶囊截图尚未按新 logo 重拍（本次按需求跳过）；
- 标题栏和空胶囊都使用 `Assets/tbox-512.png`；改 logo 后重新构建即可生效。
