# 桥接生命周期与输入凭证（0.3.1）

## 正确调用顺序

`build → open → observe（直到 canAct=true）→ act/advance → observe → close`

- `open` 只确认进程创建成功，返回 `launched=true/pending=true`，不连接 IPC、不等首屏。Windows 启动器不继承工具的标准输入输出句柄，游戏生命周期不再拖住 PowerShell/Node 的输出管道。
- `phase` 区分 `starting/loading/running/waiting-input/exited/failed`；`attached` 只表示附着到引擎，不表示能输入。
- `observe.pending=true` 是普通加载/忙碌回执，不应触发重复 open。只有 `canAct=true` 的新鲜观察可以授权输入。窗口出现、正文出现、`ok=true` 或缓存命中均不能代替此判断。
- `status` 用于确认存活与诊断，不能代替可操作观察；Core 的状态回执明确要求重新 observe。
- `close` 请求正常关窗；若进程已退出，客户端返回 `phase=exited/pending=false`，不再报启动中。

## 展示版本与决策版本分离

`observationId` 绑定已交付的展示观察；`inputEpoch` 绑定当前决策请求。字体、颜色、窗口尺寸、正文布局变化不应使未消费的真实决策凭证失效。

动作在同一 UI 回调里读取真实引擎状态、输入请求和有效按钮，再比对已交付观察对应的输入代次。历史观察只证明模型见过什么，不提供当前授权；缓存仅作非授权诊断。真实请求/选择语义改变、有效按钮代次改变或成功消费输入后，旧凭证失效。按钮有效代次取引擎 `lastButtonGeneration`，不从缓冲中残留的最大代次猜测。

Framework 对已证实的 eraMeiQ `TINPUT 50` 动画循环单独识别连续决策：真实执行行、定时默认值、完整纯文本、有效选项和权威按钮代次都相同，且尚未提交时，50 ms 子请求的对象/ID 重建不会使菜单凭证失效。提交或任一决策语义变化立即终止连续性；未满足这些条件的请求仍严格换代。

合法的限时值输入与动画用的定时键等待不同。值/按钮请求可显式提交，定时或系统键等待不能被 `advance` 自动跳过。内建标题菜单也可能标记 `systemInput=true`，不能一概把它当加载状态。

## 等待与不确定结果

- UI 工作共享单次调用的截止时间；尚未执行的过期工作会取消，不会在模型离开后迟到发键。
- 已进入引擎内部的代码无法安全强制中断。若它运行超过预算，动作返回 `OUTCOME_UNCERTAIN`，不能当作未输入。
- `actionId` 在工具调用端生成并在异常中保留。结果不明时先观察，不能换新 ID 盲重送。
- 展示分页、过滤、截断和增量只影响返回投影；不能决定动作授权，也不能把每帧动画当成决策进展。

## 端到端验证

[真实引擎回归脚本](<../tests/engine-regression.mjs>) 使用独立完整副本，不复制原 `Sav/Dat/Debug`；验证前后对原存档、配置、profile 与日志做哈希对比。它覆盖进程仍存活时 open 返回、加载期不可输入、真实菜单输入、换请求拒旧凭证以及正常关窗。机器上的游戏目录映射在脚本顶部，换电脑时按实际路径调整。

```text
node tests/engine-regression.mjs ocg
node tests/engine-regression.mjs tohok
node tests/engine-regression.mjs meiq
```

实际通过范围与尚未覆盖的引擎分支见 [测试报告](<../TEST-REPORT.md>)；同一适配路径不等于所有游戏内容均已验证。
