# 目标框架与兼容策略

阶段0六个共享项目统一目标框架为 `netstandard2.0`：

```text
AutoCAD 2022—2024 / .NET Framework 4.8
                 ↓
           netstandard2.0
                 ↑
AutoCAD 2025—2026 / .NET 8 Windows
```

为保证两个技术代际读取同一份 JSON，领域模型采用以下兼容写法：

- 使用普通 `get/set` 属性，不使用 `required` 或 `init`；
- 使用 `DateTime` / `DateTimeOffset`，不使用 `DateOnly`；
- 使用 `new List<T>()`，不使用集合表达式；
- 不使用只存在于 .NET 8 的运行时 API；
- JSON 属性采用 camelCase，枚举采用 camelCase 字符串；
- 所有模型保存 `schemaVersion`，后续通过显式迁移升级。

测试项目使用 `net8.0`，用于验证共享程序集可被现代宿主引用；`CadArchSpec.Net48Compatibility` 使用 `net48` 编译并引用全部共享项目，用于持续验证旧技术代际兼容性。阶段5仍需增加真实 AutoCAD 2024 与 AutoCAD 2026 宿主加载测试。
