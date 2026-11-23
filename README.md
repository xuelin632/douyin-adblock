# 抖音去广告（Windows 便携版）

通过本地 mitmproxy 代理过滤抖音 PC 客户端推荐列表中的广告，不修改抖音安装文件。

## 下载即用

**[下载 v1.1.0 完整便携包](https://github.com/xuelin632/douyin-adblock/releases/download/v1.1.0/douyin-adblock-portable-v1.1.0.zip)** · **[老用户升级补丁](https://github.com/xuelin632/douyin-adblock/releases/download/v1.1.0/douyin-adblock-update-v1.1.0.zip)** · [版本说明](https://github.com/xuelin632/douyin-adblock/releases/latest)

支持 Windows 10/11 x64，内置 Python 3.12 和 mitmproxy 12.3.0，无需另装运行环境。
请下载上面的便携包；GitHub 的 **Source code** 压缩包只有源码，不能直接双击使用。

### 新用户：三步启动

1. 下载完整便携包，**解压整个文件夹**到任意位置。
2. 双击 `安装证书.bat`，按 Windows 提示确认安装本机生成的 mitmproxy 根证书。只安装到当前用户，一般无需管理员权限。此证书用于本地 HTTPS 过滤，首次安装一次即可。
3. 双击 `抖音去广告启动器.exe`。以后直接执行这一步。

找不到抖音时，按启动器提示在同目录 `config.txt` 中填写：

```ini
douyin=D:\你的安装目录\douyin.exe
```

### 老用户：覆盖升级文件即可

1. 完全退出抖音，等待旧启动器和本工具的代理进程退出。
2. 下载升级补丁包，将其中的 `抖音去广告启动器.exe`、`ad_filter.py`（及 `DouyinLauncher.cs`）覆盖到**旧版所在文件夹**。
3. 直接运行新版 `抖音去广告启动器.exe`，无需重新安装 Python 或证书。

补丁不包含运行环境，首次使用请下载完整便携包。

## v1.1.0 升级内容

- **TLS 白名单精准解密**：仅解密 `www.douyin.com`；直播、视频 CDN、浏览器及其他客户端全部直接 TLS 隧道透传，不拆包重签，零证书固定（SSL Pinning）冲突与多余开销。
- **代理现场保存与上游自动串联**：启动前自动记录原有系统代理并在退出时原样还原；若启动时已开启上游系统代理（如 Clash 7897），自动以 `--mode upstream:` 串联，刷抖音不断梯子。
- **异常退出防断网与防僵尸进程**：注册 `SetConsoleCtrlHandler` 与 `ProcessExit`，无论退出抖音、直接点关黑框、按 `Ctrl+C` 或关机均自动还原系统代理；使用 Windows Job Object 绑定子进程，清理旧 `mitm.pid` 前校验进程归属，避免 PID 复用误杀。
- **动态冷门端口与就绪探测**：默认改用 `18080`（占用自动顺延至 `18099`），通过 TCP 探测确认代理监听就绪后再切换系统代理，失败直接输出报错并恢复现场。
- **稳定性与细节优化**：单实例互斥防双开；解析 JSON 前校验状态码与内容类型消除空响应 `parse fail`；日志增加完整日期并支持 1 MB 自动轮转；抖音退出连续双检防抖动误关。

本次经过白名单透传、压缩编码、广告过滤、异常响应容错等 37 项离线回归检查，以及真实本机代理首段流式转发测试。

## 使用与代理设置

启动器自动选择本地可用端口（默认 `127.0.0.1:18080`），保存当前系统代理后切换为本地代理并打开抖音；退出抖音或直接关闭启动器窗口时自动停止代理并原样恢复启动前的系统代理。

- **与 Clash 共存**：
  - **Clash TUN 模式**：直接并行开启即可，抖音国内流量由 Clash 规则走 `DIRECT` 直连，不消耗机场节点流量。
  - **Clash 系统代理模式**：启动器会自动检测并串联原有系统代理，退出时自动恢复。
- 过滤记录位于程序同目录的 `filter.log`（超过 1 MB 自动轮转为 `filter.log.old`），控制台每 5 分钟刷新一次本次累计屏蔽条数。
- 广告按 `is_ads`、`raw_ad_data`、`ad_candidates`、`ad_aweme_source`、`commerce_top_labels`、`simple_promotions` 等字段判断；平台更新后可能需要适配。

卸载时先退出抖音再删除文件夹。如果不再使用其他 mitmproxy 工具，可在 `certmgr.msc` 的“受信任的根证书颁发机构”中删除 mitmproxy 证书。

## 源码与构建

```text
src/ad_filter.py           白名单精准解密与广告过滤
src/DouyinLauncher.cs      Windows 启动器（代理现场保存、上游串联、Job Object 守护）
src/run_mitmdump.py        便携 Python 入口
build/install_cert.py      当前用户证书安装
build/make-portable.py     完整包与升级补丁构建
tests/test_proxy_regression.py  离线与本机代理回归测试
```

在 Windows 上，使用已安装 mitmproxy 12.3.0 的 Python 3.12 环境：

```powershell
python build/make-portable.py --python-dir 'D:\runtime\python' --version 1.1.0
```

默认输出到 `dist/`。需要 Windows 自带的 .NET Framework C# 编译器。运行时与源码分开发布，证书、私钥、个人配置和运行日志不进入发布包。

[MIT License](LICENSE) · [mitmproxy](https://mitmproxy.org/)
