# Fieldbench 官网 SEO 与内容方案 v1.0

| | |
|---|---|
| 日期 | 2026-07-04 |
| 范围 | 站点架构 · 关键词地图 · 技术 SEO · 免费工具页 · 内容日历 · 转化度量 · Paddle 合规 |
| 前提 | 域名待定(fieldbench.app / .com 二择,W1 核验后锁定);纯英文站,静态生成 |

## 1. 战略判断

工程师工具不投广告,流量 100% 来自搜索与社区。搜索分两类,分别对应两套页面:

1. **问题词**(海量、稳定):"modbus crc calculator"、"modbus exception code 02"、"modbus function codes"——用免费工具页 + 文档承接,建立域名权重与信任;
2. **购买词**(量小、意图极高):"modbus poll alternative"、"modbus simulator windows"、"modbus tcp client tool"——用首页 + /vs/ 对比页承接,直接转化。

原则:每个页面只服务一个主词;工具页永远免费无墙,靠页内一条不打扰的产品横幅转化。

## 2. 站点架构

```
/                        首页(着陆页,定价与 FAQ 内嵌)
/download                下载页(installer + portable,SmartScreen 说明,校验和)
/pricing                 定价独立页(与首页锚点同内容,承接外链)
/vs/modbus-poll          对比页(最高购买意图词)
/tools/
  crc16-calculator       Modbus CRC-16 在线计算(纯前端)
  function-codes         功能码速查表
  exception-codes        异常码查询(01–0B 逐条排查指引)
  tcp-frame-parser       粘贴 hex → MBAP/PDU 解析(纯前端,即产品的免费网页版切片)
/docs/                   快速上手 · 激活/离线激活 · 各功能页 · FAQ
/blog/                   长尾文章
/changelog               版本记录(信任信号 + 回访)
/activate                离线激活自助页(机器码 → 激活文件,serverless)
/privacy /terms /refund-policy /contact     Paddle 审核必备
```

栈建议:**Astro 静态生成**,零 JS 默认、CWV 满分容易;工具页的交互用岛屿组件。部署 Cloudflare Pages(与 AI 网关同生态)。

## 3. 关键词地图(核心页)

| 页面 | 主词 | 次词 | Title(≤60 字符) | Meta description 要点 |
|---|---|---|---|---|
| / | modbus debugging software | serial modbus tool, modbus master slave software | Fieldbench — Serial & Modbus debugging with AI | 一个时间轴 + AI 解释坏帧 + 手册截图导点表;买断制、离线可用 |
| /vs/modbus-poll | modbus poll alternative | modbus poll vs, modbus slave alternative | Fieldbench vs Modbus Poll — an honest comparison | 一个 App 对两个产品、总线监听、AI;表格逐项对比,承认对方长处 |
| /tools/crc16-calculator | modbus crc calculator | crc16 modbus online, crc check calculator | Modbus CRC-16 Calculator (online, instant) | 粘贴 hex 即算,支持逐字节高亮;工程师收藏页 |
| /tools/exception-codes | modbus exception codes | exception code 02 illegal data address | Modbus Exception Codes — meaning & fixes | 每个码给"最可能原因 + 排查清单",不只是定义 |
| /tools/function-codes | modbus function codes | fc03 fc04 difference | Modbus Function Codes Reference (01–2B) | 速查表 + 请求/响应帧结构示例 |
| /tools/tcp-frame-parser | modbus tcp frame format | mbap header parser | Modbus TCP Frame Parser — paste hex, get fields | 免费网页版字节检查器,产品能力的直接试吃 |
| /download | modbus software download | portable modbus tool | Download Fieldbench for Windows | 签名安装包 + 绿色版,无账号 |
| /docs/offline-activation | offline license activation | air gapped activation | Activate Fieldbench fully offline | 打 ModBus Pro 联网激活软肋的落点页 |

长尾由 blog 承接(见 §6)。

## 4. 技术 SEO 清单

- **结构化数据(JSON-LD)**:首页 `SoftwareApplication`(含 offers;上线后有真实评价再加 aggregateRating,虚标会被处罚)+ `FAQPage`;文章页 `Article` + `BreadcrumbList`;工具页 `WebApplication`。首页稿中已内置前两种示例。
- **Meta 全套**:唯一 title/description;OG + Twitter card(og:image 用主窗口浅色截图 1200×630,预生成静态图);canonical 每页自指。
- **sitemap.xml + robots.txt**:Astro 插件生成;/activate 的结果页 noindex。
- **CWV**:静态站 + 系统字体(设计语言本来就不用 webfont,天然满分);图片 AVIF/WebP + width/height 防 CLS;首屏无第三方脚本。
- **URL 规范**:全小写连字符;工具页不带日期;文章 slug 用问题原文("modbus-crc-check-failed")。
- **图片 alt**:截图 alt 写场景不写文件名("Modbus timeline showing a CRC-failed frame with AI explanation")——图片搜索是工程师真实入口。
- **国际化**:v1 仅 en,不做 hreflang;中文站(国内分发)另域名另议,避免混淆权重。

## 5. 免费工具页:引流器设计

四个工具全部纯前端(零成本、零延迟、可被收藏):CRC 计算器、功能码表、异常码查询、TCP 帧解析器。共同规范:

- 工具区在首屏之上,无需滚动即用;结果支持 URL 参数分享(反链天然来源)
- 页尾一条固定横幅:"This is 1% of Fieldbench — the desktop workbench does this live on your COM port, with AI. Download free →"
- 异常码页每个码配"AI 会怎么排查"小节,直接展示产品差异化
- 帧解析器复用产品的字段四色与字节网格视觉——网页即产品预告片

## 6. 内容日历(T = 正式发售日)

| 周 | 篇目(标题即目标词) | 意图 |
|---|---|---|
| T-2 | Modbus Exception Code 02: Illegal Data Address — causes & fixes | 问题 |
| T-2 | Modbus CRC Check Failed: the 5 real causes (baud, wiring, timing) | 问题 |
| T-1 | RS-485 A/B wiring, termination and biasing — a field checklist | 问题 |
| T | Introducing Fieldbench(发布文,交叉发 r/PLC、PLCTalk、HN Show) | 品牌 |
| T | Fieldbench vs Modbus Poll(/vs/ 页同步) | 购买 |
| T+1 | Reading float32 from Modbus: byte order AB CD vs CD AB, solved | 问题 |
| T+2 | Modbus RTU frame timing: why 3.5T breaks on Windows | 问题(技术信誉文)|
| T+3 | How to simulate a Modbus slave (no hardware) | 问题→功能 |
| T+4 | Scanning for unknown slave addresses on a 485 bus | 问题→功能 |
| T+6 | From device manual to polling in 60 seconds(点表导入演示,配视频) | 功能 |
| T+8 | Passive Modbus bus monitoring: what your SCADA can't show you | 功能 |

节奏:上线前 3 篇打底,之后每周 1 篇,标题永远 = 工程师会敲进搜索框的原话。

## 7. 站外与分发

- **YouTube**:每篇功能文配 2–4 分钟实操视频;"modbus tutorial" 系词常年稳定,视频描述回链工具页。
- **社区**:r/PLC、PLCTalk、Stack Overflow 的 modbus tag——只答题,签名带链;发布帖各一次,之后不刷。
- **GitHub**:开源 CLI 版 CRC/帧解析小工具(MIT),README 指官网——工程师世界最干净的反链。
- **目录站**:AlternativeTo(挂在 Modbus Poll 的 alternatives 下,免费高意图流量)、Product Hunt(发一次,不指望)。

## 8. 转化埋点与度量

- **分析**:Plausible(无 cookie、无横幅,与"隐私体面"的产品叙事一致);Google Search Console 上线日即验证。
- **事件**:download_free、buy_click(区分 pro/ai/bundle)、checkout_completed(Paddle webhook 回传)、tool_used(四个工具页)、docs_offline_activation_view。
- **北极星漏斗**:工具页会话 → 下载 → 首次连接(App 内匿名可选统计,默认关)→ 购买。发售 +30 天基线:自然搜索会话、下载 ≥500、付费 ≥10 对齐 PRD §12。

## 9. Paddle 上架合规清单(W3 提审前逐项勾)

- [ ] 定价页公开且与 checkout 金额一致(含首发折扣的划线展示)
- [ ] Privacy / Terms / **Refund policy(30 天)** / Contact 四页齐全,页脚全站可达
- [ ] 产品交付方式说明(数字交付:key 邮件即时发货)写入 Terms
- [ ] 公司/个人主体信息与 Paddle 账户一致;支持邮箱可回复
- [ ] 订阅条款:AI 订阅取消政策、周期、配额写明
- [ ] 站点无"coming soon"空页;下载链接真实可用(Beta 版即可)

## 10. 上线检查单(发售日)

- [ ] GSC 提交 sitemap;首页 + 8 个核心页 request indexing
- [ ] OG 图在 X/LinkedIn/Slack 三处预览验证
- [ ] 404 页(带搜索与下载入口);/blog 空目录不上线,首发即 3 篇
- [ ] Lighthouse 四项 ≥95;移动端定价卡与 FAQ 手测
- [ ] license@ / support@ 邮箱 SPF/DKIM 配置(交付邮件进垃圾箱 = 交付事故)
- [ ] Paddle 沙盒全流程走一遍:购买 → webhook → key 生成 → 邮件 → 激活
