# LineVision PaddleOCR Worker

独立进程适配器，固定使用 `PaddleOCR 3.7.0 + PaddlePaddle 3.3.1 + PP-OCRv6 small`。它读取 `--request` 指定的版本化 JSON，并把结果写入 `--output`，不会加载到 AutoCAD 进程。

当前为可验证的 Worker 原型；正式用户版还需把受支持的 Python 运行时、依赖和固定模型打包，并完成哈希与许可证清单后才启用自动选择。依赖缺失或版本不匹配时会返回结构化错误，由主插件回退 Windows OCR。
