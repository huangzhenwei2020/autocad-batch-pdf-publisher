# 万落建筑工具统一云盘授权长期方案

## 1. 结论

普通用户不应创建百度开放平台应用，也不应接触 App Key、Secret Key 或 OAuth 回调地址。万落建筑工具使用一个通过审核的统一应用，用户只需点击“登录百度网盘”。

`https://openapi.baidu.com/oauth/2.0/login_success` 仅作为开发联调期间的临时回调页。它位于百度域名下，万落无法控制其策略、可用性和未来变更，因此生产版本必须改用万落自己控制的固定 HTTPS 回调地址。

## 2. 生产结构

```text
AutoCAD 插件
  ├─ 临时 RSA 密钥 + 本机随机回调端口
  ├─ 浏览器打开百度官方授权页
  └─ DPAPI 保存最终 Token
             │
             ▼
万落授权代理（固定 HTTPS 域名）
  ├─ 保存百度 Secret Key（云端 Secret，不进源码）
  ├─ 签发短时、加密、防篡改的 OAuth state
  ├─ 用授权码换取 Token
  └─ 使用插件临时公钥加密 Token 后立即返回，不落库
             │
             ▼
百度网盘官方 OAuth / 文件 API
```

## 3. 安全规则

1. App Key 可作为公开配置；Secret Key 只存放在云端 Secret 管理中。
2. 不在服务器数据库、日志、分析平台或异常信息中保存 Access Token、Refresh Token。
3. OAuth state 使用 AES-256-GCM 加密，包含过期时间、随机 nonce、本机端口和插件临时公钥，默认十分钟失效。
4. Token 使用 AES-256-CBC 加密并以 HMAC-SHA256 做先验完整性校验，两把随机密钥再使用插件临时 RSA-OAEP-SHA256 公钥封装；该组合同时兼容 Cloudflare Workers 与 AutoCAD 2022 所用的 .NET Framework 4.8。
5. 插件只监听 `127.0.0.1` 的随机高位端口；收到一次有效回调后立即关闭。
6. 刷新 Token 时服务端仅在单次 HTTPS 请求内处理 Refresh Token，响应完成后不保留。
7. 生产服务必须启用速率限制、请求体上限、安全响应头、日志脱敏和告警。

## 4. 接口

- `GET /health`：无凭据健康检查。
- `POST /v1/baidu/authorize`：接收本机端口、nonce 和临时 RSA 公钥，返回百度授权地址。
- `GET /oauth/baidu/callback`：固定百度回调地址；校验 state、换取并加密 Token，跳转至本机回调。
- `POST /v1/baidu/refresh`：刷新 Token，返回临时公钥加密的结果。

## 5. 配置与部署

非秘密配置：

- `BAIDU_CLIENT_ID`
- `BAIDU_REDIRECT_URI`，生产值例如 `https://auth.example.com/oauth/baidu/callback`

云端秘密：

- `BAIDU_CLIENT_SECRET`
- `STATE_ENCRYPTION_KEY`（32 字节随机值的 Base64URL）

首版服务可部署到 Cloudflare Workers。Secret 必须使用平台 Secret 管理，不能写入 `wrangler.jsonc`、源码、Git 或便携版。

## 6. 实施顺序

1. 云端授权代理、加密协议和自动测试。
2. 插件本机回调监听、临时密钥、Token 解密和刷新客户端。
3. 设置界面切换为单一“登录百度网盘”按钮；开发者参数移入高级诊断页。
4. 部署测试域名，完成百度后台回调地址配置和真实账号联调。
5. 配置生产域名、速率限制、日志脱敏与监控。
6. 两台电脑授权、首次同步、Token 过期、断网、取消和升级回滚验收。

## 7. 发布条件

- 百度统一应用审核通过。
- 固定 HTTPS 回调域名和授权代理已部署。
- Secret 扫描确认源码、构建产物和日志中均无 Secret Key。
- 插件与服务端协议测试、R24 构建和两机真实同步全部通过。

## 8. 部署状态

- 2026-09-01：Cloudflare Worker 已部署至 `https://wanluo-cloud-auth-broker.xxyyu520.workers.dev`，健康检查通过；百度 App Key、Secret、固定回调地址和状态加密密钥均以 Cloudflare Secret 保存，未写入仓库。
- 2026-09-01：百度开放平台已登记固定回调地址；插件默认启用统一授权代理。Worker 已启用 32 KiB 严格请求体上限、授权接口限流、安全响应头和可观测性。
- 待完成：在插件中完成一次真实百度账号登录及两台电脑的同步验收。
