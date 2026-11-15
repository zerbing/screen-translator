# 本地 OCR 模型

`eng.traineddata` 和 `chi_sim.traineddata` 来自 Tesseract 官方 `tessdata_fast`，采用 Apache-2.0 许可，见 `LICENSE.txt`。仅用于内存中的局部文字补读，不上传截图。

| 模型 | 固定来源 | SHA-256 |
| --- | --- | --- |
| English | https://github.com/tesseract-ocr/tessdata_fast/blob/923915d4ced2a7235221788285785a29c4a42d4a/eng.traineddata | `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2` |
| 简体中文与混排 | https://github.com/tesseract-ocr/tessdata_fast/blob/80d92b7db61cb6ddc519049990be34e4c913566f/chi_sim.traineddata | `a5fcb6f0db1e1d6d8522f39db4e848f05984669172e584e8d76b6b3141e1f730` |

.NET 包 `Tesseract` 5.2.0 来自 https://github.com/charlesw/tesseract，采用 Apache-2.0 许可，并包含 Tesseract 5.2.0 与 Leptonica 1.82.0 本地库。Leptonica 许可见 `LEPTONICA-LICENSE.txt`。

模型目录和 `x64` 本地库目录需随程序一起分发；它们不含用户配置或测试截图。
