# 实现笔记

这份是给改代码的人看的。用工具的话看 [README](../README.md) 就够了。

---

## 数据存在哪、为什么这么存

```
%LOCALAPPDATA%\CutTimer\
├─ state.json   当前状态（原子替换写入）
├─ events.jsonl 结算事件账本（只追加，永不重写）
└─ *.log        诊断日志
```

**取舍看的是「零依赖 / 可读可改 / 崩溃安全」，不是性能** —— 数据量极小，数年不到 1 MB。

- **`state.json` 走原子替换**：先写 `.tmp` 再 `File.Replace`。
  断电或崩溃时要么是完整旧文件、要么是完整新文件，绝不会是半个。
- **`events.jsonl` 只追加**：没有损坏风险，且天然是时间序列 ——
  「每日投入明细」直接从它算出来，不需要额外的表。

### 为什么不用 SQLite

读 `.clip` 时确实用了 SQLite（`Microsoft.Data.Sqlite` + `e_sqlite3.dll`，约 2 MB），
但**存自己的数据没必要**：

- 数据量极小，查询就是「按日期分组求和」，几十行代码
- JSON 出问题能用记事本打开修；SQLite 损坏了普通用户没辙
- 账本能直接 `tail` 看、导入 Excel、写脚本算 —— 不依赖本工具也能读

**附带好处**：`events.jsonl` 只追加不重写，所以**即使 `state.json` 被写坏，
「我的时间」也能从账本重算回来**（把 delta 求和即可）。

---

## 构建

```sh
dotnet publish -c Release -r win-x64 --self-contained true -o release
```

需要 .NET SDK 10。产物是**未打包（unpackaged）**应用，没有安装程序，双击 exe 即用。

### 发布方式：不要用 PublishSingleFile

试过，**3 次有 2 次启动即崩**（`0xC000027B STATUS_STOWED_EXCEPTION`），UI 根本不出现；
而且单文件反而更大（148.7 MB）。发布成文件夹后 3/3 稳定。

### 发布包为什么这么大

自包含包约 155 MB，其中大头是：

| 文件 | 大小 | 用途 |
|---|---|---|
| `Microsoft.Windows.SDK.NET.dll` | 23.7 MB | C#/WinRT 互操作投影，必需 |
| `onnxruntime.dll` | 20.7 MB | Windows App SDK 的 AI 组件，**用不到** |
| `DirectML.dll` | 17.8 MB | 同上，**用不到** |
| `System.Private.CoreLib.dll` 等 | ~40 MB | .NET 运行时 |

后两项（约 38 MB）是 WinAppSDK 的 AI/ML 组件，本项目完全不碰。
WinAppSDK 没暴露排除开关，所以第一版先留着 —— 删了若某处引用会运行时崩。
想要「轻量包」就装 .NET 10 桌面运行时 + Windows App SDK 运行时，包体降到 28 MB。

---

## WinUI 3 踩过的坑

按踩到的顺序记。**每一条都真实发生过，不是理论风险。**

### 1. `x:Bind` 默认是 `OneTime`

和 WPF 的 `Binding` **默认行为不同**：`x:Bind` 默认 `Mode=OneTime`，
只在加载时取一次值，之后 `INotifyPropertyChanged` 发了通知也不更新。

症状：列表行显示的还是创建那一刻的值，看着像功能坏了。
本项目栽过 —— 行创建时 `Mine` 恰好是 0，显示成 `—`，之后无论怎么累加都停在 `—`。

```xml
{ x:Bind MineShort }              <!-- OneTime，永远不变 -->
{ x:Bind MineShort, Mode=OneWay } <!-- 正确 -->
```

### 2. `ItemContainerStyle` 里的 `ContextFlyout` 会让右键菜单失灵

用 `Setter` 给所有行设同一个 `MenuFlyout` 实例，点击时 `DataContext` 解析不到具体那一行，
菜单能弹出但点了没反应。正确做法是在 `ContainerContentChanging` 里**逐容器构造**，
闭包直接捕获数据对象。

### 3. 事件别在 XAML 和代码里各挂一次

XAML 里写了 `PointerPressed="OnRootPointerPressed"`，代码里又
`Root.PointerPressed += OnRootPointerPressed`，结果拖动逻辑每按一次跑两遍。**只留一处。**

### 4. `ToggleSwitch` 默认 `MinWidth=154`

在窄卡片里会被裁掉，只看到左半个。必须显式 `MinWidth="0"`。

### 5. `NumberBox` 的两个坑

**回车不一定提交** —— 用户报过「输入数字后按回车无法确定」。
自己挂 `KeyDown` 处理 Enter，从 `Text` 读一次再写回 `Value`；非数字输入还原成当前值。

**初始值会覆盖用户设置（数据丢失级）** —— `NumberBox` 初次加载时 `Value` 是 `NaN`，
但设了 `Minimum="5"` 就会被强制成 5，**并触发一次 `ValueChanged`**，
那一发会走「用户改了间隔」的分支，把存着的 30 分钟写成 5 分钟。

```csharp
private bool _intervalReady;   // SetInterval 首次写入真实值后才置 true

private void OnIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
{
    if (_suppressSettings || !_intervalReady) return;   // ← 挡掉初始那一次
    ...
}
```

### 6. 切页动画不能靠 `EntranceThemeTransition`

它只在「子元素被加入可视树」时播放，而这里的切页只是改 `Visibility`，动画不会重放。
改成代码里手搓 Storyboard：淡入 + 从下方 18px 上滑，260ms `CubicEase.EaseOut`。

柱子的高度动画要显式开 `EnableDependentAnimation` —— `Height` 不是合成属性，
不开的话 WinUI 会当成「每帧都要重排」而直接拒绝播放。

### 7. `NavigationViewItem.IsSelected` 写在 XAML 里不生效

`NavigationView` 认的是 `SelectedItem`。要恢复页面必须走代码 `Nav.SelectedItem = item`。

### 8. `OverlappedPresenter` 没有 `MinWidth`/`MinHeight`

悬浮小窗能被拖到挤成一团（实测 309×340 物理像素时，大圆环直接压到标题行上）。
只能在 `AppWindow.Changed` 里检测尺寸过小就 `Resize` 顶回去 ——
注意顶着的时候要把 `_restoring` 置上，否则这次纠正会被当成「用户改了尺寸」存进 state。

### 9. 拖动无边框窗口：别发 `WM_NCLBUTTONDOWN`

网上最流行的写法是「伪装成按住标题栏」，把移动交给系统：

```csharp
ReleaseCapture();
SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);   // ← 别用
```

问题在于它会**让系统进入模态移动循环，直到左键抬起才返回**。
只要进入时按键状态有一点不一致 —— `PointerReleased` 丢了导致事件状态一直停在「按下」、
或者按下与抬起正好交错 —— 系统就会一直等一个永远不来的「抬起」，
**窗口于是跟着鼠标跑到下一次点击**。

改成**自己移窗**：

```csharp
_winOrigin = AppWindow.Position;
NativeMethods.TryGetCursorPos(out _cursorOriginX, out _cursorOriginY);
// 移动时用绝对位移，不是逐帧增量，所以不会累积漂移
AppWindow.Move(new PointInt32(_winOrigin.X + cx - _cursorOriginX,
                              _winOrigin.Y + cy - _cursorOriginY));
```

只在「指针确实在移动」+「左键确实按着」时才动，从机制上不可能失控。

### 10. 没有单实例保护会真的丢数据

`state.json` 是覆盖写的。同时跑两个 CutTimer，两个实例各自持有一份 State、
各自监视目录，最后写盘的赢 —— 另一个实例刚累计的作业时间就没了。

用命名 Mutex 解决，第二个实例把已有窗口叫到前面然后退出。

### 11. 单实例的「叫醒窗口」不能只找可见窗口

上一条的「叫到前面」写成了**跳过所有不可见窗口**，结果踩了个大坑：

主窗口点 X 之后是**隐藏到托盘**（不是退出），这时整个进程里没有任何可见窗口 →
第二个实例找不到目标 → 什么都不做就退出（退出码 0）。
用户双击 exe 看到的是**完全没反应**，以为程序坏了。

正确做法是分两轮找：

1. 有可见窗口 → 直接置前（正常情况）
2. 一个可见的都没有 → 找到标题为 `CutTimer` 的主窗口，把它显示出来再置前

还有第二个坑：**`ShowWindow` 要用 `SW_SHOWNORMAL`，不能用 `SW_RESTORE`**。
对一个「被隐藏」的窗口调 `SW_RESTORE`，它会显示出来但**停在最小化状态** ——
实测拿到 `visible=True` 却 `iconic=True`，尺寸只有 159x27，用户依然什么都看不到。
`SW_SHOWNORMAL` 则无论原来是最小化还是隐藏，都能正常显示。

> 排查手段：第二个实例会把找了哪些窗口、最终选了哪个写进
> `%LOCALAPPDATA%\CutTimer\single-instance.log`。光看「没反应」是猜不出来的。

### 12. `DllImport` 的 DLL 写错，编译器不会告诉你

托盘图标的注册窗口类那一步要 `GetModuleHandleW`，它声明成了：

```csharp
[DllImport("user32.dll")]                       // ← 错
public static extern IntPtr GetModuleHandleW(string? lpModuleName);
```

**`GetModuleHandleW` 在 `kernel32.dll` 里，不在 `user32.dll`。**
这种错误**编译完全通过**，只在运行时抛 `EntryPointNotFoundException`：

```
System.EntryPointNotFoundException: Unable to find an entry point
named 'GetModuleHandleW' in DLL 'user32.dll'.
    at CutTimer.Core.TrayIcon..ctor()
```

### 13. 吞掉异常会把「功能整个没生效」变成「无从查起」

上面那个异常本来立刻就能发现，但 `SetupTray()` 的 catch 是这么写的：

```csharp
catch (Exception ex) { Debug.WriteLine("tray init failed: " + ex); }   // ← 只进调试器
```

`Debug.WriteLine` 在 Release 下**什么也不写**。于是托盘初始化每次启动都失败，
表象只是「右下角看不到图标」，而**没有任何地方留下痕迹**。

改成 `Log(...)` 落盘后，第一行日志就指出了根因。

> 教训：**凡是「功能静默失效」的地方，catch 里必须落盘。**
> 这个项目里 `Log()` 写 `%LOCALAPPDATA%\CutTimer\mini.log`，
> 托盘创建成功与否也记了一行（`tray icon: added=True ...`）。

> 另外：Win11 对**新出现的**托盘图标默认收进溢出区（`^` 里），
> 所以图标创建成功 ≠ 用户立刻能看到。判断「到底有没有加上」要看
> `HKCU\Control Panel\NotifyIconSettings` 里有没有对应 `ExecutablePath`
> 的条目 —— 加成功了系统才会写这一项。

---

## 字体

整个应用用**更纱黑体（Sarasa Gothic）**。全应用换字体只做了一件事 ——
在 `App.xaml` 里覆盖两个主题资源：

```xml
<FontFamily x:Key="ContentControlThemeFontFamily">更纱黑体 UI SC</FontFamily>
<FontFamily x:Key="ControlContentThemeFontFamily">更纱黑体 UI SC</FontFamily>
```

`TextBlock` / `Button` / `NavigationView` 等控件的默认样式里，`FontFamily` 都指向
`ContentControlThemeFontFamily`，所以覆盖一次就全应用生效。
必须放在 `MergedDictionaries` **后面**（同名资源以本字典自己的为准）。

**时间数字**用等宽变体 `等距更纱黑体 SC` —— 计时器每秒刷新，
比例字体会让数字宽度跳动。注意它的**数字 0 带斜杠**，这是 Sarasa 的特征字形。

> **图标字体不能换。** `Segoe Fluent Icons` 是逐个控件单独指定的，
> 换成中文字体会让所有图标变成方块。

---

## 素材是怎么来的

| 文件 | 来源 | 进仓库？ |
|---|---|---|
| `Assets/CutTimer.ico` | `tools/make-icon.ps1` 由 `tools/dfy-source.png` 合成（圆角白底，7 个尺寸） | ✅ |
| `Assets/mascot.png` | `tools/dfy-source.png` 的副本（界面里的角色头像与立绘用） | ✅ |
| `Assets/chime.wav` | `tools/make_chime.py` 合成的提醒铃声 | ✅ |
| `Assets/csp.png` | `tools/extract-csp-icon.ps1` 从本机装的 `CLIPStudioPaint.exe` 里提取 | ❌ 见下 |

两个生成脚本的路径都是**相对脚本自身**解析的，clone 下来就能直接跑。

### CSP 图标为什么不进仓库

界面上那个「当前是哪个作画软件」的小图标用的是 **CLIP STUDIO PAINT 的官方图标**，
版权属 CELSYS，不适合放进公开仓库。

但程序确实需要它，所以做法是**构建前从本机提取**：

```xml
<Target Name="ExtractCspIcon" BeforeTargets="BeforeBuild">
  <Exec Condition="!Exists('Assets\csp.png')" Command="... extract-csp-icon.ps1" />
</Target>
```

配上 `.gitignore` 里的 `Assets/csp.png`：

- **源码仓库保持干净** —— 不含任何第三方图标
- **本机构建照常有图标** —— 从已安装的 CSP 里现取
- **发出去的包带着它** —— 因为包是在这台机器上构建的

提取不到（本机没装 CSP）时只打印一行提示，**不算构建失败**：
程序启动时查一次文件在不在（`App.HasCspIcon`），不在就把图标位 `Collapsed`，
只留「未在作画」的文字，不会留一块空白。

> 中途试过换成系统字体的调色盘字形 `\uE790` 来彻底避开版权问题，
> 但观感上还是官方图标更清楚，所以最终选了「用，但不进仓库」这条折中路。

图标做法的关键是**先裁再缩**：按 alpha 求出原图真正的内容框，
再把它**填满**圆角卡片（`artRatio = 1.0`）—— 这样角色填满整张卡片，
白底只在四个圆角露出。缩到 0.9 会留出明显白边（试过）；
放到 1.1 会切掉呆毛和两侧头发（也试过）。

铃声是**自己合成**的：免费音效站基本都要登录才能下载，能直接下的多半版权不明。
自己合成没有版权问题，而且**长度完全可控** —— 循环播放时这一点很重要。
音色是 C6 + G6 的上行纯五度，基频叠 2/3/4 次泛音，指数衰减 + 淡入淡出，总长 1.7 秒。

---

## 命令行诊断

```sh
CutTimer.exe --probe "D:\某卡.clip"      # 读单个文件的作业时间
CutTimer.exe --openfiles CLIPStudioPaint # 列出该进程当前打开的 .clip
```

`--probe` 输出 `OK <毫秒> <时分秒> <类型> <工程名> <文件名>`，
失败输出 `FAIL <原因>`。`--openfiles` 用来验证「CSP 到底持不持有 .clip 句柄」——
见 [detection.md](detection.md)。

---

## 源码结构

```
Core/
  ClipWorkTime.cs     读 .clip 内嵌 SQLite 的 CanvasWorkTime
  CutTracker.cs       核心：watcher + 轮询 + 差分结算
  CspWorkspace.cs     从 CSP 设置里读出「上次用的目录」
  WorkspaceScanner.cs 手动扫盘：BFS 找含 .clip 的目录，折叠成少量监视根
  StateStore.cs       state.json + events.jsonl 的读写
  ReminderService.cs  起身/护眼提醒状态机
  StatsService.cs     每日统计聚合
  ToastNotifier.cs    Windows 通知
  TrayIcon.cs         纯 Win32 Shell_NotifyIcon 托盘
  NativeMethods.cs    所有 P/Invoke
MainWindow.xaml(.cs)  主窗口：计时 / 统计 / 设置 / 关于 四页
MiniWindow.xaml(.cs)  始终置顶的悬浮小窗
```
