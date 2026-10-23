# Sprocket AI Dispersion（AI 命中散布调整模组）

一个给 **Sprocket**（坦克设计/对战游戏）用的 BepInEx 模组：**削弱 AI 炮手的命中精度**，
让它的射击具有更接近实战的散布，而不是几发之后就精确到能稳定命中炮塔座圈这类弱点。

只影响 AI 控制的车辆，玩家自己的炮术保持游戏原样。

> 仅用于个人学习逆向工程与模组开发。请勿用于线上作弊或分发到对局之外。

---

## 1. 游戏原本是怎么算 AI 命中率的（逆向结论）

这部分是本模组的依据，也是值得学习的地方。Sprocket 是 Unity 6 + **IL2CPP**
（`global-metadata.dat` version **39**，游戏版本 `0.2.55.5`），所以所有逻辑都在
`GameAssembly.dll` 里，需要先用 Cpp2IL / Il2CppDumper 把元数据还原出来。

关键类型在程序集 `Sprocket.Vehicles.AI` 中：

| 类型 | 作用 |
| --- | --- |
| `Sprocket.ArtificialIntelligence.GunLayerAI` | 单个"炮层"（炮塔/火炮）的 AI，字段 `maxRangeError`(0x10)、`maxVelocityError`(0x14) |
| `Sprocket.ArtificialIntelligence.GunLayerAISettings` | `maxRangeError` / `maxVelocityError` / `fireAtAnyPoint`，由载具 AI 配置提供 |
| `Sprocket.ArtificialIntelligence.FindAndAimAtTargetGunLayerTask` | **真正的瞄准逻辑**，`Update()` 里算出瞄准点和发射方向 |
| `Sprocket.ArtificialIntelligence.GunLayerAITask` | 基类，含 `AccuracyModifier`（默认 1.0） |
| `Sprocket.Vehicle.VehicleAIAssemblyStage.AddGunLayerAIs()` | **真正给 AI 应用配置的地方**：把 `GunLayerAISettings.maxRangeError/maxVelocityError` 直接写进每个 `GunLayerAI` |
| `Sprocket.ArtificialIntelligence.CommanderAI.Attack()` | 下达攻击命令时把 `AttackOrderInfo.accuracyModifier`（默认 **1.0**）写进任务的 `AccuracyModifier` |

> 注意：`GunLayerAI.SetSettings()` 这个"看起来最该打补丁"的方法在 IL2CPP 里被**内联**掉了，
> 全游戏没有任何地方直接调用它；实际写入发生在 `AddGunLayerAIs()` 里。逆向时不能只看方法名。

`FindAndAimAtTargetGunLayerTask.Update()` 的行为（从 ISIL 反汇编还原）：

1. 用 `LongTargetInfo.CountAttacks()` 统计"已经对同一个目标开火过多少次"，用它去索引一个
   私有静态表 `private static readonly float[] ZeroingErrors`（6 个元素）。
2. `UnityEngine.Random.InitState(目标实体ID ^ 攻击次数)` —— 注意**每帧都重新播种**，
   所以同一发弹的误差是稳定的，只会"每打一发重新摇一次"。
3. 摇出两个 `[-ZeroingErrors[n], +ZeroingErrors[n]]` 之间的随机数，然后乘上：
   `info.RangeError`（= `maxRangeError`）、`info.VelocityError`（= `maxVelocityError`）、
   任务的 `AccuracyModifier`、以及目标尺寸。
4. 再叠加一个固定的 `UnityEngine.Random.insideUnitSphere * 0.25`
   （`MaxAimOffset = 0.25f`，是编译期常量）。
5. 把上述扰动加进拦截点估计里，最后 `Ballistics.CalculateInterceptPoint(...)` 得到瞄准点，
   输出 `GunLayerInstructions.AimDirection`。

**结论**：游戏其实已经有散布模型，但 `ZeroingErrors` 这张表让误差随着"对这个目标打过的次数"
迅速衰减。也就是说 AI 的前几发还算"像人"，打几发之后误差趋近于 0，于是开始精准命中弱点。

实测（模组启动时从内存里读出来的原版数值）：

| 对同一目标已开火次数 | 0 | 1 | 2 | 3 | 4 | ≥5 |
| --- | --- | --- | --- | --- | --- | --- |
| 原版 `ZeroingErrors` | 1.0 | 0.5 | 0.3 | 0.2 | 0.1 | **0.025** |

也就是说**第 6 发之后误差只剩首发的 2.5%** —— 那已经不是"准"，是激光。这就是你遇到的"AI 稳定命中
炮塔座圈"的直接原因。

本模组就是对这一点动手：

* 放大 `ZeroingErrors`（整体散布变大）；
* 给 `ZeroingErrors` 加**下限**（AI 不再越打越准，保持一个稳定散布）；
* 放大 `maxRangeError` / `maxVelocityError`（距离与提前量判断变差，远距离更明显）。

本模组默认设置（`ZeroingErrorsMultiplier = 1.5`、下限 50%）下这条曲线变成：

| 已开火次数 | 0 | 1 | 2 | 3 | 4 | ≥5 |
| --- | --- | --- | --- | --- | --- | --- |
| 默认曲线 | 1.5 | 0.75 | 0.5 | 0.5 | 0.5 | **0.5** |

再乘上原版 `maxRangeError`（实测也是 `0.1`），得到真正决定落点的"相对瞄准偏移"：

| 已开火次数 | 原版 | 默认 |
| --- | --- | --- |
| 首发 | 0.100 | 0.150 |
| ≥5 发 | 0.0025 | **0.050（20 倍）** |

⚠️ **这里有个容易踩的坑**：最终缩放是 `1 ± 偏移`（源码里就是 `x = size * (1 + err)`）。
原版首发 `1 ± 0.10` 很安全；但如果把 `ZeroingErrorsMultiplier` 和 `RangeErrorMultiplier`
**同时**拉大（比如各 3~4 倍），偏移会超过 1.0，`1 - 偏移` 变负数 ——
**瞄准点会翻到支点另一侧**，AI 会朝反方向瞄。那就不是"散布"，是把 AI 弄坏了。
所以这两个倍率是相乘的，建议 `两个倍率的乘积 ≤ 5`。

所有这些量都只被 AI 使用，玩家不经过 `GunLayerAI`，所以玩家手感不变。

---

## 2. 安装

### 2.1 关键前提：BepInEx 不能用官方原版（会黑屏）

Sprocket 是 Unity **6000.3.21f1** / IL2CPP 元数据 **v39**。这里有个坑，社区已经查清并修复：

> **原版 BepInEx 6 be.788（以及 be.789）在这款游戏上会黑屏或崩溃。**
> 原因是 Il2CppInterop 的桥接层对 Sprocket 的原生代码做了错误假设：它按字节特征找到了
> 一个**错误的** Unity 内部函数并挂上钩子（正确目标在 `0x4945E0`，它选到了 `0x16D540`），
> 同时 `Class::Init` 签名匹配失败、泛型方法的调用约定也不对（游戏传 1 个参数，上游钩子要 3 个）。
> 三者叠在一起，进程在加载主菜单时就异常了。

社区方案（**Hans21223/Sprocket-Mod-Loader**）是：用 **BepInEx be.788** 作底座，然后**覆盖 5 个
Il2CppInterop 的 DLL**（`Il2CppInterop.Runtime / .Common / .HarmonySupport / .Generator` +
`TerraFX.Interop.Windows`）——也就是"兼容补丁包"。

所以正确的安装是 **两步**，缺一不可：

```bash
./get-bepinex.sh   # 下载 BepInEx be.788，校验 SHA-256，再叠加兼容补丁
./build.sh         # 编译本模组
./install.sh       # 复制进游戏目录（third_party/ → 游戏目录 + 插件）
```

`get-bepinex.sh` 里已经写死了这几个经过校验的产物（**全部走 GitHub，不依赖可能连不上的
`builds.bepinex.dev`**）：

| 文件 | 来源 | SHA-256 |
| --- | --- | --- |
| `SprocketSDK-0.2.0-full.zip` | Neemogudmen 的 Release（内含**打包好的 BepInEx be.788** + Doorstop + .NET 6 运行时） | `5af1d66a…b247` |
| `Sprocket-Mod-Loader-1.2.3.zip` | Hans21223 的 Release（兼容补丁，覆盖上面那 5 个 DLL） | `e54f09a8…fe15` |

为什么会用 SDK 的包来当 BepInEx 底座？因为官方 be.788 只在 `builds.bepinex.dev` 上，而国内/部分网络
访问它非常不稳定。这个 SDK 的 full 包正好是**原封不动的 be.788**——可以验证：

* 它的 `Il2CppInterop.Runtime.dll` 与 NuGet 上 be.788 依赖的 `Il2CppInterop 1.5.3` **逐字节相同**
  （`65ab051a…38a4`）；
* 它的 `winhttp.dll` / `doorstop_config.ini` / `.doorstop_version` 时间戳正好是 be.788 那个 commit
  （`5b766a3`，2026-09-01 05:04）。

脚本只会取里面的 BepInEx 部分，**不会装 SprocketSDK 自己那个 patcher/plugin**（也就不会带 `Mods/`）。
另外它还会用补丁包自带的 `SHA256SUMS.txt` 再校验一遍那 5 个 DLL。

`install.sh` 默认游戏路径是
`~/.local/share/Steam/steamapps/common/Sprocket`，可用环境变量覆盖：

```bash
SPROCKET_DIR="/path/to/Sprocket" ./install.sh
```

### 2.2 Proton / Steam Linux 注意事项

游戏本体是 Windows 版，靠 Proton 运行，所以要用 **Windows 版 BepInEx**（脚本已经这么做了）。

1. **必须先无 mod 启动过一次游戏**，让 Proton 把 prefix 初始化好，再安装。
2. **必须加这条启动参数**（Steam → 右键 Sprocket → 属性 → 启动选项），否则 Proton 不会加载
   补丁里的 `winhttp.dll`，等于没装：

   ```
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

3. **首次启动要 1~3 分钟**：BepInEx 会跑 Cpp2IL + Il2CppInterop 生成 interop 程序集
   （`BepInEx/interop/*.dll`），Unity 6 的元数据较大；以后正常速度。
4. **首次生成前它还要下一个 3 MB 的 "Unity 基础库"**，这一步很容易卡住：

   ```
   [Message:InteropManager] Downloading unity base libraries
       from https://unity.bepinex.dev/libraries/6000.3.21.zip
   ```

   Il2CppInterop 需要 Unity 官方的托管引用程序集（`UnityEngine.*.dll`）才能生成代理类。
   这个文件只有 3.0 MB，但 `unity.bepinex.dev` 在部分网络下会被限速到几 KB/s 甚至卡死。
   用脚本预置（支持断点续传，可反复跑到成功）：

   ```bash
   ./get-unity-libs.sh        # 下载 + 校验(sha256 d8363294…2ee67f) + 放进游戏目录
   ```

   也可以自己下好丢到 `<游戏目录>/BepInEx/unity-libs/6000.3.21.zip`。
   BepInEx 的规则是：**该 zip 已存在就不再下载**，所以务必放**完整**文件——
   半个 zip 会被当成好文件直接用，然后解压失败。
   想彻底禁掉联网重试，可改 `BepInEx/config/BepInEx.cfg`（首次启动后生成）：

   ```ini
   [IL2CPP]
   UnityBaseLibrariesSource = 6000.3.21.zip   # 只给文件名，就只认本地文件
   ```

5. 日志：`<游戏目录>/BepInEx/LogOutput.log`。看到类似
   `Sprocket / Unity 6000.3.21f1 runtime traced` 就说明兼容补丁生效了。
6. 配置文件：`<游戏目录>/BepInEx/config/sprocket.ai.dispersion.cfg`（首次启动生成）。

### 2.3 也可以直接装现成的"模组管理器"

如果你不想自己拼这两步，还有两个社区项目可以直接用：

* **Hans21223/Sprocket-Mod-Loader** 的 `Sprocket-Mod-Manager`（有 Windows GUI 的 **Install mod
  loader** 一键按钮，自动下载+校验+打补丁），但也带了一个 Python 写的 mod 管理器；
* **Neemogudmen/SprocketSDK**（`SprocketSDK-<ver>-full.zip`）——它把 BepInEx + SDK 打包好了，
  并且提供 **`Mods/<mod>/mod.json` 这种"丢文件夹"式模组**（自定义零件、涂装、平衡数值），
  还带 F8 管理界面。

两者都只是"把加载器和插件放好"，**本模组是一个标准 BepInEx 插件，和它们都能共存**：
把 `Sprocket.AiDispersion.dll` 放进 `BepInEx/plugins/` 即可，不需要写 `mod.json`。

---

## 3. 配置与调参

第一次启动后生成 `sprocket.ai.dispersion.cfg`，改完重启游戏生效。

```ini
[General]
Enabled = true
DebugLogging = true

[Shell dispersion]              # ★ 主力：WoT 式散布圈（炮管不动，炮弹进圈）
Enabled = true
# 100 m 处散布圆半径（米）——就是 WoT 的"精度"概念
AccuracyAt100m = 0.60
Distribution = Gaussian         # Gaussian(中心密) | Uniform(圈内均匀)

[AI zeroing-in]                 # AI 的"试射→校准"保留，只是终点不再归零
# 1.0 = 原版曲线形状（实测原版 1 / 0.5 / 0.3 / 0.2 / 0.1 / 0.025）
ZeroingErrorsMultiplier = 1
# 校准终点不能低于首发的这个比例：原版 0.025 → 本模组 0.1
ZeroingErrorsFloorFraction = 0.1

[AI aim error]
# 距离判断误差倍率 / 下限（原版 maxRangeError 实测 = 0.1）
RangeErrorMultiplier = 1
RangeErrorMinimum = 0
# 提前量（速度）误差倍率 / 下限（原版 maxVelocityError 实测 = 0.1）
VelocityErrorMultiplier = 1
VelocityErrorMinimum = 0
```

设计分工：

* **散布圈（Shell dispersion）**负责"即使瞄得再准也可能打偏"——这是 WoT 手感的核心；
* **试射曲线（AI zeroing-in）**负责"前几发要校炮"——保留原版过程，但校准终点停在首发的 10%。

`AccuracyAt100m` 的手感对照。语义已明确定义为：**100 m 处"95% 落点圆"的半径（米）**
（2D 高斯 → 落点半径服从瑞利分布，95% 落在 2.447σ 内）。因为是角度，圈随距离线性变大：

| `AccuracyAt100m` | 100 m | 300 m | 500 m | 1000 m | 感觉 |
| --- | --- | --- | --- | --- | --- |
| 0.20 | 0.20 m | 0.60 m | 1.00 m | 2.00 m | 很紧 |
| 0.30 | 0.30 m | 0.90 m | 1.50 m | 3.00 m | WoT 里较好的炮 |
| **0.40** | **0.40 m** | **1.20 m** | **2.00 m** | 4.00 m | **默认** |
| 0.60 | 0.60 m | 1.80 m | 3.00 m | 6.00 m | 明显散 |
| 1.00 | 1.00 m | 3.00 m | 5.00 m | 10.0 m | 很飘，远距离靠运气 |

参考尺寸：坦克宽约 3 m，炮塔座圈仅约 0.1 m。所以 **0.40 意味着"打中车体常见、稳定命中座圈不可能"**。

注意 `Distribution = Gaussian` 时约 5% 的弹会落在圈外（正态分布的尾巴）；
想要"硬性不出圈"就改成 `Distribution = Uniform`（此时圈半径就是硬边界）。

调参建议：

* **只想"别那么准"**：只动 `ZeroingErrorsFloorFraction`（`0.5~0.8`）。这是最直接、最安全的一项。
* **想更狠**：再考虑 `ZeroingErrorsMultiplier`（1.5 → 2.5~3.0）。
* **远距离也想有明显散布**：提高 `RangeErrorMultiplier`。**但要记住它和 Zeroing 倍率相乘**。
* **打移动目标时才想散布**：提高 `VelocityErrorMultiplier`。
* **太离谱了 / AI 好像在朝反方向瞄**：把两个倍率的乘积降到 5 以下。
* 打开 `DebugLogging` 后，日志里会打印原版数值（下面是本机实测的真实输出）：

  ```
  [Info] ZeroingErrors tuned: scale x1.5, floor 50% of peak.
  [Info]   before: [1, 0.5, 0.3, 0.2, 0.1, 0.025]
  [Info]   after : [1.5, 0.75, 0.5, 0.5, 0.5, 0.5]
  [Info] [debug] vanilla maxRangeError    = 0.1 -> 0.1   (x1, min 0)
  [Info] [debug] vanilla maxVelocityError = 0.1 -> 0.1   (x1, min 0)
  ```

  注意后两行**只在有 AI 载具生成时**（真正开战）才出现一次，光停在主菜单看不到。

  预设档位（改完重启游戏即可，不用重新编译）：

  | 档位 | ZeroingMultiplier | FloorFraction | RangeMultiplier | 曲线 | 稳定态 vs 原版 |
  | --- | --- | --- | --- | --- | --- |
  | 温和 | 1.0 | 0.5 | 1.0 | `1, 0.5, 0.5, 0.5, 0.5, 0.5` | 20× |
  | **默认** | 1.5 | 0.5 | 1.0 | `1.5, 0.75, 0.5, 0.5, 0.5, 0.5` | 20× |
  | 更强 | 2.5 | 0.6 | 1.5 | `3.75 … ≥0.6` | ~36× |
  | 极限 | 3.0 | 0.8 | 1.5 | 稳定态 = 首发 80% | ~72× |

  （"稳定态 vs 原版"= 默认稳定态偏移 ÷ 原版稳定态偏移 `0.0025`；倍数越大 AI 越像"刚上战场的新兵"。）

---

## 4. 从源码构建

```
src/SprocketAiDispersion/
  Plugin.cs              BepInEx 入口 + 配置项
  Patches.cs             Harmony 补丁（AddGunLayerAIs 缩放误差 / GunLayerAI.Step 触发调表）
  ZeroingErrorsTuner.cs  改写 AI 的"越打越准"曲线
  Interop.cs             反射小工具（不编译期依赖游戏程序集）
```

* 目标框架 `net6.0`（BepInEx 6 IL2CPP 的运行时是 .NET 6）。
* **完全用反射**访问游戏类型，所以编译时**不需要** interop 程序集；依赖只有 BepInEx 自己的
  `BepInEx.Core` / `BepInEx.Unity.IL2CPP`（以及它们带进来的 `0Harmony`、
  `Il2CppInterop.Runtime`），通过 `NuGet.config` 里的 BepInEx 官方源还原。
* 构建：

  ```bash
  ./get-bepinex.sh                       # 只需要跑一次，为了 install.sh 时有加载器可复制
  DOTNET_ROOT=/path/to/dotnet6 ./build.sh
  ```

---

## 附录：相关社区项目（都不是本仓库，使用时请遵守各自许可）

| 项目 | 是什么 | 对本模组的用处 |
| --- | --- | --- |
| [BepInEx](https://github.com/BepInEx/BepInEx) | 通用的 Unity mod 加载器；6.x 的 IL2CPP 分支负责注入 + 生成 interop + HarmonyX | **必需**（底座） |
| [Hans21223/Sprocket-Mod-Loader](https://github.com/Hans21223/Sprocket-Mod-Loader) | 针对 Sprocket 0.2.55.5 / Unity 6000.3.21f1 的 **BepInEx 兼容补丁**（覆盖 5 个 Il2CppInterop DLL），另附一个 Windows/Python 的 Mod Manager | **必需**，否则原版 BepInEx 会黑屏 |
| [Neemogudmen/SprocketSDK](https://github.com/Neemogudmen/SprocketSDK) | 非官方 modding SDK：把 BepInEx 打包好，提供 `Mods/<mod>/mod.json` 式模组、自定义零件/模型、C# modding API、F8 管理界面 | 可选；适合做**数据型**模组，和本插件可共存 |
| [ArgoreOfficial/SMOL](https://github.com/ArgoreOfficial/SMOL) | 2024 年的 C++ "Sprocket Mod Loader"，思路是 patch Addressables 的 `catalog.json`（加资源/零件） | 已过时（针对旧版本），本模组用不上 |

本模组只依赖前两项（BepInEx + 兼容补丁）；后两项是另外的路线，互不冲突。

---

## 5. 卸载

删除 `<游戏目录>/BepInEx/plugins/Sprocket.AiDispersion.dll` 即可；
想彻底移除 BepInEx，再删掉 `winhttp.dll`、`doorstop_config.ini` 和 `BepInEx/` 目录
（`.doorstop_version`、`.doorstop_*` 等若存在也一并删除）。

---

## 6. 已知限制

* 模组依赖游戏的类名/字段名（`VehicleAIAssemblyStage.AddGunLayerAIs`、`GunLayerAI.Step`、
  `maxRangeError`、`ZeroingErrors` 等）。游戏大版本更新后如果这些改名或被编译器内联掉，
  模组会在 `BepInEx/LogOutput.log` 里报错并自动跳过对应功能，需要重新逆向。
* `MaxAimOffset = 0.25f` 是编译期常量，无法在运行时改，所以"最少 0.25 单位球内抖动"
  这一层是无法通过本模组调整的（它本来就是原版散布的一部分）。
* `AccuracyModifier`（来自攻击命令的 `accuracyModifier`，默认 1.0）本模组**没有**改：
  它是在 `CommanderAI.Attack()` 构造任务时一次性写入的，而同一个任务对象会被压进多门炮的
  任务栈，简单地拦截容易把它乘很多遍。需要的话可以后续用"按指针去重"的方式加。
* AI 队友同样会被削弱（模组不区分敌我，只区分"是不是 AI 在操作"）。

---

## 7. 分发与分享（给别人用）

### 7.1 产物

`./pack.sh` 会编译并生成（全部在 `dist/`）：

| 文件 | 说明 |
| --- | --- |
| `Sprocket.AiDispersion-1.0.0.zip` | ★ **给用户的发布包**，里面**只有** `Sprocket.AiDispersion.dll`（12.9 KB） |
| `Sprocket.AiDispersion.dll` | 裸 DLL，也可直接使用 |
| `安装说明.md` | **中文小白安装说明**（A. Windows 用 Mod Manager / B. Linux 手动） |
| `LICENSE` | MIT（发布前把第 3 行的占位符改成你的名字） |
| `校验值.txt` / `*.sha256` | 校验值 |

> zip 里刻意**不放文档**：社区的 Mod Manager 会把整个 folder 布局照搬进游戏目录，
> 根目录只有 DLL 才能确保它被放进 `BepInEx\plugins`。

### 7.2 推荐渠道

1. **GitHub Releases** —— 源码 + 上面那个 zip（必须：别人要能审查源码）；
2. **Nexus Mods** —— Sprocket 的 mod 页收这类东西；
3. **Steam 创意工坊：不支持代码 mod**（工坊只收坦克蓝图），别指望它。

### 7.3 用户怎么装（两条路）

* **Windows**：装社区的 **Mod Manager** → 点 `Install mod loader`（自动装 be.788 + 兼容补丁，
  带 SHA-256 校验/备份/回滚）→ `Add mod` 选我们的 zip → `Launch`。
  **全程图形界面、零脚本。**
* **Linux / Steam Deck**：Mod Manager 是 **Windows 专用程序**（作者原文 *"Mod Manager doesn't
  run on Linux."*），走 `安装说明.md` 里的 B 段——几条可复制的命令，外加启动项
  `WINEDLLOVERRIDES="winhttp=n,b" %command%`。

### 7.4 不要分发什么

* ❌ 不要把 `third_party/`（93 MB，含 BepInEx 与第三方兼容补丁）打进发布包：
  补丁包是 **LGPL-3.0**，作者也明确要求"**不要分发装好的 BepInEx 文件夹**"；
* ❌ 不要分发任何游戏文件（`GameAssembly.dll`、`Sprocket_Data/` …）；
* ✅ 让用户的安装器/脚本**从上游下载并校验**（本仓库的 `get-bepinex.sh` / `install.sh` 就是这么做的）。

致谢与许可见 `安装说明.md` 末尾。

### 7.5 兼容性与冲突

| 冲突类型 | 结论 |
| --- | --- |
| 崩溃 / 加载失败 | 基本不会：所有钩子包了 `try/catch`，出错**自动停用该功能**并写日志；Harmony 支持多 mod 链式补丁 |
| 命名冲突 | 不会：Harmony ID `sprocket.ai.dispersion`、命名空间 `SprocketAiDispersion`、配置文件名均唯一 |
| **效果叠加** | 可能：别人也改弹道/散布时会相乘。用 Mod Manager 的 **Mod report** 或"只留一个 mod"排查 |
| 加载器层 | 与本插件无关；Mod Manager 会拒绝/修复重叠的 loader 条目 |
| 游戏更新后失效 | 我们靠具体字段偏移（`velocity`@0x34、`ID`@0x18、`pool`@0x38 等）定位，更新后可能失效；**最坏只是不生效，不会崩**，日志会写明 |

本模组打的 5 个补丁点（供查冲突）：

```
Sprocket.Vehicle.VehicleAIAssemblyStage.AddGunLayerAIs   (Prefix)
Sprocket.ArtificialIntelligence.GunLayerAI.Step          (Prefix)
Sprocket.DamageModelling.ProjectileRegister.Launch       (Postfix)
  …IProjectileFactory.Launch（显式接口实现）              (Postfix)
ProjectileRegister.FixedUpdate                           (Prefix，只回读校验)
```
