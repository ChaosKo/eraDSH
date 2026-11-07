# 引擎识别

`Build-Bridge.ps1` 只扫描游戏目录**顶层**的 `*.exe`，逐个分类，不做任何写入。

## 分类

| 类别 | 判定 | 能否驱动 |
| --- | --- | --- |
| `framework` | 托管程序集，名为 `Emuera` | 能（反射宿主） |
| `core` | 无 CLR 头，但含 `hostfxr` 与 `MinorShift.Emuera` 标记 | 能（启动钩子） |
| `managed-other` | 托管程序集，但不是 Emuera | 不能 |
| `native` | 其余（更新器、外部工具） | 不能 |

`.NET Core` 单文件发布把托管程序集塞进 exe，exe 本身是原生 apphost，没有 CLR 头，所以"程序集名必须是 Emuera"这条规则对它永远不成立——判定依据是文件特征，不是名字。

## 选择顺序

1. 显式 `-EngineExecutable` / `ERADSH_ENGINE_EXE` / 配置项；
2. 否则在同目录候选里挑：已验证版本优先，其次唯一候选；
3. 多个候选无法唯一确定时报错并列出清单，**不猜**。

先看清单再决定：

```powershell
.\scripts\Get-EraDshEngines.ps1 -GameRoot 'D:\Games\SomeEraGame'
```

## 已验证范围

| 引擎 | 形态 | 状态 |
| --- | --- | --- |
| Emuera 1.821.5843.38796 | .NET Framework x86 | 已验证 |
| Emuera 1.822.6770.41230 | .NET Framework x86 | 可加载，未验证动作 |
| Emuera.NET / Lazyloading EE（bundle 6.0 / net8.0） | .NET 8 x64 与 x86 | open/observe/act/advance/close 已验证 |
| 其他分支 | — | 未测试 |

完整矩阵、证据与仍未覆盖的部分见 [engine-compatibility.md](engine-compatibility.md)。

## 运行时要求

- `framework` 引擎：Windows + .NET Framework 4（系统自带）+ PowerShell 5.1。
- `core` 引擎：额外需要 .NET 8 Desktop Runtime，以及首次构建时的一份 .NET SDK（之后复用编好的 `EraBridgeHook.dll`）。
