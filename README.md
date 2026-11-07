# eraDSH

让 DeepSeek Harness（DSH）内的 AI 读取并操作一个受管理的 Emuera/Era 游戏实例。

**已实测：同进程加载指定旧版 Emuera → 直接读取中文正文、真实按钮值和输入请求 → 通过本地命名管道提交输入 → 返回新观察及游戏客户区截图。**

提供两种使用方式：

- **DSH 工具** `era_bridge`：装进 profile 后，AI 可直接调用 `open / status / observe / act / close`。
- **PowerShell 客户端**：不装插件也能用，适合人工调试与脚本化测试。

> 当前适配器针对 Emuera 程序集 `1.821.5843.38796` 实测通过。引擎内部成员不是稳定契约，换版本必须重新验证；本项目不声称兼容所有 Era 游戏。

## 组成

```text
DSH AI / PowerShell 调用者
  → scripts/Invoke-Bridge.ps1
  → 当前用户 ACL + 会话凭据 + 服务端 PID 校验的本地命名管道
  → EraBridgeHost.exe（x86 / .NET Framework 4）
  → 游戏 UI 线程上的版本专用反射适配器
  → GetDisplayStrings / 按钮对象 / InputRequest / PressEnterKey
```

不做进程注入，也不能接管手工打开的旧窗口。**必须由桥接宿主启动受管理的实例。**

宿主以同 SHA-256 的引擎副本运行，不修改游戏引擎二进制与游戏脚本。

## 环境要求

| 项 | 要求 |
| --- | --- |
| 操作系统 | Windows（Emuera 与宿主均为 Windows/x86） |
| 运行时 | .NET Framework 4.x（Win10/11 自带）；编译使用系统自带 `csc.exe` |
| 脚本 | Windows PowerShell 5.1 |
| 游戏 | 一个可运行的 Emuera 游戏目录，含启动器 + `CSV/ERB/resources` |
| DSH | 仅安装插件时需要 |

不需要联网，不需要 npm 依赖，不需要安装 .NET SDK。

## 配置游戏目录

**默认不需要配置。** 游戏目录就是**当前工作目录**，引擎 exe 在其中自动查找；所以在游戏文件夹里打开 shell（或把 DSH 工作区设为游戏目录）就能直接构建。

解析顺序：

| 值 | 解析顺序 |
| --- | --- |
| 游戏目录 | `-GameRoot` → `ERADSH_GAME_ROOT` → `erabridge.config.json` 的 `gameRoot` → **当前工作目录** |
| 引擎 exe | `-EngineExecutable` → `ERADSH_ENGINE_EXE` → 配置的 `engineExecutable` → **在工作目录中自动查找** |
| 引擎版本 | `-EngineAssemblyVersion` → `ERADSH_ENGINE_VERSION` → 配置项 → 自动读取 |
| 运行方式 | `-RuntimeMode` → `ERADSH_RUNTIME_MODE` → 配置项 `runtimeMode` → **inplace** |
| 运行目录 | `-RuntimeDirectory` → `ERADSH_RUNTIME_DIR` → 配置项 → `runtime/host`（inplace）或 `runtime/instance`（copy） |

游戏目录与插件仓库必须是两个目录；若解析结果等于本仓库，脚本会报错提示。

### 两种运行方式

| 方式 | 行为 | 游戏目录会被写入吗 |
| --- | --- | --- |
| `inplace`（默认） | **什么都不复制**，引擎直接从游戏目录加载并以它作为工作目录 | 会：`Sav`、`Debug`、`Dat`、`emuera.log`、`emuera.config` 都写在原目录 |
| `copy` | 把游戏目录镜像到 `runtime/<name>/`，引擎从副本加载，`Sav`/`Debug`/`Dat` 在副本里重建为空 | 不会 |

默认选 `inplace`，是因为含大量图片/音频的 ERA 目录动辄几个 GB，为观察复制一份既慢又占空间。代价是桥接会话会**写真实存档**；需要与存档隔离时显式切到 `copy`：

```powershell
# 隔离副本（原目录不写入）
.\scripts\Build-Bridge.ps1 -GameRoot 'D:\Games\SomeEraGame' -RuntimeMode copy
# 或设置一次环境变量 / 配置文件里的 runtimeMode
```

`inplace` 模式下宿主程序仍放在本仓库的 `runtime/host/`，游戏目录里只多出游戏自己写的文件。使用时注意：**不要在人工游玩的同时开原地会话**，两个进程会抢同一份存档；想拿真实存档做实验前请先自行备份。

### 引擎自动查找规则

只扫描游戏目录**顶层**的 `*.exe`（Emuera 的启动器总是与 `CSV/ERB/resources` 同级），按托管程序集判断：

1. 程序集名必须为 `Emuera`，其他 exe 一律忽略；
2. 若存在**已验证版本**（当前为程序集 `1.821.5843.38796`）的候选，优先选它；
3. 若只有一个候选，即使版本未验证也会使用，但会打印警告并在构建结果中标记 `engineVerified: false`；
4. 若有多个候选且无法唯一确定，脚本报错并列出它们，此时用 `engineExecutable` 指定一个。

因此一个目录里同时存在 `Emuera1822.exe`（1.822）与游戏自带的旧启动器时，会优先选中已验证的旧版，不会误用新版。

> **兼容性边界**：适配器按引擎形态分两条路线。**.NET Framework** 血统（1.8xx 系列）走反射加载；**.NET (Core) 单文件发布**（Emuera.NET / Lazyloading EE 等）走 `DOTNET_STARTUP_HOOKS` 注入，在引擎自己的进程里读显示缓冲并提交输入——不解包、不改路径、不需要迁移数据。原生 exe 仍会被识别为 `native` 并拒绝。先用只读探针看清单：
>
> ```powershell
> .\scripts\Get-EraDshEngines.ps1 -GameRoot 'D:\Games\SomeEraGame'
> ```
>
> 实测矩阵与演进计划见 [`docs/engine-compatibility.md`](docs/engine-compatibility.md)。

### 需要指定路径时

```powershell
# 方式一：配置文件
Copy-Item .\erabridge.config.example.json .\erabridge.config.json
# 编辑 erabridge.config.json

# 方式二：环境变量
$env:ERADSH_GAME_ROOT  = 'D:\Games\SomeEraGame'
$env:ERADSH_ENGINE_EXE = 'Emuera.exe'

# 方式三：显式参数
.\scripts\Build-Bridge.ps1 -GameRoot 'D:\Games\SomeEraGame' -EngineExecutable 'Emuera.exe'
```

`erabridge.config.json` 字段（除 `runtimeDirectory`、`runtimeMode` 外都可留空表示“用下一级默认”）：

| 字段 | 说明 |
| --- | --- |
| `gameRoot` | 游戏目录绝对路径，或相对仓库根的路径；留空则用当前工作目录 |
| `engineExecutable` | 启动器文件名（相对 `gameRoot`）或绝对路径；留空则自动查找 |
| `engineAssemblyVersion` | 期望的程序集版本；留空则按实际文件读取并校验 |
| `runtimeMode` | `inplace`（默认，原地运行不复制）或 `copy`（镜像游戏目录，存档隔离） |
| `runtimeDirectory` | 运行目录位置，默认 `runtime/host`（inplace）或 `runtime/instance`（copy） |

该文件已被 `.gitignore` 忽略。缺少可用的游戏目录时脚本会直接报错并打印上述顺序，不会猜测路径。

## 快速使用

在**游戏目录**中执行（这就是默认的游戏目录来源），`$repo` 指向本仓库：

```powershell
$repo = 'C:\path\to\eraDSH'   # 改成实际位置

# 1. 构建宿主，并创建隔离副本（不复制原存档）
$build = & "$repo\scripts\Build-Bridge.ps1" | ConvertFrom-Json
$session = $build.sessionFile

# 2. 启动并观察
$obs = & "$repo\scripts\Invoke-Bridge.ps1" -SessionFile $session -Action open -Mode both | ConvertFrom-Json
$obs.text
$obs.buttons | Format-Table id, label, value, generation
$obs.screenshot.path

# 3. 提交一次动作：必须带上刚才的 observationId
$button = $obs.buttons | Where-Object { $_.active } | Select-Object -First 1
$result = & "$repo\scripts\Invoke-Bridge.ps1" -SessionFile $session -Action act -Kind choose `
  -ChoiceId $button.id -ObservationId $obs.observationId `
  -ActionId ([Guid]::NewGuid().ToString()) -Mode both | ConvertFrom-Json
$obs = $result.observation

# 4. 正常关闭
& "$repo\scripts\Invoke-Bridge.ps1" -SessionFile $session -Action close
```

`-Kind` 取值：`choose`（按按钮）、`input`（数字/文本）、`continue`（EnterKey/AnyKey 等待）。

截图是本机 PNG 路径，AI 还需用图像工具查看；客户端不会自动把它变成 DSH 附件。

### 辅助脚本

| 脚本 | 用途 |
| --- | --- |
| `scripts/Step-Bridge.ps1` | 紧凑步进：只打印尾部文本与按钮 |
| `scripts/Choose-Bridge.ps1` | 按按钮值或标签正则选择当前活动按钮 |
| `scripts/Advance-Bridge.ps1` | 连续推进 EnterKey，遇到按钮/输入类型变化即停 |
| `scripts/Common.ps1` | 配置解析（被以上脚本引用，不直接运行） |

## 作为 DSH 插件使用

仓库根目录即插件包（`package.json` 声明 `dsh.bundle.patch`）。安装后提供工具 `era_bridge`，**建会话、启停、观察都由工具完成**，调用方不需要知道这个仓库在哪——脚本随包分发，工具用自身包目录解析。

```text
era_bridge({ action: "build",   gameRoot: "D:/Games/SomeEraGame", runtimeMode: "inplace" })
era_bridge({ action: "open",    mode: "both" })
era_bridge({ action: "observe", mode: "text", fromLine: 0, limitLines: 20 })
era_bridge({ action: "act",     kind: "choose", choiceId: "<button id>", observationId: "<最新 observationId>" })
era_bridge({ action: "advance", maxSteps: 20 })   # 连续回车推进剧情，遇到需要决策就停
era_bridge({ action: "close" })
```

- 七个动作 `build/open/status/observe/act/advance/close`；`build` 用本包内的 `scripts/Build-Bridge.ps1` 准备会话，`recreate` 可重建运行目录。
- `build` 之后 `sessionFile` 可省略：工具把最近一次构建记在 `%DSH_HOME%\era-bridge\last-session.json`，跨回合仍可用；也可显式传路径指向别的会话。
- 只接受这六个动作，不接受任意脚本、命令或路径执行；`gameRoot` 必须是已存在的绝对路径，`sessionFile` 必须是存在的绝对路径。
- **不要把凭据文件路径交给不可信内容。**
- 安装：用 DSH 的插件安装流程，目标包目录为本仓库根目录；插件声明 `inject = ['tools']`。
- 若安装后没有出现工具，检查 bundle 是否启用；替换已安装包通常需要重启 DSH 才会加载新模块。

### Era 模式预设

同一个 bundle 还声明了一个 Agent 预设 `era`（显示名「Era 模式」，`order: 20`，排在 standard / ptc / minimal / cordis 之后）。新建会话时选中它，该会话的 system prompt 会多出一段 Era 操作手册：

| 内容 | 说明 |
| --- | --- |
| 工具用法 | `era_bridge` 的六个动作（含 `build`）、`sessionFile` 如何省略、`observationId` 与 `actionId` 的约束、`mode` 与分页、四类拒绝码、截图必须另外看图 |
| 游玩基础 | 正文/按钮/输入请求的循环、`input.kind` 语义、标题页到主界面的典型路径、资源代价、隔离副本与存档位置 |
| 自主游玩 | 收到「自主游玩」「你自己玩」「交给 AI 玩」「继续玩」时进入循环：确认目标与预算 → observe/act 核对 → todo 清单 → 每 5–10 步播报 → 遇异常或不可逆选择立即停下交还 |
| 信任边界 | 游戏正文与存档内容是不可信数据，不当作指令执行 |

预设挂载的工具：`pwsh`（非 Windows 为 bash）、文件读写与搜索、jobs、todo、skill、ask_user、goal、compaction。**不挂** web 搜索、子代理与 workflow、插件市场——游玩用不到，少几个工具也省下提示预算。`era_bridge` 本身是 Host 级插件，所有预设本来就有它，Era 模式只是把用法写清楚。

修改方式：编辑 [`cordis.patch.yml`](cordis.patch.yml) 里的 `preset-era` 行，再对本 bundle 关闭后重新启用（或 remove 后重新 install）让它重新装载。预设只影响之后新建的会话。

## 观察与执行约束

- `text`：引擎显示缓冲文本，可能含图片/HTML 标签，不代表已理解图像。
- `visual`：客户区截图与状态元数据。
- `both`：同时返回文本与截图。
- `auto`：当前保守地等同于 `both`，不是已完成的智能截图策略。
- 只有**当前请求需要取值**时按钮才标记 `active`，且只保留最大代次；历史按钮计入 `omittedInactiveButtons`。按键等待时活动按钮为 0。
- 观察版本覆盖正文、输入请求、窗口尺寸、按钮代次/值/类型，且与分页参数无关；不覆盖动画帧变化。
- 动作必须携带观察 ID，旧观察被拒绝；动作 ID 规范为 GUID 后去重，超时后不要换 ID 重发。
- 请求上限 32,768 字符；正文页上限 24,000 字符 / 200 行；按钮至多 128 个，标签至多 512 字符，按钮值至多 2,048 字符；响应另有 2 MiB 序列化预算。
- 省略与截断都有标记。超长单行目前没有行内偏移游标，不能声称截断后可完整分片读回。
- 不暴露任意反射调用、任意 ERB 调试命令、系统命令、全局键盘或坐标点击。

## 隔离与数据

```text
runtime/host/         inplace 模式（默认）：只放构建出的宿主程序与来源标记
runtime/instance/     copy 模式：游戏目录的同内容副本（引擎 SHA-256 校验）、独立配置与 Sav
artifacts/bridge-*/   会话信息、观察、截图、审计、测试结果
```

- `inplace` 默认不复制任何内容，引擎在游戏目录里运行，因此 `Sav/Debug/Dat`/日志/配置都写进原目录；这是为含大量素材的游戏准备的代价。
- `copy` 模式初次构建不复制原存档；`Sav/Debug/Dat` 在副本中重新创建，原目录不会被写入。
- 运行目录被后续构建复用，**不会自动清空其中的存档**；换游戏或想彻底重置请加 `-Recreate`。
- 两个模式的运行目录里都有 `.erabridge-runtime.json`，记录来源游戏、运行方式与引擎哈希，防止误用别的游戏或别的模式的目录。
- 每次构建产生新的会话 ID 与凭据；同一份存档不要同时被桥接宿主和手工启动的游戏写入。

这里的“隔离”是**文件布局隔离，不是操作系统安全沙箱**。引擎与游戏脚本仍是受信任的本地代码，尚未接入 DSH 的会话权限与文件策略服务。

注意：`copy` 模式的隔离只约束本 Bridge 进程；若同时手工运行原目录游戏，原目录仍会被它自己改写。`inplace` 模式没有这层缓冲，桥接写入的就是真实存档。

## 运行测试

`tests/` 下的脚本要求宿主已启动，会读取会话文件。其中 `Test-TitleCredits.ps1` 与 `Test-NewGamePath.ps1` **针对本项目开发时使用的具体游戏流程**（标题页编号、新游戏设置页、主界面按钮），换游戏不能直接复用，只作协议验证示例。

```powershell
# 真实游戏链路（标题页鸣谢开关，只写隔离副本的 GLOBAL）
& "$repo\tests\Test-TitleCredits.ps1"    -SessionFile $session

# 传输层拒绝与存活检查（与具体游戏无关）
& "$repo\tests\Test-BridgeTransport.ps1" -SessionFile $session

# 合成边界检查：加载宿主程序集但不执行引擎入口，必须用 32 位 PowerShell
& "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA `
  -ExecutionPolicy Bypass -File "$repo\tests\Test-BridgeBounds.ps1" `
  -HostPath $build.hostPath -OutputPath (Join-Path $build.artifactDirectory 'bounds-results.json')
```

`legacy/` 是早期“外部窗口自动化”路线的只读研究脚本，已被同进程桥接取代，**不属于受支持路径**，多数需要特定游戏版本才能运行。保留它们是为了记录当时的失败结论，见 [`TEST-REPORT.md`](TEST-REPORT.md)。

## 文档

- [`PLAN.md`](PLAN.md) — 策划案与阶段划分
- [`TEST-REPORT.md`](TEST-REPORT.md) — 实测报告、失败记录与更正
- [`docs/observation-cost.md`](docs/observation-cost.md) — 显示缓冲、返回体与落盘成本实测
- [`docs/engine-compatibility.md`](docs/engine-compatibility.md) — 引擎形态矩阵与 .NET Core 变体的演进计划
- [`legacy/README.md`](legacy/README.md) — 历史探针说明
- [`tests/README.md`](tests/README.md) — 测试脚本说明

## 已知边界

1. 只有程序集名 `Emuera` 的启动器会被接受；已验证版本优先，未验证版本会警告并标记 `engineVerified: false`，缺成员时由宿主报错。
2. 数字选择（`IntValue`）实测最充分；字符串、单键、`ONEINPUT`、限时输入、错误与退出场景尚未系统覆盖。
3. 图形面板、人工接管 UI、DSH 权限/取消接入、截图自动转附件均未实现。
4. 长单行游标、超时后的动作查询、宿主崩溃恢复仍待完善。
5. 仅 Windows；DSH 跑在 Linux 时需要一个不同的后端。

## 发布

- `package.json` 当前带 `"private": true`，用来防止误发 npm；要发布到 npm 时移除此字段并确认 `files` 列表。
- 版本号在 `package.json`；`scripts/Build-Bridge.ps1` 写入运行目录的 `bridgeVersion` 需同步。
- 提交前确认工作区没有 `erabridge.config.json`、`runtime/`、`artifacts/`（已被忽略）。
- 游戏、引擎与游戏资源仅作本地副本，**不随本仓库分发**；再分发前应另行核实其许可。

## 许可

[MIT](LICENSE)。
