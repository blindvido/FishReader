# 墨鱼阅读静态分析与实现对照

分析目标：`C:/Users/X/AppData/Local/Programs/moyuread2`。

## 已确认

- `resources/app.asar/package.json` 标明名称 moyuread2，版本 2.4.3，描述为 PC 网页浏览和阅读工具。
- 安装目录含 Electron/Chromium 的本地化资源、图形库及快照，主可执行文件约 204MB。
- 主入口为 `out/main/main.js`，另有 `out/preload/preload.js`。主进程脚本约 1.54MB，preload 约 36.6KB。
- 声明的依赖包括 better-sqlite3（本地 SQLite）、chardet/iconv-lite（编码处理）、epub2（EPUB）、electron-updater（更新）以及 CodeMirror 相关包。
- F:/Fish/moyu 中另有 PyQt5/QtWebEngine 原型，文本仅 UTF-8 解码，无阅读进度；鼠标回到区域还需要左右键同按，与本次要求不符。

## 分析边界

以上来自安装文件和资源包元数据的只读检查。未运行原程序验证界面，未完整审计其主进程、数据表、网络行为或安全性。依赖表只能说明引入了相关库，不能证明具体功能如何实现。未复制原程序代码或素材，未修改安装文件。

## 新程序

轻阅以 WPF 实现本地 TXT 阅读，不引入浏览器内核。

| 用户要求 | 实现 |
|---|---|
| TXT 与进度 | 多种中文编码；每个文件保存顶部可见字符位置，重排时钳制到有效位置；JSON 持久化 |
| 透明与置顶 | 背景 alpha 与文字独立；0% 视觉透明；原生 Topmost 开关 |
| 移出隐藏、返回显示 | 原生窗口矩形与全局鼠标位置，每 40ms 检测；Hide 后继续检测，返回 Show |
| 隐藏后可找回 | 托盘恢复并关闭摸鱼，保留任务栏入口；设置/文件对话框暂停自动隐藏 |

进度按文件路径识别，而非复制小说内容；这使应用数据保持很小，但文件移动后不会自动关联原书签。
