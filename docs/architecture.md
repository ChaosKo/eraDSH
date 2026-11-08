# 架构与协议

面向维护者：数据怎么流、两种宿主怎么分工、协议字段和错误码是什么。面向使用者的内容在 [README](../README.md)。

## 1. 组件

```text
era_bridge 工具（index.js，Host 插件）
   │  解析参数、记住会话、紧凑渲染
   ▼
scripts/Invoke-Bridge.ps1（PowerShell 客户端）
   │  命名管道 + 会话凭据 + 服务端 PID 校验
   ▼
宿主（两种，按引擎形态选择）
   ├─ EraBridgeHost.exe      .NET Framework x86，反射加载引擎
   └─ EraBridgeHook.dll      .NET 8 启动钩子，注入引擎自身进程
   ▼
Emuera 引擎 → 显示缓冲 / 按钮 / 输入请求 / 客户区
```

`scripts/Build-Bridge.ps1` 负责建会话：识别引擎、生成凭据、写 `session.json`、必要时编译宿主。工具不直接执行任意命令，只调用这两个脚本。

## 2. 两种宿主

| | `EraBridgeHost.exe` | `EraBridgeHook.dll` |
| --- | --- | --- |
| 目标引擎 | .NET Framework Emuera（1.8xx） | .NET 8 单文件 bundle（Emuera.NET / Lazyloading EE） |
| 加载方式 | `Assembly.LoadFrom` + 反射 | `DOTNET_STARTUP_HOOKS` 注入引擎进程 |
| 启动参数 | 会话 JSON 路径 | 环境变量 `ERA_BRIDGE_SETTINGS` |
| 引擎路径语义 | 由宿主设定 `Sys.ExeDir` | 保持引擎自身，不做任何改写 |
| 取文本 | `GetDisplayStrings` | `DisplayLineList`（属性） |
| 提交输入 | `PressEnterKey(bool,string,bool)` | 同名同签名 |

两者说同一套管道协议，所以 `Invoke-Bridge.ps1` 与 `era_bridge` 不需要区分引擎。

## 3. 动作

| 动作 | 作用 |
| --- | --- |
| `build` | 建会话（工具层动作，不经过管道） |
| `open` | 启动实例并返回首个观察 |
| `observe` | 读取当前状态，支持窗口、游标、增量 |
| `act` | 提交一次输入（`choose`/`input`/`continue`） |
| `advance` | 连续回车推进剧情，遇到决策即停 |
| `status` | 存活与身份 |
| `close` | 正常关闭 |

## 4. 观察字段

| 字段 | 说明 |
| --- | --- |
| `observationId` | 会话 ID + 完整缓冲区/输入/按钮的哈希；动作据此做过期校验 |
| `state` | `Initializing` / `Running` / `WaitInput` / `Error` / `Quit` / `Sleep` |
| `input` | `kind`、`requestId`、`oneInput`、`needValue`、`timeLimit`、`runningTimer`、`systemInput`；`render=compact` 只留 `kind` 与为真的标志 |
| `text` | 本窗口正文（已过滤噪声、折叠装饰线；`render=compact` 时进一步折叠布局） |
| `lines` | 逐行数组，默认不下发，`includeLines` 打开 |
| `buttons` | 全量活动按钮（含代次），超出窗口的行也会收集；`render=compact` 只给决策需要的字段 |
| `cursor` / `nextCursor` / `cursorBase` / `cursorExpired` | 行游标：末尾位置、下一页位置、已丢弃行数、是否失效 |
| `delta` | 增量结果：`kind`（`append`/`changed`/`none`/`full`）、`fromOrdinal`、`removed`、`lines`；`compact` 下 `act`/`advance` 也会回增量，此时 `text` 为 null |
| `filteredNoiseLines` / `collapsedSeparators` | 本次过滤掉的加载行与折叠的装饰线数量 |
| `collapsedLayoutLines` / `mergedRepeatedLines` / `compressedRuns` | `render=compact` 折叠的纯装饰行、合并的重复邻行、压缩的超长字符/空格串数量 |
| `screenshot` | `visual`/`both`/`auto` 时的客户区 PNG 路径 |

`act` 与 `advance` 在 `compact` 下会拿"你操作的那一屏"（`advance` 取第一次回车之前的那一屏）做增量：累计缓冲的游戏（EraFL 这类不清屏的实现）每步会重发旧屏，因此只回新增/改写行。增量行数达到窗口的 60% 时自动回退成整屏文本，因为逐行 JSON 那时更贵；`delta.reason` 会写明回退原因。

`render` 只改变呈现：`compact`（工具层默认）把纯装饰行换成 `▦(宽度)`、把连续相同行折成 `行 ×N`、把长串同字符压成 `字符×N`，并省掉诊断字段；`full` 原样返回。两者共用同一个 `observationId`，动作授权不受影响。按钮的 `generation`/`value` 只在响应里被裁剪，投递与校验仍用完整按钮，因此不能在响应体之外依赖这些字段。

`observationId` 覆盖完整缓冲区，与分页、过滤、增量参数无关——这些只改变呈现，不改变"这是哪一屏"。

## 5. 错误码

| 码 | 含义 |
| --- | --- |
| `STALE_OBSERVATION` | 动作基于的观察已不是当前画面 |
| `STALE_BUTTON` | 按钮属于更早的代次 |
| `DUPLICATE_ACTION` | 同一 `actionId` 重复提交 |
| `WRONG_INPUT_KIND` | 当前不是这种输入类型 |
| `INTEGER_REQUIRED` | 当前请求需要整数 |
| `INVALID_INPUT` | 值不合法（换行、超长、`@` 调试命令） |
| `NOT_WAITING_INPUT` | 引擎不在等待输入 |
| `NOT_READY` | 窗口/控制台尚未就绪 |
| `OUTCOME_UNCERTAIN` | UI 操作超时，**不要**换 ID 重发 |
| `CHOICE_NOT_FOUND` / `CHOICE_VALUE_TOO_LONG` | 按钮不存在或值过长 |

## 6. 限额

- 请求 ≤ 32 768 字符；响应 ≤ 2 MiB。
- 正文窗口 ≤ 200 行 / 24 000 字符；按钮 ≤ 128 个，标签 ≤ 512 字符，值 ≤ 2 048 字符。
- 增量差异超过 200 行时退回全文。
- 会话内动作记录上限 10 000 条。

## 7. 运行方式

| 方式 | 行为 |
| --- | --- |
| `inplace`（默认） | 引擎在游戏目录原地运行，不复制；游戏自己的 `Sav`/`Debug`/`Dat`/日志写在原目录 |
| `copy` | 镜像到运行目录，隔离存档 |

`copy` 需要整目录拷贝，重资产游戏不划算；`inplace` 的代价是桥接会话会写真实存档。

## 8. 并发与安全

- 每次构建产生新会话 ID 与凭据；管道只接受当前用户 ACL，校验服务端 PID 后才发送凭据。
- 同一运行目录同时只允许一个宿主；`open` 会检查已存在实例。
- 动作串行处理，`actionId` 去重；超时返回 `OUTCOME_UNCERTAIN`，不自动重放。
- 游戏正文与存档内容属于不可信数据，不作为指令执行。

## 9. 就绪判定

`observe`/`open` 只在引擎能给出画面时才算就绪：窗口与控制台已附加、`state` 不是 `Initializing`/`Running`/`Sleep`、并且有行数。

**不把"定时器已停"当成就绪条件**：Era 引擎的重绘/动画定时器不会停，按它判断会让 `open` 一直等到窗口被关闭——那是一处真实故障，已修。

等待有上限（默认 15 秒，可用 `ERA_BRIDGE_READY_MS` 覆盖）。超时不报错、也不继续等，而是返回当前已知画面并标 `pending: true` 与 `observationAgeMs`；调用方看到 `pending` 再 observe 一次即可。这样任何情况下都不会把调用方挂住。

## 10. open 与 observe 的分工

`open` **只负责启动**：起进程、写实例记录、返回 `{ launched: true, pending: true, state: "starting" }`，不等待任何画面。启动快慢只影响随后的 `observe` 拿到什么，不会把调用方堵在 open 里。

`observe`负责采样：先看闲置缓存，未就绪时主动向 UI 线程要一次新快照（3 秒上限），最多等 20 秒；仍未就绪就返回当前画面并标 `pending: true`。调用方看到 `pending` 再采样一次即可。

这样设计的原因：引擎启动时间差异很大（`eraOCG2` 约 12 秒，`eraFL` 约 6 秒），而任何"等画面"的逻辑只要在一台机器上不成立，就会变成调用级阻塞。
