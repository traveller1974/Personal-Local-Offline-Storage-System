# 本地库存管理 1.2.0

Windows 11 x64、WPF / .NET 10、SQLite。库存保存到本机；手工业务和备用OCR可断网使用。联网识别可选千问 `qwen3.5-ocr` 或官方 DeepSeek `deepseek-flash`，只上传确认后的截图。

本版取消OCR对人工填写的限制：数量列不明、单位缺失、照片合计不同都只是提示。“已核对”为可选标记，入库使用最终清单。保留原进货步骤和名称、规格、颜色词条，图片约40%、结果约60%；增加本机纠错记忆和DeepSeek设置。

千问真实样单未通过自动填单验收；DeepSeek待用户配置密钥后继续真实测试。默认保留千问，识别仅作建议，最后仍需用户确认入库。

- [使用说明](docs/使用说明.md)
- [本版发布说明](docs/发布说明-v1.2.0.md)
- [验收报告与真实测试结果](docs/验收报告.md)
- [识图辅助填单架构](docs/识图自动填单架构-v1.2.md)
- [独立识图程序](docs/千问识别独立测试.md)
- [已知限制](docs/已知限制.md)

安装包、独立EXE、SHA-256和说明文档位于本机 `dist`。安装前关闭主程序；升级保留已有库存、照片、加密设置和备份。1.2.0库存数据库仍为schema v2，不改写历史快照。独立纠错数据库为schema v1。

## 功能

- 类型、名称、完整规格、颜色、编码共同区分库存。标记随单保存。
- 名称、规格、颜色支持关键词候选或填写新值；手工进货可新建零库存货品。
- 识图辅助选品和填数量，支持补漏行、移除多余行、直接修改。照片原文可展开对照。
- 成功入库后记录修正。相关货品参考最多3条，永远不带入过去的数量；可关闭学习、停用错误记录。
- 多照片和重复货品保留独立原行。库存记账、作废、调拨继续事务校验，不允许负库存。
- 组合筛选、分页、分类汇总、流式Excel导出及取消。
- v1→v2保护备份和迁移、滚动三年结转维护、备份恢复；升级及卸载保留数据。

API Key在“设置与备份”填写，按服务分别由Windows当前用户加密，不进源码、库存备份、日志或安装包。核对记录是本机辅助资料，不训练云模型。

## 构建与验证

SDK在 `global.json`，NuGet及本机OCR依赖锁定。新环境用 `-Bootstrap` 恢复 `tools`、`ocr/.venv`；普通用户不需要开发环境。

```powershell
.\scripts\build_recognition_lab.ps1 -SmokeTest
.\scripts\verify_recognition_integration.ps1
.\scripts\build.ps1 -Bootstrap -UseEmbeddedRecognition -AppVersion 1.2.0
.\scripts\verify.ps1 -ApplicationPath artifacts/publish-1.2.0-true/LocalStockManager.exe -OutputDirectory artifacts/v1.2.0/desktop-smoke -RecognitionIntegration
```

`-UseEmbeddedRecognition`嵌入已生成且哈希一致的 `dist/Stock.RecognitionLab.exe`，不会重建它。普通构建默认用进程内入口；两个入口共享服务和解析实现。软件测试通过不等于真实识别准确度通过。

构建包含核心回归、20万行查询/汇总/分页/导出与取消检查、本机OCR及协议、隐藏识图后台和完整进货流程，生成自包含发布、NSIS安装包和SHA-256。`-SkipOcrBuild`仅复用未变化且已验证的本机OCR程序。

```powershell
.\scripts\install_patch_test.ps1 -BaselinePublish artifacts/baseline-1.1.4 -BaselineVersion 1.1.4 -TargetVersion 1.2.0 -TargetPublish artifacts/publish-1.2.0-true -EmbeddedRecognition
```

升级测试使用独立注册表、快捷方式、安装目录和数据，逐文件比较数据库、照片、DPAPI设置、模型选择、纠错文件和备份。证据保存在 `docs/验证证据/v1.2.0`；历史证据保留。实际显示器DPI、干净电脑和实体摄像头尚未覆盖。

人工样单基准 `docs/样单人工基准.json` 的 `null`是待确认字段，不能算正确。`scripts/evaluate_samples.py`读取本地结果，不上传图片或读取密钥。显式开发入口 `--evaluate-confirmed-samples` 用于已授权真实测试，只加载加密设置、不打开库存数据库；普通启动不会运行它。

## 数据与模块

默认数据目录 `%LOCALAPPDATA%\LocalStockManager\Data`，设置和保护备份位于旁边。未知或更高库存结构版本拒绝打开，不重建空库。

`Stock.Recognition`负责共享接口、解析和协议；`Stock.RecognitionLab`是独立EXE；`Stock.Core`负责业务、纠错匹配和维护；`Stock.Desktop`负责原用户流程。`ocr`为主动选择的本机备用组件。第三方许可见 `THIRD-PARTY-NOTICES.md` 和 `licenses`。

仓库不提交真实数据库、照片、密钥、模型原始响应、开发工具或构建缓存。
