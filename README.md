# eraDSH

让 DeepSeek Harness 里的 AI **真正读得懂、也点得动** Emuera / Era 游戏。

不是截图猜坐标，也不模拟键盘：桥接直接读引擎的显示缓冲、真实按钮和输入请求，再把操作交回引擎自己执行。

## 能做什么

| 能力 | 说明 |
| --- | --- |
| 观察 | 正文、当前可点的按钮、输入类型（数字 / 文字 / 回车）、客户区截图 |
| 操作 | 按按钮、填值、回车；每步带观察 ID，旧画面上的动作会被拒绝 |
| 自动推进 | `advance` 连续回车走完剧情，遇到需要决策的地方立刻停下并说明原因 |
| 省上下文 | 过滤加载日志、折叠装饰线、增量观察、行级游标——一屏几十 KB 降到几 KB |
| 多引擎 | .NET Framework 的 Emuera，以及 Emuera.NET / Lazyloading EE 这类 .NET 8 单文件发布 |
| 两种运行方式 | 原地跑（重资产游戏不复制）或隔离副本（不碰真实存档） |
| Era 模式预设 | 附带一个预设：工具用法、Era 游玩常识、以及"自主游玩"协议 |

## 依赖

| 项 | 要求 |
| --- | --- |
| 操作系统 | Windows（Emuera 与桥接都是 Windows/x86 或 x64） |
| 运行时 | .NET Framework 4（Win10/11 自带） |
| 脚本 | Windows PowerShell 5.1 |
| .NET 8 Desktop Runtime | 仅当游戏用 .NET 8 版引擎时需要 |
| .NET SDK | 仅首次构建 .NET 8 适配器时需要，之后复用编好的 DLL |
| DSH | 仅作为插件使用时需要 |

不需要联网，不需要 npm 依赖，不装 SDK 也能跑 .NET Framework 引擎。

## 安装

> `dsh plugin` 必须带 `--profile <名字>`，否则会直接报 `required option '--profile <name>' not specified`。下面的命令以 `web` 为例；名字就是 `DSH_PROFILE` 环境变量的值（在 DSH 会话里执行 `$env:DSH_PROFILE` 可查）。

### 作为 DSH 插件（从 GitHub）

```powershell
dsh plugin --profile web add github:ChaosKo/eraDSH
```

装完后重启 `dsh web`。启动时 DSH 会把声明了 `dsh.bundle` 的新依赖自动登记为 profile 的一个 bundle（也可以在 Web UI 的插件管理里确认它已启用）。

升级到新版本：

```powershell
dsh plugin --profile web remove dsh-era-bridge
dsh plugin --profile web add github:ChaosKo/eraDSH
```

### 本地开发

```powershell
dsh plugin --profile web add C:\path\to\eraDSH
```

链接安装后的脚本改动会被下一次调用读取；[工具入口](<index.js>) 之类的 Host 模块需要重新加载（替换已安装模块时通常需重启 `dsh web`，以安装结果为准）。修改 [Core 钩子源码](<bridge/net8/EraBridgeHook/StartupHook.cs>) 后，下一次 build 会校验源码标记并自动重新编译；不要复用已经启动且加载旧 DLL 的游戏实例。Era 预设的新修订应在新会话验证，旧会话不自动切换。

### 不用插件

`scripts/` 里的 PowerShell 客户端可以独立使用，适合人工调试与脚本化测试。

## 快速上手

先告诉它游戏在哪（一次性，之后可省略）：

```text
era_bridge({ action: "build", gameRoot: "D:/Games/SomeEraGame" })
era_bridge({ action: "open" })                 # 只返回启动回执，不等 IPC 或首屏
era_bridge({ action: "observe" })              # pending=true 时继续有界观测
era_bridge({ action: "observe" })              # 等 canAct=true，再看正文与按钮
era_bridge({ action: "act", kind: "choose", choiceId: "<按钮 id>", observationId: "<可操作观察的 observationId>" })
era_bridge({ action: "advance", maxSteps: 20 })   # 剧情连按回车，遇到决策就停
era_bridge({ action: "close" })
```

`build` 之后 `sessionFile` 可以省略——工具会记住最近一次会话。想看更省流量的读取方式：

```text
era_bridge({ action: "observe", sinceObservationId: "<上次的 observationId>" })  # 只要差异
era_bridge({ action: "observe", sinceCursor: 120 })                             # 从第 120 行往后读
```

`ok=true` 或 `attached=true` 不代表可输入；请等 `canAct=true`。普通重绘不使决策凭证失效，但已消费或已换代的输入请求会被拒绝。超时结果不明确时，不要换 actionId 盲重送。详见 [0.3.1 桥接协议](<docs/bridge-protocol.md>)。

## 配置

默认不需要配置：游戏目录 = 当前工作目录，引擎在其中自动查找，运行方式为原地运行。

| 值 | 解析顺序 |
| --- | --- |
| 游戏目录 | `-GameRoot` → `ERADSH_GAME_ROOT` → `erabridge.config.json` 的 `gameRoot` → 当前工作目录 |
| 引擎 | `-EngineExecutable` → `ERADSH_ENGINE_EXE` → 配置项 → 目录内自动查找 |
| 运行方式 | `-RuntimeMode` → `ERADSH_RUNTIME_MODE` → 配置项 → `inplace`（原地） / `copy`（隔离副本） |

`erabridge.config.example.json` 是模板，复制成 `erabridge.config.json` 后填写；该文件已被 git 忽略。识别规则与支持范围见 [docs/engine-detection.md](docs/engine-detection.md)。

> `inplace` 意味着桥接会话会写真实存档。做实验前先备份 `Sav/`，或显式切到 `-RuntimeMode copy`。

## 文档

| 文件 | 内容 |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | 组件、两种宿主、协议字段、错误码、限额 |
| [docs/engine-detection.md](docs/engine-detection.md) | 引擎分类与选择顺序、运行时要求 |
| [docs/engine-compatibility.md](docs/engine-compatibility.md) | 各引擎实测矩阵、演进计划 |
| [docs/observation-cost.md](docs/observation-cost.md) | 观察成本实测：缓冲增长、返回体、过滤与增量的收益 |
| [TEST-REPORT.md](TEST-REPORT.md) | 实测报告与失败记录 |
| [PLAN.md](PLAN.md) | 策划与阶段验收 |
| [tests/README.md](tests/README.md) | 测试脚本说明 |

## 已知边界

- 只支持 Windows；DSH 跑在 Linux 时需要另一套后端。
- 数字选择实测最充分；字符串、单键、限时输入覆盖较少。
- 长单行目前没有行内字符游标，截断后不能声称可完整拼回。
- 图形面板、人工接管 UI、DSH 权限与取消接入尚未实现。
- 没有为每个 Era 分支维护成员表；换引擎要重新验证。

## 许可

[MIT](LICENSE)。游戏、引擎与素材不随本仓库分发。
