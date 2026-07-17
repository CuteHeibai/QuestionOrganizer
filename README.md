# 题目整理

面向 Windows 的数学题目整理桌面应用。应用使用统一的结构化题目数据连接 AI 识别、可恢复任务和文档导出。

## 运行

```powershell
dotnet run --project src/EaxmBuilder.App
```

首次启动会进入 AI 服务配置。当前支持 OpenAI、火山豆包和兼容 OpenAI API 的服务。

## 打包

项目发布为解压即用的 ZIP 包

```powershell
dotnet publish src/EaxmBuilder.App -c Release -r win-x64 --self-contained true -o publish
tar -a -c -f QuestionOrganizer-win-x64.zip -C publish .
```

本地每次新的软件文件只输出到仓库根目录的 `publish` 文件夹。用户解压 ZIP 后直接运行 `QuestionOrganizer.exe`；卸载时删除解压目录即可。

## GeoGebra 接入

软件不内置、不分发 GeoGebra。需要 GeoGebra 绘图能力的用户可以自行下载 GeoGebra Math Apps Bundle，并任选一种方式接入：

1. 解压后把其中的 `GeoGebra` 文件夹放到 `QuestionOrganizer.exe` 同级目录；
2. 或设置环境变量 `QUESTION_ORGANIZER_GEOGEBRA_PATH`，指向 `deployggb.js`、包含 `deployggb.js` 的目录，或包含 `GeoGebra\deployggb.js` 的目录。

未接入 GeoGebra 时，外部工具/GeoGebra 图形模式会自动回退到几何图裁剪。

## 数据位置

- 应用设置：`%LOCALAPPDATA%\QuestionOrganizer\settings.json`
- API Key：使用当前 Windows 用户的 DPAPI 加密后写入设置
- 默认项目：`%USERPROFILE%\Documents\题目整理`
