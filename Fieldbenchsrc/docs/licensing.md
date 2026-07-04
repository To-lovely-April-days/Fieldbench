# Fieldbench 授权与收款对接指南

覆盖 PRD §6.9（授权激活）与 §8.2（收款）的完整落地路径：
**Paddle 收款 → 自动发 license key → 用户在线一键激活 / 离线自助激活**。

## 0. 原理（30 秒版）

- 激活文件 = `{ payload, signature }`，payload 是 JSON（key、邮箱、绑定机器码、tier），
  signature 是你用 **Ed25519 私钥** 对 payload 的签名。
- 应用内嵌 **公钥**（`src/Fieldbench.Core/Licensing/LicenseManager.cs` 的 `PublicKey`），
  导入时验签 + 校验本机机器码在绑定列表里 → 激活。
- 激活后状态存本地，**永不回连**（PRD 红线）。伪造激活文件 = 破解 Ed25519，不可行。
- 服务端的唯一职责是"自动化签发"：验证 key 合法 → 用私钥签一份绑定用户机器码的文件。

## 1. 一次性准备（上线前）

```bash
# 1) 生成正式密钥对（仓库里现在这对是开发用的，发布前必须换！）
dotnet run --project tools/LicenseTool -- keygen
#   PRIVATE → 保存到密码管理器 + Cloudflare Worker secret，绝不进 git
#   PUBLIC  → 替换 LicenseManager.cs 中的 PublicKey 常量

# 2) 试签一张（本地就能测全流程，不需要任何服务器）
dotnet run --project tools/LicenseTool -- issue \
    --priv "<私钥>" --email buyer@example.com \
    --machine "AAAA-BBBB-CCCC-DDDD" --out activation.json
dotnet run --project tools/LicenseTool -- verify --pub "<公钥>" --file activation.json
```

`issue` 不带 `--machine` 时签发"未绑定"文件（任何机器可激活）——**不建议**用于正式发货，
仅适合评测/媒体 key。正式流程都在激活时绑定机器码（见下）。

## 2. Paddle 收款 → 发 key

1. Paddle 后台建产品：`Fieldbench Pro`（$99 / 首发 $69 一次性）、`AI Assistant`（$6.9 月 / $49 年订阅）、Bundle。
2. 配 webhook（Paddle Billing 的 `transaction.completed` 事件）指向你的 Worker。
3. Worker 收到付款成功 → 生成 license key → 存 KV → 邮件发给买家（Paddle 可代发，或用 Resend/Postmark）。

**买家邮件里只需要一个 key（`FB1-XXXX-XXXX-XXXX`）**，不需要附件——激活文件是激活时按机器码现签的。

## 3. 服务端：一个 Cloudflare Worker 搞定（~100 行）

需要的存储只有一张 KV 表：`key → { email, tier, machines: [] }`。

```js
// wrangler secret put ED25519_PRIVATE_KEY   (base64, 32 字节 seed)
// wrangler secret put PADDLE_WEBHOOK_SECRET
// KV binding: LICENSES

export default {
  async fetch(req, env) {
    const url = new URL(req.url);

    // ── Paddle webhook：付款成功 → 造 key 入库 → 发邮件 ──
    if (url.pathname === "/api/paddle-webhook" && req.method === "POST") {
      const body = await req.text();
      if (!(await verifyPaddleSignature(req, body, env.PADDLE_WEBHOOK_SECRET)))
        return new Response("bad signature", { status: 401 });
      const evt = JSON.parse(body);
      if (evt.event_type === "transaction.completed") {
        const email = evt.data.customer?.email ?? evt.data.custom_data?.email;
        const key = newKey();                       // FB1-XXXX-XXXX-XXXX
        await env.LICENSES.put(key, JSON.stringify({ email, tier: "pro", machines: [] }));
        await sendEmail(email, key);                // Resend/Postmark API
      }
      return new Response("ok");
    }

    // ── 在线激活：应用 POST {key, machine} → 返回签好的激活文件 ──
    // 应用内 Settings→License 的 Activate 按钮调用的就是这个端点。
    if (url.pathname === "/api/activate" && req.method === "POST") {
      const { key, machine } = await req.json();
      const rec = JSON.parse((await env.LICENSES.get(key)) ?? "null");
      if (!rec) return new Response("Unknown license key", { status: 404 });
      if (!rec.machines.includes(machine)) {
        if (rec.machines.length >= 2)               // PRD：1 license = 2 台
          return new Response("Both device slots are used — unbind one at fieldbench.app/devices", { status: 409 });
        rec.machines.push(machine);
        await env.LICENSES.put(key, JSON.stringify(rec));
      }
      return json(await signActivation(env, key, rec));
    }

    // ── 解绑自助页用：POST {key, machine} 从 machines 里移除 ──
    if (url.pathname === "/api/unbind" && req.method === "POST") { /* 同上，splice + put */ }

    return new Response("fieldbench license api");
  }
};

// payload/签名格式与 LicenseManager.Issue 完全一致：
// { "payload": base64(UTF8(JSON)), "signature": base64(ed25519_sign(payloadBytes)) }
async function signActivation(env, key, rec) {
  const payload = {
    key, email: rec.email, tier: rec.tier, machines: rec.machines,
    issuedUtc: new Date().toISOString(), expiresUtc: null,
  };
  const payloadBytes = new TextEncoder().encode(JSON.stringify(payload));
  const pkcs8 = seedToPkcs8(b64decode(env.ED25519_PRIVATE_KEY));   // 32B seed → PKCS#8
  const cryptoKey = await crypto.subtle.importKey("pkcs8", pkcs8, { name: "Ed25519" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("Ed25519", cryptoKey, payloadBytes);
  return { payload: b64encode(payloadBytes), signature: b64encode(new Uint8Array(sig)) };
}
```

> 注意 payload 的 JSON 字段名是 camelCase（`key/email/tier/machines/issuedUtc/expiresUtc`），
> 与应用端 `JsonSerializerDefaults.Web` 反序列化一致。`seedToPkcs8` 就是给 32 字节 seed 加
> 固定 16 字节 PKCS#8 头：`302e020100300506032b657004220420` + seed。
> 不想用 Worker 也可以用任何能跑 Ed25519 的东西（一台 VPS 上跑 `LicenseTool issue` 都行）。

## 4. 用户侧的两条激活路径（应用内已实现）

**在线激活（Settings → License）**：输入邮件里的 key → 点 Activate →
应用把 `{key, 本机机器码}` POST 到 `/api/activate`（地址在 `AppSettings.ActivationApiUrl` 配置）→
服务端绑定机器码并回签激活文件 → 应用自动导入。全程一次点击。

**离线激活（现场无网机器，PRD 明确卖点）**：
1. Settings → License 卡片显示本机机器码（如 `8F3A-22C1-90DE-4471`）
2. 用户在**任何**有网设备上打开 `fieldbench.app/activate`（一个静态页 + 上面同一个 `/api/activate` 端点），输入 key + 机器码 → 下载 `activation.json`
3. U 盘拷到目标机器 → Settings → "Import activation file…" → 激活完成，永久离线可用

两条路产出**同一种文件**，服务端只有一个签发端点，没有第二套逻辑。

## 5. 官网需要的页面（Paddle 审核也要求）

| 页面 | 内容 |
|---|---|
| `/pricing` | 三个 SKU + Paddle Checkout（内嵌 overlay，一行 JS） |
| `/activate` | 离线激活自助页：key + 机器码 → 下载激活文件（调 `/api/activate`） |
| `/devices` | 自助解绑：key + 邮箱验证 → 列出 2 台设备 → 解绑（调 `/api/unbind`） |
| `/privacy` `/refunds` | Paddle 上架审核必需；退款 30 天无理由（PRD §8.2） |

## 6. 安全备忘

- **私钥只存在两处**：你的密码管理器 + Worker secret。泄露 = 任何人可无限签发。
- 仓库里现在的密钥对是**开发用**的（私钥出现在测试里），发布前 `keygen` 换新并替换 `PublicKey`。
- 机器码是硬件指纹哈希（Windows MachineGuid / Linux machine-id），换主板会变——
  所以自助解绑页是必需品，客服邮件也要能手动解绑（KV 里删一行）。
- 试用不需要服务端：14 天 Pro 试用完全本地（`LicenseManager.StartTrial`）。
```
