# 开发与构建指南

README.md 面向产品使用者，只介绍功能、使用方法和使用范围。构建、测试、打包及实现约束集中维护在本文件；面向用户的截图放在 docs/images/，只使用演示数据。

## 开发环境

- Windows 10 2004+，x64。
- PowerShell 7（pwsh）。
- .NET 10 SDK，版本策略见 global.json。
- Visual Studio 2022 或更新版本的“使用 C++ 的桌面开发”工作负载，含 MSVC x64 工具。native/build.ps1 使用 vswhere 定位工具并编译附带的 SILK 解码源码。
- 首次还原 NuGet 依赖需要网络。语音识别运行时要求 Windows 11+ 与 VC++ 2022 x64 运行库；普通采集与音频导出可在 Windows 10 使用。

主要实现为 C# / .NET 10 WinForms；native/ 为 C 语言 SILK 解码器及构建包装。保留 .gitattributes 与 .editorconfig 规定的编码和行尾。

## 本地构建

在仓库根目录执行：

```powershell
# 精简 DLL 版本，需要 .NET 10 Desktop Runtime
.\build.ps1

# 独立 EXE，包含 .NET 运行环境
.\build.ps1 -Standalone

# 自定义输出目录，不创建快捷方式
.\build.ps1 -Standalone -SkipShortcut -OutputDirectory dist/Ripper-local
```

默认输出分别为 dist/Ripper/ 与 dist/Ripper-exe/。独立版运行入口为 Ripper.exe，DLL 版通过启动.cmd 或 `dotnet Ripper.dll` 启动。两种版本都需要保留配套 runtimes/ 语音运行库。

build.ps1 会构建 SILK 解码组件，附带许可证、使用说明、文档图片及对应源码；语音模型、用户设置和聊天数据不随程序打包。

## 验证

```powershell
# 构建并运行合成测试
.\test.ps1

# 验证指定的已构建程序
.\test.ps1 -Application dist/Ripper-exe/Ripper.exe -Report artifacts/tests/standalone.json
```

test.ps1 使用合成数据，不连接个人微信；报告默认在 artifacts/tests/raw-tests.json，界面预览 PNG 生成在报告旁。按修改范围执行验证：会话选择与分页、导出和附件、语音与设备回退、数据库页及 WAL 等行为均有合成验证。文档或截图修改检查链接、图片和发布目录复制即可，无需单独增加测试或运行全套测试。

可选的本机只读连接诊断命令如下，诊断不属于普通测试步骤：

```powershell
dotnet .\dist\Ripper\Ripper.dll --raw-probe .\artifacts\raw-probe.json
```

诊断报告不应含密钥、联系人标识或聊天正文。旧版 --self-test 仅用于遗留截图代码验证。真实语音模型验证与真实微信兼容性验证应单独记录，不混入合成结果。

## 打包发布

```powershell
# DLL 发布包
.\package.ps1

# 推荐的 Windows x64 独立 EXE 发布包
.\package.ps1 -Standalone
```

package.ps1 从全新 staging 目录构建，直接测试生成的 DLL 或 EXE，检查发布目录后生成 Windows ZIP、对应源码 ZIP 和 SHA256SUMS.txt，输出到 artifacts/packages/。不要直接压缩日常使用的程序或数据目录。

独立包命名为 Ripper-<版本>-win-x64-standalone.zip，DLL 包为 Ripper-<版本>-win-x64.zip，对应源码包为 Ripper-<版本>-source.zip。检查 ZIP 内的 VERSION、入口程序、runtimes/、许可证、使用说明、docs/images/ 与 source/，并核对 SHA256 清单。

根目录 VERSION 是版本号唯一来源。版本变更时同步更新 CHANGELOG.md；程序集、关于窗口及发布包名称均使用该版本。GitHub Actions 的 Windows build 工作流执行两种打包流程并上传产物。

## 主要代码位置

| 文件 | 职责 |
| --- | --- |
| src/WeChatNotes/MainForm.cs、RawBindDialog.cs | 主界面和会话选择 |
| src/WeChatNotes/RawDatabase.cs | 密钥校验、数据库页和 WAL 读取 |
| src/WeChatNotes/RawChatSource.cs、RawRecorder.cs | 只读消息解析与轮询 |
| src/WeChatNotes/Store.cs | 本地记录持久化与去重 |
| src/WeChatNotes/Exporter.cs、HtmlExporter.cs | 导出文件与离线 HTML |
| src/WeChatNotes/RawVoiceSource.cs、VoiceExporter.cs、SilkAudio.cs | 语音匹配、导出和解码 |
| native/ | SILK SDK 源码、内存解码包装器与构建脚本 |

## 实现约束

- 微信源数据库只读；不向微信进程写入，不注入代码。不将密钥或完整明文聊天库主动写入文件，仅保存所选会话记录。
- 数据库页需校验，只接受已提交 WAL 事务。修改解密或 WAL 时验证密码校验、损坏数据、提交边界和扩容覆盖。
- 保留消息原文和来源元数据；按消息标识去重，同文不同消息不得合并。修改保存或导出时验证重启恢复、消息更新、会话隔离和来源链接。
- 文件导出只匹配已有的本机附件，限制文件名和目录边界；重复、同名或语音映射歧义时不猜测。HTML 需对消息和文件名转义，保持相对链接及目录移动后的可用性。
- 模型文件由用户选择或主动下载，以固定 SHA256 校验；不随发布包分发，也不提交用户模型路径。GPU 故障回退 CPU，不丢失已完成的转写结果。
- 诊断、截图和测试使用合成数据；不提交真实消息、账号标识、聊天库、密钥、内存转储、用户导出、模型及本机配置。

贡献流程见 CONTRIBUTING.md；保留 GPL-3.0-or-later 及第三方许可证，新依赖同步更新 THIRD_PARTY_NOTICES.md。涉及微信版本适配时记录实测的完整版本号。
