# LineVision PaddleOCR Worker

独立进程适配器，固定使用 `PaddleOCR 3.7.0 + PaddlePaddle 3.3.1 + PP-OCRv6 small`。它读取 `--request` 指定的版本化 JSON，并把结果写入 `--output`，不会加载到 AutoCAD 进程。

`build/Build-LineVisionPaddleOcrWorker.ps1` 将受支持的 Python 运行时、依赖和三个固定模型打包成免安装用户组件，同时生成逐文件 SHA-256 清单并携带 PaddlePaddle、PaddleOCR 和 PaddleX 许可证。主插件会从发布根目录的 `OcrEngine/LineVisionPaddleOcrWorker` 自动发现组件；组件缺失、损坏或识别失败时自动回退 Windows OCR。

PaddlePaddle 3.3.1 的 Windows CPU oneDNN/PIR 路径存在可复现异常，因此固定关闭 oneDNN。模型路径以 Worker 当前目录下的 ASCII 相对路径传入，以兼容安装目录包含中文的情况。
