# 第三方组件、模型和分发声明

本项目库存业务代码自行实现，参考了 MIT 授权的 [InventorySystem](https://github.com/mbilalakmal/InventorySystem) 的本地库存思路，未复制其源码。

| 组件 | 固定版本 | 许可 / 用途 |
|---|---|---|
| .NET / WPF | 10.0.11；SDK 10.0.400 | MIT 与随包第三方声明；自包含桌面运行时 |
| Microsoft.Data.Sqlite | 10.0.0 | MIT；SQLite 本地数据库 |
| SQLitePCLRaw / SQLite | 2.1.11 | Apache-2.0 / Public Domain |
| ClosedXML | 0.105.0 | MIT；Excel |
| Open XML SDK | 3.1.1 | MIT |
| ClosedXML.Parser | 2.0.0 | MIT |
| ExcelNumberFormat | 1.1.0 | MIT |
| RBush.Signed | 4.0.0 | MIT |
| SixLabors.Fonts | 1.0.0 | Apache-2.0 |
| OpenCvSharp / OpenCV | 4.10.0.20240616 / 4.10.0 | Apache-2.0 及随包声明；图片与 DirectShow 摄像头 |
| Python | 3.12.10 | PSF；随 OCR 独立分发 |
| RapidOCR | rapidocr-onnxruntime 1.4.4 | Apache-2.0 |
| ONNX Runtime | 1.20.1 | MIT；CPU 推理 |
| NumPy | 1.26.4 | BSD-3-Clause 及附带数值库声明 |
| opencv-python | 4.10.0.84 | Apache-2.0 及随包第三方声明；OCR 图像处理 |
| Pillow | 11.1.0 | HPND；图像输入与样本测试 |
| Shapely / GEOS | 2.1.2 / 3.13.1 | BSD-3-Clause / LGPL-2.1；几何计算，GEOS 为独立动态库 |
| pyclipper / Clipper | 1.4.0 | MIT / Boost Software License；文字区域扩展 |
| PyInstaller | 6.12.0 | GPL-2.0-or-later with bootloader exception；打包工具 |
| NSIS | 3.11 | zlib/libpng、bzip2、CPL-1.0 with LZMA exception；安装程序 |
| Microsoft Visual C++ runtime | 随 Python、ONNX 依赖分发 | Microsoft 分发条款；原生运行时 |

Python 的完整传递依赖版本保存在 `ocr/requirements.lock.txt`；NuGet 的传递依赖保存在各项目 `packages.lock.json`。`licenses/dependency-inventory.json` 记录实际依赖、许可元数据和来源，`licenses` 目录保留许可全文与第三方声明。GEOS 3.13.1 对应源码随安装包保存在 `licenses/sources`，其源码亦可从 [GEOS 官方](https://download.osgeo.org/geos/) 获取；可用兼容的自行构建动态库替换该独立库。

离线模型源于 [RapidAI/RapidOCR](https://github.com/RapidAI/RapidOCR) 的 1.4.4 wheel，模型体系源于 Apache-2.0 授权的 [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)。检测与识别使用 PP-OCRv4；文字方向分类使用该发行包配套的 PP-OCR mobile v2 分类模型。中文识别字典嵌入识别模型元数据。模型校验值保存在 `ocr/models/manifest.json`，运行时读取明确的本地路径并核验 SHA-256，没有模型下载代码。

不包含 Office、不分发 Windows 字体。示例货单的图片在本机生成，并非用户的真实厂家单据。原生 FFmpeg 视频文件组件未使用且未包含在最终发布目录，本版只处理照片和 DirectShow 摄像头。
