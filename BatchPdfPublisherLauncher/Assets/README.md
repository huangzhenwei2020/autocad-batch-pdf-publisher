# 启动器设计素材

确认稿：`approved-design.png`，由用户确认的日间 / 夜间设计板。

正式图标：`../BatchPdfPublisherIcon.png`。内置 image_gen 工具从确认稿提取，保留透明背景。`../BatchPdfPublisher.ico` 为同一图像封装的 16、20、24、32、40、48、64、128、256 像素 ICO，共九档尺寸。

本次素材生成使用内置工具，未使用 API/CLI。生成提示词如下：

> Extract the blue rounded-square W app icon shown at the upper left of each launcher window in the reference. Produce a single production app icon, front facing square centered, nearly filling a 1024x1024 transparent canvas, no UI, no letters outside icon, no caption, no surrounding shadow. Preserve precisely its glossy sapphire/cyan blue squircle, folded white architectural W building monogram with icy-blue faceted shading. Actual alpha transparency outside rounded corners. This is the approved existing icon, not a new logo redesign.

界面使用实际控件绘制，文字、主题按钮、开关和快捷键标签不嵌在背景图片中，方便字体和 DPI 缩放。原生窗口边框、滚动条及系统文件选择框随 Windows 版本变化，不承诺像素级一致。
