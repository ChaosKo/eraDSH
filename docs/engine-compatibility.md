# 引擎兼容性矩阵与演进计划

日期：2026-11-07  
适配器基线：`EraBridgeHost.exe`（.NET Framework 4，x86），通过反射调用引擎内部成员

这份文档记录四款 Era 游戏的实际引擎形态、当前适配器能做到哪一步、以及要支持更多变体需要改什么。所有结论都来自对文件的只读检查（不启动游戏），检查脚本见 [`scripts/Get-EraDshEngines.ps1`](../scripts/Get-EraDshEngines.ps1)。

## 1. 结论先行

| 引擎形态 | 能否用本桥接驱动 | 原因 |
| --- | --- | --- |
| .NET Framework Emuera（1.8xx 系列，x86） | 能 | 可被 `Assembly.LoadFrom` 加载，反射成员与适配器一致 |
| .NET (Core) 单文件发布（Emuera.NET / Lazyloading EE 等） | 不能 | 可执行文件里没有可加载的 CLR 头，托管程序集被打包在 exe 内部 |
| 原生可执行文件（更新器、外部工具） | 不能 | 不是 .NET 程序集 |

**检测的局限不在"程序集名必须叫 Emuera"这一条规则本身，而在于：这个适配器的加载方式决定了它只可能支持 .NET Framework 血统。** 一个程序集名正确、但用 .NET 8 发布的 Emuera 变体，即使名字对也加载不了。

## 2. 实测矩阵（2026-11-07）

| 目录 | 可执行文件 | 大小 | 架构 | 形态 | 当前结果 |
| --- | --- | --- | --- | --- | --- |
| `EraMaou0.92+EX2.2` | `eramaou_EX 0.92.exe` | 0.6 MB | x86 | .NET Framework 1.821.5843.38796 | **可驱动**（已验证） |
| 同上 | `Emuera1822.exe` | 0.6 MB | x86 | .NET Framework 1.822.6770.41230 | 可加载，未验证版本 |
| `eraOCG2_20261009` | `Emuera.NET 1824+v22+EMv18+EEv52fix.exe` | 2.9 MB | x64 | .NET Core 单文件 | 不支持 |
| `erafl` | `Emuera.exe` | 3.3 MB | x64 | .NET Core 单文件 | 不支持 |
| `erafl` | `update.exe` | 3.1 MB | x64 | 原生更新器 | 与引擎无关 |
| `eratw-sub-modding-main` | `Emuera.NET 1824+v22+EMv18+EEv53 NAudio.exe` | 3.3 MB | x64 | .NET Core 单文件 | 不支持 |
| 同上 | `LazyloadingV2.4-Snake-EEv53-NAudio-x64/x86.exe` | 3.5 / 3.4 MB | x64 / x86 | .NET Core 单文件 | 不支持 |
| 同上 | `LazyLoadingV2.exe` | 6.5 MB | x64 | .NET Core 单文件 | 不支持 |

判定依据（只读）：

| 检查 | Maou | 三个 .NET Core 变体 |
| --- | --- | --- |
| PE 的 CLR 头（COM descriptor） | 有 | **无** |
| `AssemblyName.GetAssemblyName` | 成功，`Emuera` 1.821 | 抛异常 |
| 文件内 `hostfxr` / `.NETCoreApp` 标记 | 无 | 有 |
| 文件内 `BSJB`（托管元数据）出现次数 | — | 16–25 次 |
| 文件内 `System.Private.CoreLib` | — | 0（框架依赖，不自带运行时） |
| `MinorShift.Emuera.MainWindow` 字符串 | 有 | **无**，改用 `Emuera.UI` |
| `GetDisplayStrings` | 有 | **无** |
| `displayLineList` / `inputReq` / `newButtonGeneration` / `PressEnterKey` | 有 | 有 |

## 3. 这意味着什么

1. **单文件打包是硬阻塞。** .NET Core 的单文件发布把托管程序集塞进 exe，exe 本身是原生 apphost，没有 CLR 头。`Assembly.LoadFrom` 对它只会抛 `BadImageFormatException`；我们的宿主连"加载"这一步都做不到，谈不上反射。
2. **即使能解包，成员表也变了。** 这批变体是 Emuera.NET 血统：窗口类型在 `Emuera.UI` 命名空间下，`GetDisplayStrings` 不存在；但 `displayLineList`、`inputReq`、`newButtonGeneration`、`PressEnterKey` 仍在。所以不是"换个宿主就能跑"，还需要一份新的成员映射。
3. **好消息是程序集没压缩。** `BSJB` 在各 exe 中出现 16–25 次，说明托管程序集以原始 PE 形式嵌在文件里，理论上可提取；且它们不携带运行时（无 `System.Private.CoreLib`），说明是框架依赖发布，机器上已有 .NET 运行时即可。

## 4. 当前行为（本轮修好的部分）

以前：自动检测说 "No Emuera engine found"，显式指定说 "not a .NET assembly"。两者都会让人以为路径写错了或引擎坏了。

现在：

- 检测把所有顶层 exe 分为 `framework` / `core` / `native` / `managed-other` 四类，并给出原因；
- 没有可用引擎时列出每个可执行文件的形态，而不是只说"没找到"；
- 显式指定 .NET Core 引擎时报"这是 .NET Core 单文件构建，无法被反射加载"，并指向本文档；
- 新增只读探针，先看清单再决定：`.\scripts\Get-EraDshEngines.ps1 -GameRoot <游戏目录>`。

## 5. 修复计划

### P0 已完成（本轮）

- 引擎分类与可操作报错；
- `Get-EraDshEngines.ps1` 只读探针；
- 本文档与 README 的兼容性说明。

### P1 兼容性表达（小，1 次改动）

- 构建结果与会话里带上 `engineFamily: framework|core`，让下游（预设、面板）能显示"这个引擎不受支持"而不是等失败；
- `Get-EraDshEngines.ps1` 增加 `-Json`，便于脚本批量体检一组游戏目录。

### P2 支持 .NET Core 变体（大，需要新宿主）

目标是让 Emuera.NET / Lazyloading 系列也能被观察和操作。分三步，每步都能独立验证：

1. **解包**：从单文件 exe 中提取内嵌程序集。程序集未压缩，可行路径是解析 bundle 清单或扫描 PE 头；优先用官方 `Microsoft.NET.HostModel` 的 bundle 读取实现，避免自研格式解析。产出：一个临时目录里的 `Emuera.dll` 等。
2. **新宿主**：`EraBridgeHost.net8`（.NET 8、x86/x64 各自一份），加载提取出的程序集并调用其入口。这一步与现有宿主结构相同，只是运行时换了。
3. **成员映射**：按 `Emuera.UI` 的命名空间探测窗口类型，重新定位 `console` 缓冲区、输入请求与按钮代次。已知 `displayLineList` / `inputReq` / `newButtonGeneration` / `PressEnterKey` 存活，缺口主要在"取显示文本"的方法名。

风险与前提：

- 解包依赖 bundle 格式，压缩过的发布（`EnableCompressionInSingleFile`）会额外增加一步；
- 各变体（v22 / EMv18 / EEv52 / EEv53）是否同源需逐个核对，可能要为每个分支维护成员表；
- 必须逐个变体做冒烟测试，不能声明"支持 Emuera.NET"。

### P3 备选：外部窗口路线（不推荐）

早期 `legacy/` 探针已经证明：Emuera 自绘正文，UIA 与窗口文本都取不到内容，前台切换也被系统拒绝。对这批 .NET 变体大概率同样成立，且即使能用也只能拿到截图，无法可靠提交输入。不建议作为主线。

## 6. 如何复现本文档的结论

```powershell
# 1. 分类一个游戏目录里的所有可执行文件（只读，不启动游戏）
.\scripts\Get-EraDshEngines.ps1 -GameRoot 'C:\Data\Game\Eras\eraOCG2_20261009'

# 2. 直接构建，观察报错是否指向受支持范围
.\scripts\Build-Bridge.ps1 -GameRoot 'C:\Data\Game\Eras\eraOCG2_20261009'
```

## 7. 未覆盖

- 其他 .NET Framework 血统的 Emuera 分支（不同作者改版）未逐个测试；当前只声明 1.821 已验证、1.822 可加载但未验证。
- 未实测这些 .NET Core 变体能否正常运行（不属于本桥接的能力范围），因此"游戏本体可用"与"桥接可用"是两件事。
- 解包可行性只做了静态特征判断，尚未真正提取过程序集。
