# 阶段0架构

依赖方向：

```text
CadArchSpec.Domain
├─ CadArchSpec.Application
├─ CadArchSpec.RuleEngine
├─ CadArchSpec.LayoutEngine
├─ CadArchSpec.StandardRegistry
└─ CadArchSpec.EditorBridge

CadArchSpec.Stage0.Tests
└─ 引用以上全部共享项目
```

约束：

- `Domain` 不引用 AutoCAD、WPF、WebView2、前端或 AI；
- `Application` 只引用领域模型和抽象接口；
- `RuleEngine` 只解析受控规则和白名单公式；
- `LayoutEngine` 只处理纸面毫米与版面 DTO；
- `StandardRegistry` 不包含商业规范全文；
- `EditorBridge` 只负责统一 JSON 与版本化消息协议；
- React 前端是独立静态资源，不引用任何 AutoCAD 类型。

后续 AutoCAD 2022—2026 宿主必须是薄适配层，不能复制领域、规则或版面业务逻辑。
