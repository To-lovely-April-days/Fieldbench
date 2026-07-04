# Fieldbench

工业通信调试工作台 — 串口 · Modbus · AI。基于 **.NET 8 + Avalonia 11** 的完整 v1.0 实现，
对应 `fieldbench-v1.0-design-package/` 中的 PRD 与 UI 设计稿。

> Serial, Modbus and AI on one frame timeline: select any frame and the AI tells
> you what it is, what went wrong, and what to check next.

## 构建与运行

```bash
dotnet build                                 # 全解决方案
dotnet run --project src/Fieldbench.App      # 启动应用（Windows / Linux）
dotnet test                                  # 379 项协议核心测试 + headless UI 测试
```

无硬件也能看到全部能力：首跑弹窗底部 **Run demo** 一键启动进程内自环
（模拟 Master ↔ Slave，正弦值发生器 + 周期性线路误码 + 越界轮询），
时间轴、字段着色、寄存器网格、曲线、AI 解释全部实时演示。

## 架构

```
Connection(物理/网络通道)                    src/Fieldbench.Core/Transport
  └─ ByteStreamStore(字节流 = 唯一事实源)     src/Fieldbench.Core/Streams
       └─ Session × IProtocolLens(派生帧视图) src/Fieldbench.Core/Lenses
            └─ Master / Slave 引擎            src/Fieldbench.Core/{Master,Slave}
```

- **字节流是事实源，帧只是视图**（W1 验收项）：存储层只存带时间戳、分方向的原始
  字节流；任意时刻切换 Lens 会对全部历史重新切帧、着色、解析
  （`Session.SwitchLens` → `LensReplay.Replay`），有测试锁定该行为。
- **RTU 混合切帧**：候选长度预测 + CRC 校验即时抽帧（高波特率粘包不依赖计时），
  3.5T 静默（带下限，Windows 定时不可靠）+ CRC 滑窗回溯扫描处理残帧与再同步。
- **协议自动识别**：连续 ≥3 块 CRC16 通过 → 非模态 chip 一键切换解析；
  随机流零误报（200 块随机流测试锁定）。
- **总线被动监听**：物理方向全 RX 时按请求/响应语义推断 M→S / S→M 并计算 Δms。

## 功能面（对照 PRD §3 In 清单）

| 功能 | 状态 |
|---|---|
| 串口 / TCP Client / TCP Server + 参数热改 | ✅ `SerialTransport` 支持在线改波特率等，会话与捕获保留；连接配置档命名保存 + 左栏一键重连 |
| 监视 / Master / Slave 三类会话 + 统一时间轴 | ✅ 虚拟化列表，26px 行高，10 万帧环形缓冲 |
| 逐字节解析高亮 + 字节检查器 | ✅ 四色固定色板（地址/功能码/长度/数据 + CRC 红），悬停 tooltip 十/十六/二进制 |
| 时间轴过滤 + 导出入口 | ✅ 关键字（hex/ASCII/FC/@addr）、方向 TX/RX、仅异常帧；工具条导出 CSV/JSON/bin/txt（有选中导选中，否则全部） |
| 原始终端（hex/ASCII、校验附加、循环发送、收藏） | ✅ CRC16-Modbus / CCITT / XOR / SUM |
| Modbus Master：FC 01–06/0F/10、轮询、寄存器网格、扫描 | ✅ 同连接串行调度，超时 1000ms×3 可配，网格改值自动选 FC 下发，扫描 1–247 + 参数矩阵 |
| 数据类型 8 种 × 字节序 4 种 | ✅ bit/int16/uint16/int32/uint32/float32/double/string × ABCD/CDAB/BADC/DCBA |
| 异常码人话翻译 | ✅ 8 种异常码 + 排查提示 |
| Modbus Slave 仿真 + 值发生器 | ✅ 四区稀疏存储，静态/递增/随机/正弦（幅值/周期/范围/步长 ✎ 弹层可配），非法地址自动 EXC 02，未知功能码 EXC 01 |
| 数据曲线 + 时间轴联动 | ✅ 自绘 `FieldChart`（60s/10min/All，暂停，CSV 导出），异常点标红可点击跳回对应帧 |
| AI 报文解释 | ✅ 首次使用隐私同意弹窗 → 结构化流式输出（判断→原因排序→检查清单）；离线专家引擎打穿 5 个标杆场景，网关客户端就绪 |
| AI 点表导入 | ✅ 粘贴表格文本或剪贴板截图（vision 走网关）→ 地址基准强制二选一 → 冲突/越界标注 → 分段轮询任务（≤125 寄存器）→ 设备档案 JSON；成功才扣配额 |
| Demo 模式 | ✅ 进程内自环，不计入 Free 连接额度 |
| 授权（Ed25519 + 离线激活 + 14 天试用） | ✅ 机器码绑定，激活文件导入，激活后零回连 |
| Free/Pro 分层 | ✅ Free：1 连接、曲线 2 通道、Slave 仿真属 Pro；配额本地计数 |
| i18n EN/简中 + 深浅主题 | ✅ 运行时切换，同一 token 表派生 |
| 导出 CSV / JSON / bin / txt | ✅ 选中或全部帧；曲线 CSV |

设计稿的十个界面（主窗四态、Monitor 检测 chip、Slave、首跑、扫描、导入审核、设置）
均按 `fieldbench-ui-v3-*` 逐区实现；设计 token 见
`src/Fieldbench.App/Themes/Tokens.axaml`（浅色默认 + 深色派生）。

## 与 PRD 的已知偏差

- **图表库**：PRD 选型 ScottPlot；本实现使用自绘 `FieldChart` 控件（零依赖、
  完全贴合 Tesla 极简风格、三通道轮询速率下性能富余）。如需 ScottPlot 可在
  `ChartPanelView` 内替换。
- **AI 网关**：客户端（`GatewayAiClient`，license 鉴权头 + 流式 NDJSON）已就绪，
  Cloudflare Workers 端未包含在本仓库；无网关时自动回退到离线专家引擎
  （`OfflineAiEngine`），断网时其余功能零影响。
- **串口友好名**：Linux 下仅枚举设备名；Windows 友好名（`COM3 — CH340`）需接
  SetupAPI/WMI，接口已预留（`SerialTransport.ListPorts`）。
- 写盘记录（Record）当前为文本日志追加，PRD 的 P1 record-to-disk 二进制格式待 v1.0.x。

## 测试

- `tests/Fieldbench.Core.Tests` — 379 项：CRC/编解码向量、RTU 混合切帧
  （粘包、断帧、误码、再同步、角色推断）、MBAP、检测器零误报、
  **Lens 回溯切换一致性**、自环全功能码 E2E（master↔slave loopback）、
  扫描器、点表管线、Ed25519 授权、导出、配额月滚动。
- `tests/Fieldbench.App.UITests` — Avalonia.Headless：Demo 端到端
  （时间轴/网格/曲线/AI 全链路）、Lens 切换重解析、Free/Pro 门控，
  以及浅色/深色/中文三态截图（`artifacts/screenshots/`）。
