# LineVision PaddleOCR CPU 性能基准（2026-09-08）

## 结论

发布版 PaddleOCR Worker 对一张 1632×1587 建筑表格截图连续进行 3 次独立进程识别，三轮均输出 81 个文字区域：

| 项目 | 结果 |
| --- | ---: |
| 首次独立进程识别 | 18.817 秒 |
| 后两次独立进程识别平均 | 18.666 秒 |
| 单轮最大峰值工作集 | 1089.3 MB |
| 发布组件目录体积 | 652.4 MB |

Worker 当前采用“一次任务、一个进程”的隔离设计，因此每次独立启动都会重新初始化 PaddleOCR 和三个固定模型，后续独立进程不会继承上一进程的内存模型，重复识别耗时与首次启动接近。应用内对相同图片内容和相同参数已有 SHA-256 结果缓存；缓存命中时直接读取版本化 JSON，不启动 Worker，本报告中的“重复独立进程”是未命中缓存时的最坏路径，不代表缓存命中耗时。

峰值工作集约 1.09 GB，高于组件磁盘体积属于正常现象：模型、Paddle 推理运行时和图片张量在识别期间会展开到内存。CPU 时间高于墙钟时间是 6 核 12 线程并行推理累计得到的进程 CPU 时间，并非单核耗时。

## 环境

- CPU：12th Gen Intel Core i5-12400F，6 核 12 线程
- 内存：31.8 GB
- 系统：Windows 11 专业工作站版，10.0.28000
- OCR：PaddleOCR 3.7.0、PaddlePaddle 3.3.1、PP-OCRv6 small
- 输入：1632×1587 本机建筑表格截图；样图含项目内容，仅用于本机测量，未提交仓库
- Worker：总插件发布包 `OcrEngine/LineVisionPaddleOcrWorker`
- 采样间隔：100 ms

## 单轮数据

| 轮次 | 类型 | 墙钟时间 | CPU 时间 | 峰值工作集 | 文字区域 |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | 首次独立启动 | 18.817 秒 | 92.844 秒 | 1080.7 MB | 81 |
| 2 | 重复独立启动 | 18.767 秒 | 87.531 秒 | 1089.3 MB | 81 |
| 3 | 重复独立启动 | 18.564 秒 | 91.844 秒 | 1088.8 MB | 81 |

## 重现方法

仓库提供 `build/Measure-LineVisionOcrPerformance.ps1`。它会为每轮生成唯一临时任务目录，运行发布版 Worker，持续采样 CPU 与工作集，校验协议版本、任务编号和成功状态，最后安全清理成功任务的临时文件。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build\Measure-LineVisionOcrPerformance.ps1 `
  -WorkerPath <发布目录>\OcrEngine\LineVisionPaddleOcrWorker\LineVisionPaddleOcrWorker.exe `
  -ImagePath <测试图片> `
  -Runs 3 `
  -JsonOutputPath artifacts\linevision-ocr-performance.json
```

失败任务的临时证据会保留并输出路径，避免清理掉诊断所需的请求、结果及标准错误；成功完成全部轮次后自动清理。

## 后续性能原则

- 保持独立进程隔离，OCR 崩溃或取消不得影响 AutoCAD 主进程。
- 优先依靠内容哈希缓存、用户裁剪和输入尺寸限制降低重复等待。
- 若以后引入常驻 Worker，必须先补充进程生命周期、内存回收、模型损坏隔离和 AutoCAD 退出清理测试；不能仅为缩短模型初始化而牺牲稳定性。
