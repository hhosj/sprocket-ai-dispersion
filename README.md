# Sprocket AI 散布

**为游戏的火炮引入了类似WOT的散布机制，现在发射的炮弹不会准确的射向瞄准的地方。对玩家火炮也生效**

**因为总是被ai锁弱点，所以使用deepseek开发了这个模组**

*A BepInEx plugin that gives Sprocket's AI gunners realistic shell dispersion instead of laser-like accuracy.*

## 下载

- **[`Sprocket.AiDispersion.dll`](release/Sprocket.AiDispersion.dll)** ← 单文件，Mod Manager 里 **Add mod** 直接选它
- [`Sprocket.AiDispersion-1.0.0.zip`](release/Sprocket.AiDispersion-1.0.0.zip) ← 同样内容，打包形式
- 校验值：[`release/SHA256SUMS.txt`](release/SHA256SUMS.txt)

**直接下载（点开即下）：**
[`Sprocket.AiDispersion.dll`](https://github.com/hhosj/sprocket-ai-dispersion/raw/main/release/Sprocket.AiDispersion.dll) ·
[`Sprocket.AiDispersion-1.0.0.zip`](https://github.com/hhosj/sprocket-ai-dispersion/raw/main/release/Sprocket.AiDispersion-1.0.0.zip)


## 它做了什么

- **试射校对**：AI 对同一目标的头几发误差较大（打远/打近），随后逐发收拢——保留游戏原有的"校射"手感。
----***这一项效果似乎不是很明显***
- **散布圈**：校准完成后，每发炮弹仍在锥形散布圈内随机落点，不会再"连续几发打在同一点"。
- **随火炮变化**：不同炮的散布不同——长管高速炮更准，短管低初速炮更散（可关闭）。
- 玩家自己的炮也同样有散布；只影响本地单机，不改存档。

## 安装（Windows，推荐）

1. 下载 **Sprocket Mod Manager**（Hans21223 的 Sprocket-Mod-Loader 项目 Release 里的 `Sprocket-Mod-Manager-1.4.0.zip`），解压到任意固定位置。
2. 双击 **`Mod Manager.exe`**（自带 Python，不需要安装别的东西）。
3. 点 **Install mod loader**，等到底部出现 `Installed and verified … loader files`。
   - 国内下载失败时：先用浏览器把 **BepInEx be.788** 和 **Sprocket 兼容补丁** 两个原始 zip 下好放进"下载"文件夹，再点一次按钮（Mod Manager 会自动采用并校验）。
4. 点 **Add mod** → 选上一步下载的 `Sprocket.AiDispersion.dll`（或 `.zip`）→ 在列表里**双击**启用。
5. 点 **Launch**。首次启动慢 1~3 分钟（在生成 interop），之后正常。


## 安装（Linux / Steam Deck）

Mod Manager 是 Windows 专用程序，Linux 只能手动装：

```bash
GAME="$HOME/.local/share/Steam/steamapps/common/Sprocket"; cd "$GAME"

# 1) 加载器：be.788 + 兼容补丁（两行校验必须都 OK）
curl -fL -o bepinex.zip 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
curl -fL -o patch.zip  'https://github.com/Hans21223/Sprocket-Mod-Loader/releases/download/v1.4.0/Sprocket-Mod-Loader-1.2.3.zip'
sha256sum -c <<'SUMS'
f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a  bepinex.zip
e54f09a863913b96dc2eb15402ee1a8a91ba46d60548c3be04c6e672ec43fe15  patch.zip
SUMS
unzip -oq bepinex.zip
unzip -oq patch.zip 'Sprocket-Mod-Loader-1.2.3/Patch/*' -d t && cp -r t/Sprocket-Mod-Loader-1.2.3/Patch/BepInEx . && rm -rf t bepinex.zip patch.zip

# 2) 预置 3MB 的 Unity 基础库（国内容易卡死，建议先放好）
mkdir -p BepInEx/unity-libs
curl -fL -o BepInEx/unity-libs/6000.3.21.zip 'https://unity.bepinex.dev/libraries/6000.3.21.zip'

# 3) 本模组
unzip -o ~/Downloads/Sprocket.AiDispersion-1.0.0.zip -d BepInEx/plugins/
```

然后在 Steam → Sprocket → 属性 → 启动选项加上（**必须**，否则 Proton 不会加载模组）：

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

## 配置

编辑 `BepInEx/config/sprocket.ai.dispersion.cfg`，改完**重启游戏**生效：

| 项 | 说明 |
| --- | --- |
| `AccuracyAt100m` | 整体散布大小。含义是"100 m 处 95% 落点圆的半径（米）"，默认 `0.28`，越小越准 |
| `Mode` | `GunData` = 按火炮数据（默认）；`Fixed` = 所有炮统一用上面的值 |
| `LooseSideCompression` | 跨炮差距：`0` 差距最大，`1` 完全压平（默认 `0.4`） |
| `[AI zeroing-in]` | 只想调 AI 的试射/校准行为时改这里 |

## 卸载

删掉 `BepInEx/plugins/Sprocket.AiDispersion.dll`。
想连加载器一起清掉：再删 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx/`、`dotnet/`。

## 注意

- **玩家自己的炮也有散布** —— 炮弹发射层拿不到"谁开的炮"，所以无法只作用于 AI。

## 许可 / 致谢

本模组：MIT，见 `LICENSE`。

加载器是第三方项目，本仓库**不包含**它们：

- [BepInEx](https://github.com/BepInEx/BepInEx)
- [Hans21223/Sprocket-Mod-Loader](https://github.com/Hans21223/Sprocket-Mod-Loader) —— Sprocket 专用兼容补丁 + Mod Manager（LGPL-3.0）
- [Neemogudmen/SprocketSDK](https://github.com/Neemogudmen/SprocketSDK) —— 其发布包提供可直接下载的 be.788 底座（MIT）

开发与逆向笔记见 [`docs/开发笔记.md`](docs/开发笔记.md)。
