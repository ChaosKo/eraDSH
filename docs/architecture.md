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
| `input` | `kind`、`requestId`、`oneInput`、`needValue`、`timeLimit`、`runningTimer`、`systemInput` |
| `text` | 本窗口正文（已过滤噪声、折叠装饰线） |
| `lines` | 逐行数组，默认不下发，`includeLines` 打开 |
| `buttons` | 全量活动按钮（含代次），超出窗口的行也会收集 |
| `cursor` / `nextCursor` / `cursorBase` / `cursorExpired` | 行游标：末尾位置、下一页位置、已丢弃行数、是否失效 |
| `delta` | 增量结果：`kind`（`append`/`changed`/`none`/`full`）、`fromOrdinal`、`removed`、`lines` |
| `filteredNoiseLines` / `collapsedSeparators` | 本次过滤掉的加载行与折叠的装饰线数量 |
| `screenshot` | `visual`/`both`/`auto` 时的客户区 PNG 路径 |

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
