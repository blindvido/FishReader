# FishReader · 轻阅

Windows 桌面 TXT 阅读器与内嵌网页浏览器，基于 C#、WPF 和 Microsoft Edge WebView2。

## 功能

- **TXT 阅读**：自动识别常见中英文章节标题，目录选章、章节页数、全书进度，读到章末继续翻页可进入下一章。
- **自动记忆**：逐本保存阅读位置，恢复上次书籍、窗口大小、配色及网页地址。
- **桌面显示**：窗口置顶、透明背景、自定义文字颜色，支持四边和四角缩放，字号随窗口尺寸调整。
- **摸鱼模式**：隐藏工具栏和任务栏按钮，只显示内容；鼠标移出后隐藏，按住 **Ctrl 并双击原窗口范围**唤出。当前版本不会通过鼠标悬停自动唤出。
- **网页浏览**：地址栏、前进/后退、刷新、手机模式、25%–200% 缩放、适应弹窗及网页透明。手机模式支持将鼠标拖动转换为触摸输入。

## 运行环境

- Windows 10 1809 或更新版本。
- [.NET 10 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；开发时安装 .NET 10 SDK。
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

本仓库保存源码。构建后打开 `dist/FishReader.exe`，分发时保留整个 `dist/` 文件夹。运行新版前先退出旧版，避免文件被占用。

## 构建与启动

在 PowerShell 中执行：

```powershell
git clone https://github.com/blindvido/FishReader.git
cd FishReader
# 还原依赖、编译并发布到 dist/
./build.ps1
# 使用独立数据目录开发运行
dotnet run --project FishReader.csproj -- --data-dir ./artifacts/dev-data
```

首次构建需要连接 NuGet。`build.ps1` 会把使用说明和示例 TXT 一并复制到发布目录。

## 常用操作

| 操作 | 方法 |
| --- | --- |
| 打开 TXT | Ctrl+O、点击「打开 TXT」或拖入文件 |
| 下一页 / 上一页 | Page Down / Page Up，或空格 / Shift+空格 |
| 下一章 / 上一章 | Ctrl+Right / Ctrl+Left，或章节栏按钮 |
| 移动 / 缩放窗口 | 拖动顶栏 / 边缘及四角 |
| 唤出隐藏窗口 | Ctrl+双击原窗口范围 |
| 找不到窗口 | 双击托盘图标，或右键选择「显示阅读器（关闭摸鱼）」 |
| 网页地址栏 / 刷新 | 网页模式下 Ctrl+L / F5 |
| 登录弹窗显示不全 | 点击「适应弹窗」或减小网页缩放比例 |

详细操作、章节规则和透明效果说明见 [使用指南](docs/usage.md)。

## 数据与限制

默认数据目录为 `%LOCALAPPDATA%/FishReader/`：`state.json` 保存阅读进度和设置，`WebView2/` 保存独立浏览器缓存与登录状态。使用 `--data-dir "目录"` 可以隔离数据；同一目录只允许运行一个实例。

TXT 阅读不会上传书籍内容；网页浏览会访问所输入的网站。进度按文件绝对路径识别，移动或重命名书籍会视为新书。支持 UTF-8、带 BOM 的 UTF-16/UTF-32，以及 GB18030 回退，单本上限 32 MB。

网页透明不消除图片、视频和画布中的底色；网站布局、登录验证和手机适配仍取决于网站本身。显示异常时可关闭「网页透明」或「手机浏览」。0% 背景保留极低透明度以接收鼠标输入。

## 开发与验证

```powershell
dotnet restore Tests/FishReader.Tests.csproj --configfile NuGet.Config
dotnet build FishReader.csproj --no-restore
dotnet run --project Tests/FishReader.Tests.csproj --no-restore
```

测试使用自带 STA 控制台程序，成功标志为退出码 0 和 `Failures: 0`，覆盖解码、进度、章节、窗口显示和本地网页交互。测试会显示窗口并短暂移动鼠标，需要可交互的 Windows 桌面；不操作真实网站验证码。

本次导入验证：构建通过（0 警告、0 错误）；23 项回归检查中 22 项通过，网页综合检查在连续导航后等待「加载完成」时超时，后续网页检查未执行，该问题仍待排查。

| 路径 | 职责 |
| --- | --- |
| `App.cs` | 启动、数据目录及单实例 |
| `ReaderWindow.cs` / `SettingsWindow.cs` | 阅读窗口与设置 |
| `BrowserPanel.cs` | WebView2 浏览与输入适配 |
| `Core.cs` / `Chapters.cs` | 数据保存、文本解码、章节和窗口几何 |
| `ChapterPickerStyle.xaml` | 章节选择器样式 |
| `Tests/Program.cs` | 回归检查 |
| `docs/` | 使用说明与设计记录 |

## 协作更新

修改前先 `git pull --ff-only`，建议在独立分支开发，通过 Pull Request 合并。提交时说明变更行为和验证结果；界面修改附截图。不要提交 `bin/`、`obj/`、发布目录、书籍、个人进度或浏览器登录缓存。历史设计文档仅供参考，当前行为以源码和使用指南为准。
