# 本地库存管理

Windows 11 x64、WPF / .NET 10、SQLite、本地中文 OCR、Excel 导出。业务范围与固定验收条件见 [执行规格](docs/执行规格.md)。最终用户使用 `dist` 中的离线安装包，不需要开发环境。

## 下载与备份

[下载 1.0.0 离线安装包、校验文件和交付文档](https://github.com/traveller1974/-/releases/tag/local-stock-manager-v1.0.0)。安装包适用于 Windows 11 x64，正常使用和中文 OCR 均不需要联网。

本仓库备份源码、锁定的依赖版本、中文 OCR 模型及第三方许可。安装包通过 GitHub Release 保存；本机库存数据库、照片、开发工具、构建缓存和测试数据库不提交到仓库。验证范围及尚未验证的项目见 [验收报告](docs/验收报告.md) 和 [已知限制](docs/已知限制.md)。

仓库原始说明：个人vibe codeing作品，仅供娱乐。

## 构建

开发机需要 .NET SDK 10.0.400 与 PowerShell 7。SDK 版本见 `global.json`。Python 3.12.10、OCR、NSIS 3.11 等工具由引导脚本下载到项目自己的 `tools` / `ocr/.venv` 目录；不会要求用户电脑安装这些工具。

首次构建：

```powershell
.\scripts\build.ps1 -Bootstrap
```

若开发时需要本机代理，可以明确传入已开启的代理地址，例如：

```powershell
.\scripts\build.ps1 -Bootstrap -DownloadProxy http://127.0.0.1:7890
```

代理参数只用于开发工具下载，命令结束即停止使用，不更改系统代理设置。正常使用软件不联网。

后续构建：

```powershell
.\scripts\build.ps1
```

构建锁定 NuGet / Python 版本，验证模型和构建工具校验值，运行业务测试、真实 OCR 样本测试及错误协议测试，然后自包含发布并生成 NSIS 安装包和 SHA-256。许可原文与 GEOS 对应源码保存在 `licenses`。没有变更 OCR 工作程序时可使用 `-SkipOcrBuild` 复用已有已测试工作程序。

生成目录为 `artifacts/publish`，交付目录为 `dist`。自动化测试证据在 `artifacts/tests`、`artifacts/ocr`、`artifacts/desktop-smoke`。

## 应用集成测试

```powershell
.\scripts\verify.ps1
```

测试使用独立项目子目录，从实际自包含应用调用真实 OCR，不触碰用户数据。验证实际 WPF 数量按钮、草稿取消、重复提交、渠道必选、图片处理、货单解析、OCR 超时、Excel 与备份恢复，并输出界面图片。

未在干净测试电脑上验证的项目必须标为未验证。具体结果以 `dist/验收报告.md` 为准。

## 数据结构与迁移

`Stock.Core/StockService.Initialize` 负责事务性的结构版本0→1创建，仅在没有应用表的新数据库中执行。使用 `PRAGMA user_version=1` 标识当前结构；未知旧结构与高于1的数据库直接拒绝打开，保留原文件，禁止通过重建空库掩盖错误。本版不存在需要升级的已发布旧结构；以后增加迁移时必须先使用 SQLite 备份 API 做保护，再在事务中逐版迁移。

每次打开检查数据库和库存不变量。所有写入都由业务服务的立即 SQLite 事务完成；UI 不写余额。五年清理先结转流水，再删除历史，原单与作废反向流水均参加库存核对。

## 文件

- `src/Stock.Core`：事务业务、图片引用、记录查询、结转、备份恢复、Excel、OCR 表格解析。
- `src/Stock.Desktop`：WPF 界面、视图模型、数量组件、OCR 进程客户端、OpenCV 图片和摄像头。
- `ocr/worker.py`：单行 JSON 标准输入输出协议，仅识别图片，不访问库存数据库。
- `installer/installer.nsi`：当前用户安装、快捷方式、升级和卸载保留数据。
- `docs`：执行规格、使用说明、限制和交付报告。
