# 抖音去广告（Windows 便携版）

> 通过本地 mitmproxy 代理拦截抖音 PC 客户端的广告数据，实现刷视频不显示广告。
> **不需要 Root、不需要改抖音文件、不需要逆向。**

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)]()
[![Python](https://img.shields.io/badge/python-3.12-3776AB)]()
[![mitmproxy](https://img.shields.io/badge/mitmproxy-12.3-orange)]()
[![License](https://img.shields.io/badge/license-MIT-green)]()

---

## 原理

抖音 PC 端是 Electron 应用，**广告由服务器下发**（前端 18 个 JS 文件里只有 1 处 `isAd`，
说明客户端只是照着渲染）。

所以正确的拦截点不是客户端代码，而是**网络响应**：

```
抖音请求 /aweme/v1/web/tab/feed/
        ↓
服务器返回 { aweme_list: [ ...含广告... ] }
        ↓
   ★ 本工具在这里过滤 ★
        ↓
客户端拿到干净的列表 → 渲染时就没有广告
```

### 广告判定字段

普通视频这些字段全为空，广告有值：

| 字段 | 普通视频 | 广告 |
|---|---|---|
| `is_ads` | `false` | **`true`** |
| `raw_ad_data` | `null` | 有数据 |
| `ad_candidates` | `null` | 有数据 |
| `ad_aweme_source` | `null` | 有值 |
| `commerce_top_labels` | `null` | 有列表 |
| `simple_promotions` | `null` | 有数据 |

命中任一条件，就把该条目从 `aweme_list` 里删掉，重写 response body。

**为什么不需要重算签名**：只改响应、不改请求，抖音的 `X-Bogus` / `a_bogus` 签名完全不受影响。

---

## 快速开始

### 1. 下载

去 [Releases](../../releases) 下载 `抖音去广告便携版.zip`（约 33 MB，含完整 Python 运行时）。

解压到**任意位置**（不能只复制 exe，必须整个文件夹）。

### 2. 安装证书（只需一次）

右键 `安装证书.bat` → **以管理员身份运行**。

它会：
- 用便携版 mitmdump 生成 CA 证书
- 装进「当前用户」和「本机」两个信任区

> 为什么必须装证书：代理要解密 HTTPS 才能改响应，不装证书抖音会报网络错误。

### 3. 启动

双击 `抖音去广告.exe`。它会：

```
[1/3] 启动代理组件（mitmdump --listen-port 8080）
[2/3] 设置系统代理 → 127.0.0.1:8080
[3/3] 通过 explorer.exe 启动抖音
      ↓
   显示进度 4 秒
      ↓
   窗口自动隐藏，后台等待
      ↓
   抖音退出 → 关闭代理 + 停止 mitmdump + 自己退出
```

**关闭抖音后系统代理自动还原，不会影响其他软件。**

---

## 效果

实测日志（`filter.log`）：

```
12:22:41 REMOVED 1/7 ads  <- /aweme/v1/web/tab/feed/
12:27:17 REMOVED 1/7 ads  <- /aweme/v1/web/tab/feed/
12:30:13 REMOVED 1/7 ads  <- /aweme/v1/web/tab/feed/
12:49:32 REMOVED 1/7 ads  <- /aweme/v1/web/tab/feed/
12:59:08 REMOVED 1/7 ads  <- /aweme/v1/web/tab/feed/
13:33:38 REMOVED 1/7 ads  <- /aweme/v1/web/tab/feed/
```

每次 7 条里删掉 1~2 条广告。

---

## 常见问题

### 找不到抖音？

程序会自动在**注册表**和**常见路径**里找。如果找不到，它会生成 `config.txt`：

```ini
# 抖音安装路径（如果自动找不到，取消下面一行的注释并改成你的路径）
# douyin=C:\Program Files (x86)\douyin\douyin.exe
```

改好路径重新运行即可。

### 和 Clash / v2ray 冲突？

Windows 系统代理**同一时间只能有一个**。本工具在抖音运行时占用，**关闭抖音后自动还原**。

如果你经常同时用，可以考虑改 `mitmdump --mode upstream` 链式代理（见「进阶」）。

### 首次启动弹防火墙？

点「允许」即可（只监听本地 127.0.0.1）。

### 广告标记是「品牌号推广」的没拦住？

有一部分广告可能不走 `is_ads` 这套字段。`src/ad_filter.py` 里有 `AD_DEEP_MARKS`
深度检测，命中的会写进 `dump.log`（`DEEP-HIT` 行），欢迎提 issue 提供样本。

### 想彻底卸载？

1. 删掉整个文件夹
2. 打开「管理计算机证书」→「受信任的根证书颁发机构」→ 删除 `mitmproxy`

---

## 进阶

### 链式代理（与 Clash 共存）

修改 `src/DouyinLauncher.cs` 里 mitmdump 的启动参数：

```csharp
"--listen-port 8080 --mode upstream:http://127.0.0.1:7897 -s \"" + AddonPath + "\""
```

这样流量路径是 `应用 → mitmproxy → Clash → 外网`，Clash 的分流规则照常生效。

### 自己编译

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' `
    /nologo /target:exe /codepage:65001 `
    /out:'抖音去广告.exe' 'src\DouyinLauncher.cs'
```

> 源码必须存为 **UTF-8 with BOM**，否则中文乱码。
> 不要在 MSYS/Git-Bash 里调用 csc（`/nologo` 会被当成路径转换）。

### 构建便携包

```bash
python build/make-portable.py
```

（脚本见 `build/` 目录）

---

## 已知限制

| 限制 | 说明 |
|---|---|
| **只支持 Windows** | 用的是 Windows 注册表改系统代理 |
| **启动瞬间可能漏** | 抖音启动时立刻拉数据，若代理还没就绪，第一批 feed 是直连的（刷一下就好） |
| **需要装证书** | 不装就解不开 HTTPS |
| **抖音更新可能需要适配** | 字段名可能变，需要重新抓包确认 |

---

## 技术笔记

### 为什么不能改 douyin.exe

抖音有三道锁：

| 防护 | 作用 |
|---|---|
| `douyin_guard.exe` | 启动前检查命令行参数和环境 |
| `md5.json` | 校验每个 dll/exe 的哈希（119576 字节的校验表） |
| 数字签名 | 文件被改就失效 |

改一个字节 → 签名失效 + md5 对不上 → guard 拒绝启动。而且抖音更新会覆盖。

**独立启动器零风险**：不碰抖音文件、不需要管理员、出问题删图标即可。

### 为什么抖音必须用 explorer.exe 启动

| 启动方式 | 结果 |
|---|---|
| `Start-Process douyin.exe` | ❌ 进程 0 |
| `Start-Process <抖音.lnk>` | ❌ 进程 0 |
| `Process.Start(..., UseShellExecute=true)` | ❌ 进程 0 |
| **`Process.Start("explorer.exe", "\"path\\douyin.exe\"")`** | ✅ 9 个进程 |

抖音检测**父进程**，只接受 Explorer 启动的。

### 为什么放弃 CDP 注入

试过 `douyin.exe --remote-debugging-port=9222` → `douyin_guard.exe`
检测到参数后**主进程根本不启动**。`Ctrl+Shift+I` 无反应。

### 抖音 PC 架构

- 版本 8.7.0，Electron，`resources/app.asar` 15 MB
- 主进程加固：`index.js` / `mainWorker.js` / `preload.js` **都只有 166 字节**，
  是 `require("./__META-INF__/" + platform + arch + ".node").link(...)` 壳，
  真正的代码在 14 MB 的 `Common.Build-*.node` 里
- **渲染层是普通 JS**（仅 webpack 压缩，字符串明文可读）
- **asar 解析**：header 是 4 个 uint32 然后才是 JSON —— JSON 从 **offset 16** 开始，
  长度取 `raw[12:16]`（不是常见的 3 字段格式）

---

## 项目结构

```
.
├── src/
│   ├── ad_filter.py          # mitmproxy addon：广告过滤核心
│   └── DouyinLauncher.cs     # C# 启动器：管代理 + 管抖音生命周期
├── build/
│   ├── install-cert.bat      # 证书安装脚本
│   └── make-portable.py      # 便携包构建脚本
├── README.md
├── LICENSE
└── .gitignore
```

---

## 免责声明

本项目仅供**学习与研究**使用，请勿用于商业用途。

使用者应自行承担使用风险。请遵守抖音的用户协议与相关法律法规。
作者不对因使用本工具产生的任何后果负责。

---

## 致谢

- [mitmproxy](https://mitmproxy.org/) — 强大的中间人代理框架
- 灵感来自 LSPosed 生态的安卓去广告模块（DexKit 运行时反查混淆类）
