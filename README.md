# 题目整理

面向 Windows 的数学题目整理桌面应用。应用使用统一的结构化题目数据连接 AI 识别、可恢复任务和文档导出。

## 运行

```powershell
dotnet run --project src/EaxmBuilder.App
```

首次启动会进入 AI 服务配置。当前支持 OpenAI、火山豆包和兼容 OpenAI API 的服务。

## 打包

项目发布为解压即用的 ZIP 包，不再使用 `Install.ps1` / `Uninstall.ps1`。

```powershell
dotnet publish src/EaxmBuilder.App -c Release -r win-x64 --self-contained true -o artifacts\QuestionOrganizer-win-x64
tar -a -c -f artifacts\QuestionOrganizer-win-x64.zip -C artifacts\QuestionOrganizer-win-x64 .
```

用户解压 ZIP 后直接运行 `QuestionOrganizer.exe`；卸载时删除解压目录即可。

## 数据位置

- 应用设置：`%LOCALAPPDATA%\QuestionOrganizer\settings.json`
- API Key：使用当前 Windows 用户的 DPAPI 加密后写入设置
- 默认项目：`%USERPROFILE%\Documents\题目整理`
