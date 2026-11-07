# eraDSH 首轮实测报告

日期：2026-11-07  
入口：开发时由 `erabridge.config.json` 指定的 Emuera 启动器（文件中不含机器路径）  
文件版本：1.821.8.0；程序集：Emuera 1.821.5843.38796  
环境：Windows 桌面会话，.NET Framework 4，x86 宿主  
结论：**文本通道、视觉通道、输入通道均已在指定旧版引擎上实测接通；原游戏目录与存档未发生内容变化。**

## 1. 结论摘要

| 能力 | 状态 | 依据 |
| --- | --- | --- |
| 启动指定旧版游戏 | 已实测 | 原版直接启动与同哈希隔离宿主启动均成功 |
| 读取中文正文 | 已实测 | 引擎显示缓冲，标题页全文 932 字符、28 行 |
| 读取真实按钮值 | 已实测 | 标题页按钮值 0、1、8、9，与脚本分支一致 |
| 识别等待输入类型 | 已实测 | `WaitInput` + `IntValue` |
| 提交输入并被引擎消费 | 已实测 | 编号 9 切换鸣谢，28 行→15 行→恢复 28 行 |
| 截图 | 已实测（人工查看） | 1168×657 客户区 PNG，内容与文本一致 |
| 外部窗口文本接口读取正文 | 不可用 | UIA 与 WM_GETTEXT 均无正文 |
| 外部焦点输入 | 未能确认 | `SetForegroundWindow` 返回 false，探针主动停止 |
| 正式 DSH 工具 | 已安装并实测 | `era_bridge` 五动作可用；面板与权限接入未完成 |

## 2. 早期 GUI 探针（P0）

证据目录：[artifacts/p0-20261107-01](<artifacts/p0-20261107-01>)。

### 2.1 成功项

- 指定进程与主窗口被发现：类名 `WindowsForms10.Window.8.app...`，标题 `ERA_MAOU_EX 0.92`。
- 输入控件为 **RichEdit** 而非普通 Edit；`WM_SETTEXT` 写入 `731`、回读一致、恢复为空，未提交。
- `PrintWindow` 截图成功，人工查看确认标题页完整；窗口含大量紫边，客户区裁剪后正确。
- 启动前基线覆盖根目录 `.config`/`.log` 与 `Sav`、`Debug` 递归文件，共 11 个文件（6 个存档）。

### 2.2 失败与限制

| 项目 | 结果 | 解释 |
| --- | --- | --- |
| UIA 文本 | 仅 6 个元素，正文/输入框均无 TextPattern | 自绘界面，标准文本接口拿不到正文 |
| 菜单鼠标消息 | `PostMessage` 点击"文件"后菜单未打开 | 该消息路径未生效，未继续重试 |
| 定向回车 | 输入框清空，但截图未切页 | 见下节更正 |
| 前台切换 | `SetForegroundWindow`=false | 未发送任何字符，符合保护设计 |

### 2.3 重要更正：编号 2 不是说明项

早期把标题页"制作人员及相关说明"误判为编号 2，并据此提交了一次。随后由引擎按钮数据核实：标题页实际按钮为 **0、1、8、9**，编号 2 不存在，游戏对未知输入执行 `RESTART`。因此"输入框清空但未切页"**不能**归因于输入链路失败，准确解释是**未知编号导致标题页重绘**。

该实验未新开游戏、未加载存档；提交前后存档哈希一致。对应记录保留在 `title-info-submit.json` 与 `character-stage.json`，两个历史探针脚本已加显式停用，避免再次误用。

## 3. 结构化 Bridge（P1/P2 原型）

### 3.1 路线

外部窗口自动化无法取得正文后，改为**同进程加载同 SHA-256 引擎副本**：宿主在游戏 UI 线程调用版本专用接口，通过仅限当前用户的命名管道向 DSH 侧提供 AI 友好接口。不做进程注入，不接管用户手工打开的窗口。

### 3.2 实测链路

隔离会话：`bridge-20261107T100121-023b36d6`，引擎副本哈希 `0c47d6fa…89e33`，与原启动器一致。

1. `open` → `ok:true`，`state:WaitInput`，`input.kind:IntValue`，28 行正文，4 个真实按钮。
2. `choose` 编号 9（鸣谢开关）→ 15 行。
3. 旧观察、重复 actionId、等价 GUID 格式、错误输入类型、`@OUTPUT` 调试输入全部被拒绝。
4. 再次 `choose` 9 → 恢复 28 行，输入请求 ID 前进。
5. `close` → 正常退出，未强制结束进程。

截图证据（同一观察内文本与图像）：

- [初始标题页](<artifacts/bridge-20261107T100121-023b36d6/screen-00001.png>)
- [鸣谢收起后](<artifacts/bridge-20261107T100121-023b36d6/screen-00003.png>)

### 3.3 测试结果

| 套件 | 数量 | 结果 | 证据 |
| --- | --- | --- | --- |
| 真实游戏集成 | 13 | 全部通过 | [integration-results.json](<artifacts/bridge-20261107T100121-023b36d6/integration-results.json>) |
| 合成边界 | 11 | 全部通过 | [bounds-results.json](<artifacts/bridge-20261107T100121-023b36d6/bounds-results.json>) |
| 传输拒绝与存活 | 4 | 全部通过 | [transport-results.json](<artifacts/bridge-20261107T100121-023b36d6/transport-results.json>) |

合成测试**不执行游戏入口**：uses 合成正文/按钮对象，验证 24,000 字符预算、128 按钮上限、512 字符标签、超长值不可选、分页版本独立、隐藏按钮元数据参与版本、2 MiB 响应降级、32,768 字符请求硬上限，以及超限后服务仍可继续工作。

传输测试验证：错误凭据的 `close` 被拒绝且宿主存活；33,000 字符请求被拒绝且宿主存活。

### 3.4 数据保护核查

原目录基线在三次时间点复核（`verification-20261107T093744009/095208382/100337748.json`）均为：

- `changes: []`
- `saveContentChanges: 0`
- 无残留目标进程

所有游戏操作只发生在 `eraDSH/runtime/maou-1821/` 隔离副本；该副本的 `Sav/global.sav` 由鸣谢开关测试产生，属于隔离数据，未触碰原存档。

## 4. 能力边界（不应夸大）

- 已验证本游戏从标题到主界面的完整数字选择链路，以及能力显示、页签、返回、地城概况、休息等按钮；字符串、继续键以外的输入类型（ONEINPUT、限时输入、文本输入）仍未实测。
- `auto` 观察当前等同于 `both`，不是智能视觉判断。
- 观察版本不跟踪动画帧；超长单行没有行内偏移游标，不能承诺截断后可完整分片读回。
- `active` 依据“当前请求需要取值 + 最大按钮代次”判断，已覆盖标题、设置、序章、主界面；它不是引擎内部命中测试，按钮被禁用或悬停态等细节仍未读取。`choosable` 另表示按钮值长度合规。
- 动作去重只在宿主生命周期内有效，不承诺跨崩溃 exactly-once；超时后应重新观察，不应换 ID 重发。
- 版本专用反射仅适用于 1.821.5843.38796；换引擎必须重新适配与验证。
- 未安装 DSH 插件，未提供 `era_*` 正式工具或图形面板；当前通过 PowerShell 客户端调用。
- 文件布局隔离不是操作系统安全沙箱；游戏脚本仍是受信任的本地代码。
- 文档 [Emuera EM+EE](https://evilmask.gitlab.io/emuera.em.doc/zh/index.html) 对应更新版本，仅作线索；本地能力以实测为准。

## 5. 复现方式

见 [README.md](README.md)。最小流程（在仓库根目录执行）：

```powershell
$build = & .\scripts\Build-Bridge.ps1 | ConvertFrom-Json
$obs   = & .\scripts\Invoke-Bridge.ps1 -SessionFile $build.sessionFile -Action open -Mode both | ConvertFrom-Json
& .\tests\Test-TitleCredits.ps1    -SessionFile $build.sessionFile
& .\tests\Test-BridgeTransport.ps1 -SessionFile $build.sessionFile
& .\scripts\Invoke-Bridge.ps1 -SessionFile $build.sessionFile -Action close
```

集成测试会切换鸣谢显示并恢复，只写隔离 `global.sav`；运行前须确认标题页鸣谢处于展开状态。

> 证据文件（截图、`*-results.json`、`host-audit.jsonl`）位于本地 `artifacts/`，未随仓库发布：其中含游戏画面与会话凭据。下文链接在本机检出中有效。

## 6. 第二轮：新游戏路径与主界面（2026-11-07 18:15–18:20）

目标：从标题“1 新的猎物”进入新游戏，直到含日期与大量按钮的主界面，并实际点击按钮。

### 6.1 路线与结果

| 步骤 | 观察 | 结果 |
| --- | --- | --- |
| 标题页 | IntValue，4 个活动按钮 | 选择 `1 新的猎物` |
| 新游戏设置 | IntValue，5 个活动按钮（含 `[100] 决定`） | 默认项 + 决定 |
| 序章 | EnterKey，0 个活动按钮 | 连续推进 |
| 角色设定 | IntValue，`[100] 发动魔王真眼` | 选择后进入对峙 |
| 对峙 | 2 个活动按钮（同一行 `[1]`、`[2]`） | 选择 `[2]` 后进入囚禁剧情 |
| 主界面 | IntValue，21 个活动按钮 | 出现 `第0年 1月1日（第1日） 上午 (所持金：10000 pts.)` |

主界面点击实测：

- `[101] 能力显示` → 打开角色列表与页签（编号/状态/所持金/借金）。
- 页签 `[1300] 状态` → 列表显示 HP、气、婚配等状态列。
- `[999] 返回` → 回到主界面。
- `[504] 地城概况` → 打开 1–9 阶层面板。
- `[199] 休息` → 输出“你专心于内政，稍作了休息……（税金+5%）”，日期由 **上午** 变为 **下午**。

自动化路径检查脚本：[tests/Test-NewGamePath.ps1](<tests/Test-NewGamePath.ps1>)，10 项断言全部通过，结果见对应 `path-results.json`。主界面截图（本地证据，未发布）：
`artifacts/bridge-20261107T101926-e44ba377/screen-00002.png`。

### 6.2 本轮发现并修复的缺陷

长流程暴露出显示缓冲会累积历史按钮，若不区分代次，AI 可能点到上一屏的按钮：

1. 首个改法“代次 == `newButtonGeneration`”把标题页按钮全部过滤掉——该字段表示**下一个**代次。
2. 改为“代次 == 列表最大代次”后，EnterKey 页面上旧按钮仍被当作可选。
3. 最终规则：**仅当输入请求需要取值（`needValue`）时，按钮才可选；并只保留最大代次**。其余按钮计入 `omittedInactiveButtons`。

对应字段：`buttons[].active`、`buttonGeneration`、`omittedInactiveButtons`；操作端对非活动按钮返回 `STALE_BUTTON`，对按键等待提交按钮返回 `WRONG_INPUT_KIND`。

回归：合成边界检查增至 12 项（新增 `inactive_buttons_excluded`），真实路径 10 项，全部通过。

### 6.3 原目录写入异常（待用户确认）

本轮结束后的基线复核显示：

| 文件 | 变化 | 时间 |
| --- | --- | --- |
| `Sav\global.sav` | 内容未变，仅 mtime 变化 | 18:12:35 |
| `Sav\save99.sav` | 内容与长度变化（3473 → 3482 字节） | 18:14:04 |

判断依据：

- 上述时间早于本轮所有 Bridge 进程（首个 18:15:23 启动），也早于新游戏操作。
- 隔离副本 `runtime\maou-1821\Sav\save99.sav` 的 mtime 为 18:19:41，与本轮实际运行吻合，大小为 3525 字节，内容与原目录文件不同。（`maou-1821` 是当时的默认目录名，现为可配置的 `runtime/instance`。）
- 因此 Bridge 的存档写入落在隔离副本；原目录改动更可能来自外部直接运行游戏。

**未能还原原 `save99.sav`**：基线只记录了哈希与元数据，未备份存档内容。如需保留该进度，请勿覆盖它；后续测试建议继续使用隔离副本，或先显式备份原 `Sav`。

## 7. 一页结论

**该方案可行且已跑通最小闭环：文本与按钮直接来自引擎，截图作为视觉补充，输入在 UI 线程提交并由下一次观察确认。** 下一步应补齐多输入类型的最小测试游戏、把客户端封装为 DSH 会话工具，再考虑人工接管面板与长程自动游玩；不应先迁移新版引擎。

## 8. 发布准备（2026-11-07 20:45 之后）

目标：把仓库从“绑死这台机器与这款游戏”改成可发布的独立项目，同时不改变已验证能力。

### 8.1 与当前环境解绑

| 项目 | 改造前 | 改造后 |
| --- | --- | --- |
| 游戏目录 | 从 `$PSScriptRoot` 父目录反推，且假定仓库位于游戏目录内 | 参数 → 环境变量 → `erabridge.config.json` → **当前工作目录** |
| 启动器名 | 硬编码 `eramaou_EX 0.92.exe` | 参数 → 环境变量 → 配置项 → **在工作目录中按程序集名 `Emuera` 自动查找**（已验证版本优先） |
| 引擎版本 | 宿主内硬编码 `1.821.5843.38796` | 构建时读取程序集版本写入 `session.json`，宿主按会话值校验 |
| 运行目录 | `runtime\maou-1821` | `runtime/instance`（可配置），并写入 `.erabridge-runtime.json` 来源标记 |
| 插件包名 | `@local/dsh-era-bridge` | `dsh-era-bridge` |
| 插件包根 | `bundle/` 子目录 | 仓库根（否则 npm 打包后 `../scripts` 不存在） |
| 文档链接 | 绝对路径 | 相对链接 |

`erabridge.config.json` 已被 `.gitignore` 忽略，仓库内不再出现任何机器路径。

### 8.2 目录调整

| 目录 | 内容 |
| --- | --- |
| 根目录 | `index.js`、`cordis.patch.yml`、`locale/`、`icon.svg`、`package.json` |
| `scripts/` | Common / Build / Invoke / Step / Choose / Advance |
| `tests/` | 4 个测试脚本（原 `Test-Bridge.ps1` 更名 `Test-TitleCredits.ps1`） |
| `legacy/` | 早期外部窗口探针；两个已停用探针只保留失败声明 |

### 8.3 改造后实测（2026-11-07 20:49 / 20:56）

全部在本机游戏目录上执行，未提交任何游戏动作（只 `open` + `observe` + `close`）。

| 检查 | 结果 |
| --- | --- |
| 17 个 PowerShell 脚本、`index.js`、JSON 语法 | 通过 |
| 无配置、cwd=游戏目录构建 | 通过；`gameRootSource=current-directory`、`engineSource=detected` |
| 引擎自动选择 | 选中 `eramaou_EX 0.92.exe`（1.821.5843.38796），跳过 `Emuera1822.exe`（1.822.6770.41230） |
| 隔离副本内容 | 未复制 `eraDSH` 自身；`Sav/Debug/Dat` 为空；中文目录名正常 |
| 启动 + 观察 | 28 行正文、4 个按钮（0/1/8/9）、`WaitInput` + `IntValue` |
| 正常关闭 | 无残留进程 |
| 原游戏目录写入 | 无；`Sav/` 与 `emuera.log` mtime 仍为 18:12–19:54 |
| cwd=仓库根 | 按预期报错“gameRoot 解析成了插件仓库本身”并打印解析顺序 |
| 显式选未验证引擎 | 警告 + `engineVerified: false`；运行目录引擎不一致时提示 `-Recreate` |
| `-Recreate` 切换引擎 | 通过，随后已切回 1.821 |

**顺带发现**：`Emuera1822.exe`（程序集 1.822.6770.41230）在 1.821 适配器下也能启动并返回 `WaitInput`，说明所依赖的反射成员在该版本仍然存在。但对它**未做任何动作测试**，因此仍按未验证处理，不据此声称兼容。

### 8.4 插件重装与端到端确认

旧包 `@local/dsh-era-bridge` 指向已删除的 `bundle/` 目录，因此按新结构重装：

| 步骤 | 结果 |
| --- | --- |
| `remove_bundle @local/dsh-era-bridge` | `application: applied` |
| `install_bundle`（目标=仓库根） | 包名 `dsh-era-bridge` 0.1.0，`application: applied`，无警告 |
| 工具注册 | `era_bridge` 出现在当前 Agent 的工具列表 |
| 通过工具调用 `open` | 28 行正文、4 个按钮（0/1/8/9），`WaitInput` + `IntValue` |
| 通过工具调用 `close` | `normal_close_scheduled`，无残留进程 |

说明插件已能从新的包根找到 `scripts/Invoke-Bridge.ps1`。

### 8.5 仍未覆盖的部分

- `tests/` 中的真实链路断言已在目录迁移后复测通过（见 8.6）；它们只对当前游戏版本的菜单成立，换游戏不适用。
- 图形面板、人工接管、DSH 权限接入仍属下一阶段，未随本次发布准备改变。
- 因此本节声明的是“代码、包结构、引擎选择与两条游戏链路在迁移后的目录上可用”，不声明兼容其他游戏或其他引擎版本。

### 8.6 目录迁移后的复测（2026-11-07 21:07–21:12）

仓库从 `EraMaou0.92+EX2.2\eraDSH` 整体迁移到 `Eras\eraDSH`（插件开发目录独立于游戏目录）。迁移后工作目录就是仓库本身，不再是游戏目录，因此构建改用显式 `-GameRoot`：

| 检查 | 结果 |
| --- | --- |
| 仓库位置与状态 | `.git` 已在 `Eras\eraDSH`；`main` 与 `origin/main` 一致，32 个文件，工作区干净 |
| 插件注册 | profile 中 `dsh-era-bridge` 为 `link:C:/Data/Game/Eras/eraDSH`，`era_bridge` 工具正常加载 |
| 构建（cwd=仓库根 + `-GameRoot`） | 通过；`gameRootSource=parameter`、`engineSource=detected`，引擎 SHA-256 与迁移前一致 |
| 工具级 open → close | 28 行正文、4 个按钮（0/1/8/9）、`WaitInput` + `IntValue`；`normal_close_scheduled`，无残留进程 |
| `Test-TitleCredits.ps1` | 13 项断言全部通过：鸣谢展开/收起、重复动作与等价 GUID 拒绝、旧观察拒绝、分页、输入类型守卫、调试输入拒绝、恢复 |
| `Test-NewGamePath.ps1` | 10 项断言全部通过；主界面 21 个活动按钮，日期行正常 |
| 原游戏目录 | 6 个存档与 `emuera.log`、`emuera.config` 的 mtime 均未变 |

迁移后遗留 `runtime/maou-1821/`：旧的默认目录名对应的隔离副本，含上一轮产生的隔离存档。它已被忽略、不再被新构建引用，需要时可删除。

环境侧记录：迁移完成后 DSH 沙箱无法在新目录上建立写授权（缺少取得所有权权限），该会话内的工具调用全部失败，与仓库内容无关；修复目录权限后原操作恢复正常，本节复测即在其后完成。

### 8.7 Era 模式预设（2026-11-07 21:14–21:16）

bundle patch 从一行扩到两行：除 `dsh-era-bridge` 外，新增一条 `@deepseek-ai/dsh-agent-preset` 声明（`config.id = era`，显示名「Era 模式」，`order: 20`），persona 用 `@deepseek-ai/dsh-persona` 的 `prefix` 写入操作手册、游玩基础与自主游玩协议。

| 检查 | 结果 |
| --- | --- |
| patch 语法与结构 | 两行；preset 声明 15 个子插件、persona 1964 字符、2 个 `!!js` 平台开关，结构检查无问题 |
| Loader 装载 | `Config` 出现 `include:preset-era`（schema 投影正常）；`list_plugins` 显示 `enabled: true`、`fiberPhase: active`，无诊断 |
| 生效方式 | 对 bundle 关闭再启用后即重新读取 patch；`install_bundle` 对已装 link 包返回 `already-installed`，不能靠它刷新 |
| 会话组成 | 任务卡以 `mode: era` 运行，会话记录 `agentPreset: "era"`，`system/message` 含完整 persona 文本 |
| 实际回答 | 自检会话正确自述为 Era 模式代理，并说出 `era_bridge` 的五个动作与「先拿到 `sessionFile`、`act` 需 `observationId`」这一前提 |

该预设挂载的工具集不含 web 搜索、子代理/workflow 与插件市场；`era_bridge` 属 Host 级注册，因此与预设选择无关。

### 8.8 原地运行模式（2026-11-07 21:28–21:31）

重资产游戏（含大量图片/音频）不应为了观察而整目录复制。新增 `runtimeMode`：

| 方式 | 行为 | 原目录写入 |
| --- | --- | --- |
| `inplace`（默认） | 不复制，引擎从游戏目录加载并以其为工作目录 | `Sav`/`Debug`/`Dat`/日志/配置 |
| `copy` | 镜像到 `runtime/instance`，引擎从副本加载 | 无 |

默认值按使用者的决定在验证之后改为 `inplace`；下表的验证先测的 inplace、再测 copy 回归，两者互不影响。

验证在一份临时副本（22.1 MB）上完成，避免触碰真实存档：

| 检查 | 结果 |
| --- | --- |
| inplace 构建 | `runtimeMode=inplace`，`enginePath` 指向游戏目录，宿主在 `runtime/host/`，未创建任何副本 |
| inplace 会话 | open 读到 28 行、4 个按钮；act 成功且观察变化；close 正常，无残留进程 |
| 宿主自述 | `status` 返回 `runtimeMode=inplace`、`gameDir=<游戏目录>` |
| 原目录写入 | 副本中的 `Sav/global.sav` mtime 变化（GLOBAL 由游戏自己写）；`emuera.log`、`emuera.config` 未变 |
| copy 回归 | 另一运行目录构建成功，`Sav/Debug/Dat` 为空、副本 22.1 MB、源目录存档未被写入 |
| 宿主位置 | inplace 下游戏目录未新增任何桥接文件，宿主仍在仓库 `runtime/host/` |

清理：临时副本与 `runtime/verify-copy` 已删除，真实游戏目录未被本项验证接触。

### 8.9 自包含的建会话动作（2026-11-07 21:41–21:43）

问题：`era_bridge` 的五个动作都需要现成的 `session.json`，而生成它只能靠手跑 `Build-Bridge.ps1`，所以 Era 模式下的 Agent 会去工作区里找 `eraDSH` 仓库。插件的脚本本来就随包分发，工具也已经用自身包目录解析 `scripts/Invoke-Bridge.ps1`，缺的只是把构建这一步也交给工具。

| 检查 | 结果 |
| --- | --- |
| 新增 `build` 动作 | 以插件包目录解析 `scripts/Build-Bridge.ps1`，参数为 `gameRoot` / `engineExecutable` / `runtimeMode` / `recreate` |
| 会话记忆 | 构建结果写入 `%DSH_HOME%\era-bridge\last-session.json`；`DSH_HOME` 未导出时回退到 `%USERPROFILE%\.dsh` |
| 省略 `sessionFile` | build 之后 open / observe / act / close 直接可用，实测 open 返回 28 行 + 4 按钮 |
| 错误提示 | 没有会话时提示先调用 `build`，不再要求调用方去找仓库 |
| persona | 改为「不要在工作区里找 eraDSH 仓库，也不要自己拼脚本路径；不知道游戏目录就问用户」 |
| 包内状态 | 首次验证时状态文件误落到仓库 `.era-bridge-state/`，已改为回退到用户主目录，并把该目录加入忽略规则 |

验证环境：22 MB 临时副本（`_tool-test`），验证后删除；`runtime/host` 一并清理，下次构建重新生成。版本号升至 0.2.0，运行目录标记同步。

### 8.10 多引擎兼容性（2026-11-07 21:50–22:10）

对四个 Era 目录做只读检查（不启动游戏），结果见 [docs/engine-compatibility.md](docs/engine-compatibility.md)。核心结论：兼容性瓶颈是**运行时形态**，不是程序集名。

| 目录 | 引擎 | 形态 | 结果 |
| --- | --- | --- | --- |
| `EraMaou0.92+EX2.2` | `eramaou_EX 0.92.exe` | .NET Framework x86 1.821 | 可驱动（已验证） |
| `eraOCG2_20261009` | `Emuera.NET 1824+…EEv52fix.exe` | .NET Core 单文件 x64 | 不支持 |
| `erafl` | `Emuera.exe` | .NET Core 单文件 x64 | 不支持 |
| `eratw-sub-modding-main` | 四个 EE/NAudio 变体 | .NET Core 单文件 x64/x86 | 不支持 |

判定手段：PE 的 CLR 头、`AssemblyName.GetAssemblyName`、`hostfxr` / `.NETCoreApp` 字符串、`BSJB` 元数据计数。

修复：

- 检测从"找不到引擎"改为四类分类（framework / core / native / managed-other），并在没有可用引擎时列出每个 exe 的形态与原因；
- 显式指定 .NET Core 引擎时给出"无法被反射加载"的说明，而不是"不是 .NET 程序集"；
- 新增只读探针 `scripts/Get-EraDshEngines.ps1`。

未解决（列入计划）：这批变体的托管程序集未压缩地嵌在 exe 里（`BSJB` 16–25 次、无 `System.Private.CoreLib`），理论上可解包；但成员表已变（`Emuera.UI` 命名空间、无 `GetDisplayStrings`），需要新宿主 + 新成员映射，见计划 P2。


### 8.11 .NET 8 引擎接入（2026-11-07 22:05–22:22）

目标引擎 `eraOCG2_20261009`（`Emuera.NET 1824+v22+EMv18+EEv52fix.exe`，bundle 6.0 / net8.0 / x64）。

| 检查 | 结果 |
| --- | --- |
| 清单解析 | 20 个条目、未压缩、`Microsoft.NETCore.App 8.0.0` + `WindowsDesktop 8.0.0` |
| 注入 | `DOTNET_STARTUP_HOOKS` 成功进入引擎进程（.NET 8.0.21） |
| 观察 | `WaitInput`、31 行、`EnterKey`，正文与按钮均为真实数据 |
| 输入 | `act continue` 成功且观察变化 |
| 关闭 | 进程正常退出 |
| 数据 | 测试前移出 `Sav`/`emuera.log`，测试后还原 |

曾失败并已修正的三处：`BeginInvoke` 排队超时（改由 Idle 刷新缓存）、按最新画面校验导致所有动作被判 `STALE_OBSERVATION`（改为接受最近 8 个已交付观察）、缓存早期窗口句柄（改为每次重新解析）。

注意：`IsHungAppWindow` 在这个引擎上不可靠——不挂钩子直接启动同样被报为“未响应”，因此没有把它当作判据。

### 8.12 三引擎兼容性实测（2026-11-07 22:43–22:52）

| 启动器 | 形态 | 构建 | 观察 | 动作 | 关闭 |
| --- | --- | --- | --- | --- | --- |
| `erafl\Emuera.exe` | net8 x64 bundle | 通过 | `WaitInput` 1961 行 / 5 按钮 | 通过 | 通过 |
| `eratw\…NAudio-x86.exe` | net8 x86 bundle | 通过 | 51 行 / 3 按钮 | 通过 | 通过 |
| `eratw\…NAudio-x64.exe` | net8 x64 bundle | 通过 | 51 行 / 3 按钮 | 通过 | 通过 |
| `eratw\Emuera.NET…EEv53 NAudio.exe` | net8 x64 bundle | 通过 | 48 行 / 3 按钮 | 通过 | 通过 |
| `EraMaou\eramaou_EX 0.92.exe` | .NET Framework x86 | 通过 | 28 行 / 4 按钮 | 通过 | 通过 |

过程中发现一处自身回归：为加入 `engineKind` 修改 `Common.ps1` 时误删了结果对象的 `EngineAssemblyVersion`，.NET Framework 宿主因“会话未声明引擎版本”启动失败、管道超时。补回字段后 Maou 复测通过。真实存档按既有流程移出并还原。

### 8.13 观察瘦身与加载噪声过滤（2026-11-07 23:00）

| 改动 | 结果 |
| --- | --- |
| 加载噪声过滤 | `erafl` 缓冲开头 200 行加载日志中剔除 199 行（7 284 → 24 字符） |
| 分隔线折叠 | `EraMaou` 折叠为 `- ×123`，`eraOCG2` 折叠为 `— ×156` |
| `lines[]` 默认不下发 | `erafl` 默认观察 4.7 KB，`includeLines` 时 11.9 KB（省约 60%） |
| `act` 精简 | 观察体约 4.3 KB，不再携带整屏逐行数组 |
| 工具层紧凑渲染 | 去掉 pretty-print（约 1.28×） |
| 回归 | `EraMaou` 28 行/4 按钮、`eraOCG2` 36 行/3 按钮，动作均正常 |

过滤只影响呈现：`observationId` 仍按完整缓冲区计算，`noise=raw` 可取回原始文本。第一版规则漏掉了中文加载行（`ABL.CSV读取中・・・`），已补入判定。

### 8.14 增量观察（2026-11-07 23:20）

`observe` 新增 `sinceObservationId`：返回 `delta.kind`（`append`/`changed`/`none`/`full`）加差异行，而不是整屏。两个宿主都实现，比较的是过滤后的实际行序列，`observationId` 仍按完整缓冲区计算。

| 游戏 | 全文 | 增量（动作后） | 增量（同一画面） |
| --- | --- | --- | --- |
| `erafl` | 2 811 B | 2 165 B `append` 8 行 | 1 727 B `none` |
| `EraMaou` | 2 193 B | 2 144 B `changed` 7 行 | 1 586 B `none` |
| `eraOCG2` | 1 913 B | 1 478 B `append` 5 行 | 1 266 B `none` |

基线未知时返回 `full` + `sinceMissing`，不会让调用方卡在“不知道前面是什么”的状态。

### 8.15 行级游标与 artifacts 保留（2026-11-07 23:40）

游标：观察新增 `cursor`（缓冲末尾）、`nextCursor`（本窗口之后）、`cursorBase`（引擎已丢弃行数）、`cursorExpired`；`observe` 接受 `sinceCursor`。绝对行号 = `DeletedLines` + 缓冲下标，裁剪后仍有意义。

| 检查 | 结果 |
| --- | --- |
| 连续翻页（`erafl` 1961 行） | from=0 → 60 行 → `nextCursor=60` → 60 行 → `nextCursor=120`，无重叠 |
| 越界游标 | `sinceCursor=0` 时 `cursorExpired=false`（引擎未裁剪） |
| 过滤与游标 | 过滤模式从头翻页会得到空页（前 200 行全是加载日志），`noise=raw` 可见原文 |

保留策略：`scripts/Prune-Artifacts.ps1`，默认 20 会话 / 100 MB，从最旧删起，当前会话永不删除；构建结束自动调用，失败只告警。临时目录实测 6 个会话、`-Keep 3`、当前=最旧 → 删除 2 个、保留 4 个。
