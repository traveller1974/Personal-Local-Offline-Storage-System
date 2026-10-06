# 本地库存管理 1.1.1

Windows 11 x64、WPF / .NET 10、SQLite。库存本机保存；照片识别使用阿里云百炼 `qwen3.5-ocr`，只上传用户确认的表格截图。手工业务与本机离线备用识别可断网使用。

功能升级依据见 [升级计划](docs/升级计划-v1.1.md)，本次修复见 [1.1.1 发布说明](docs/发布说明-v1.1.1.md)，操作说明见 [使用说明](docs/使用说明.md)，验证结果和未验证边界见 [验收报告](docs/验收报告.md) 与 [已知限制](docs/已知限制.md)。安装包、SHA-256、说明书及报告在本机 `dist` 目录，也可从 [GitHub 1.1.1 发布页](https://github.com/traveller1974/Personal-Local-Offline-Storage-System/releases/tag/local-stock-manager-v1.1.1) 下载。

安装包只更新程序目录，保留已有数据库、照片、密钥设置和备份；安装前请关闭软件。首次启动会先完整备份，再迁移数据库，库存余额及已有单据内容保留；三年历史保留规则继续按升级计划执行。

## 主要功能

- 类型、名称、完整规格、颜色、物料编码共同区分库存。标识随单保存。
- 多张照片与重复货品保留独立原行，库存及作废按货品合计校验。
- 本机检测表格、四角调整、透视校正、截图确认、云端识别、逐行核对和最终入库。
- 框选裁剪先预览、再应用或取消；显示最新处理图并保持比例。识别缺字段时保留待核对行，错误指出具体行与字段。
- SQL 组合筛选、分页、分类型与单位汇总、自选列及流式 Excel 全部结果导出。
- v1→v2 保护备份与事务迁移，旧资料待补全，历史快照不改写。
- 滚动三年结转维护，保留当前库存；v1/v2 备份恢复，升级及卸载保留数据。

API Key 在软件设置中填写，由 Windows 当前用户加密保存，不进入源码、库存备份、日志或安装包。服务地址和密钥地域需以百炼控制台为准。真实云端识别准确率与本次修复对真实响应的兼容性，仍需用户在软件内主动识别后验收；开发测试不会读取用户密钥或上传照片。

## 开发与验证

SDK 版本在 `global.json`，NuGet 和本机备用 OCR 依赖均锁定。为精简目录，本地开发工具、虚拟环境及构建缓存已清理；首次重新构建使用 `-Bootstrap` 恢复 `tools`、`ocr/.venv`。软件使用不需要开发环境。

```powershell
.\scripts\build.ps1 -Bootstrap
.\scripts\build.ps1 -SkipOcrBuild
.\scripts\verify.ps1 -OutputDirectory artifacts/v1.1.1/desktop-smoke
```

正常构建运行核心回归、20万行 SQL/汇总/分页/导出与取消测试、本机 OCR 样本及错误协议，生成自包含发布、NSIS 安装包和 SHA-256。`-SkipOcrBuild` 只复用未变更的已测试本机 OCR 工作程序。

```powershell
dotnet test tests/Stock.Tests/Stock.Tests.csproj -c Release --filter 'Category=Performance'
.\scripts\install_upgrade_test.ps1
dotnet run --project tests/Stock.DesktopHarness/Stock.DesktopHarness.csproj -c Release -- (Get-Location).Path
```

安装升级测试用独立注册表、快捷方式、安装目录和测试数据。`install_upgrade_test.ps1` 覆盖 v1→当前版迁移，须保留 `artifacts/v1.1/v1-test-setup.exe`；新克隆需先构建隔离的 v1 基准包。`install_patch_test.ps1 -BaselinePublish <v1.1.0自包含发布目录>` 覆盖 v1.1.0→v1.1.1 的安装、重复安装与卸载，逐文件比较数据库、照片、DPAPI 设置及保护备份的 SHA-256。测试不改变真实安装，当前与历史精简证据分别保存在 `docs/验证证据/v1.1.1` 和 `docs/验证证据/v1.1`。

人工样单基准在 `docs/样单人工基准.json`，`null` 代表模糊完整字段待人工确认。开发者可用 `scripts/evaluate_samples.py` 比较本地保存的识别结果，不会自动上传或读取密钥。模拟接口测试不能代替真实样单验收。

## 数据与源码

默认数据目录为 `%LOCALAPPDATA%\LocalStockManager\Data`；密钥设置与保护备份位于其旁。数据库使用 `PRAGMA user_version=2`。未知结构和更高版本会拒绝打开，不重建空库掩盖错误。SQLite 事务内验证、更新余额；三年维护核对结转与保留流水后才提交。

`src/Stock.Core` 包含识别协议、本地校验、事务记账、迁移、SQL 查询、流式导出和备份维护。`src/Stock.Desktop` 包含 WPF 核对、图片处理、密钥加密和用户流程。`ocr` 为用户主动选择的本机识别备用组件。第三方许可见 `THIRD-PARTY-NOTICES.md` 和 `licenses`。

仓库不提交用户数据库、照片、密钥、开发工具和构建缓存。
