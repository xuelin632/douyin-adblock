# 抖音去广告（Windows 便携版）

通过本地 mitmproxy 代理过滤抖音 PC 客户端推荐列表中的广告，不修改抖音安装文件。

## 下载即用

**[下载 v1.0.1 完整便携包](https://github.com/xuelin632/douyin-adblock/releases/download/v1.0.1/douyin-adblock-portable-v1.0.1.zip)** · **[老用户小补丁](https://github.com/xuelin632/douyin-adblock/releases/download/v1.0.1/douyin-adblock-update-v1.0.1.zip)** · [版本说明](https://github.com/xuelin632/douyin-adblock/releases/latest)

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

### 老用户：覆盖一个文件即可

1. 完全退出抖音，等待旧启动器和本工具的代理进程退出。
2. 下载小补丁，将其中的 `ad_filter.py` 覆盖到**旧版 exe 所在文件夹**。
3. 使用原启动器重新打开抖音，无需重新安装证书。

补丁只包含过滤脚本，不包含运行环境。首次使用请下载完整便携包；旧版启动器若无法运行，改用完整包。

## v1.0.1 修复内容

- 修复推荐列表过滤后压缩标记与响应内容不一致，导致后续视频加载失败。
- 直播和媒体响应在收到响应头后开始流式转发，避免完整缓冲长直播流。
- 移除无效的 CONNECT 放行写法，保留直播 TLS 隧道，并补充无 SNI 时的目标识别。
- 主机和路径单独判断，避免 URL 查询参数中的签名被误判为直播。
- 使用随包 Python 和 `run_mitmdump.py` 启动代理，避免移动文件夹后旧 mitmdump.exe 中的绝对路径失效。

本次经过压缩响应、广告过滤、分页信息、直播识别等 30 项回归检查，以及真实本机代理首段数据转发测试；用户已确认视频和直播恢复正常。

## 使用与代理设置

启动器启动 8080 代理，将系统代理设为 `127.0.0.1:8080`，然后打开抖音。正常退出抖音后停止本工具的代理并关闭系统代理。

- 当前启动器**不会保存和恢复启动前的全部代理设置**；使用其他系统代理软件时，退出后可能需要手动重新开启。
- Clash **TUN 模式可继续开启**。避免其“系统代理”功能与本工具反复覆盖设置。经过 mitmproxy 的出站连接可能显示为 `python.exe`，具体线路由 Clash 规则决定。
- 直播跳过 TLS 解密后仍可能经过本地代理或 Clash TUN，并非保证物理直连。
- 过滤记录位于程序同目录的 `filter.log`。
- 广告按 `is_ads`、`raw_ad_data`、`ad_candidates`、`ad_aweme_source`、`commerce_top_labels`、`simple_promotions` 等字段判断；平台更新后可能需要适配。

卸载时先退出抖音再删除文件夹。如果不再使用其他 mitmproxy 工具，可在 `certmgr.msc` 的“受信任的根证书颁发机构”中删除 mitmproxy 证书。

## 源码与构建

```text
src/ad_filter.py           广告过滤与直播转发
src/DouyinLauncher.cs      Windows 启动器
src/run_mitmdump.py        便携 Python 入口
build/install_cert.py     当前用户证书安装
build/make-portable.py     完整包与升级补丁构建
```

在 Windows 上，使用已安装 mitmproxy 12.3.0 的 Python 3.12 环境：

```powershell
python build/make-portable.py --python-dir 'D:\runtime\python' --version 1.0.1
```

默认输出到 `dist/`。需要 Windows 自带的 .NET Framework C# 编译器。运行时与源码分开发布，证书、私钥、个人配置和运行日志不进入发布包。

[MIT License](LICENSE) · [mitmproxy](https://mitmproxy.org/)
