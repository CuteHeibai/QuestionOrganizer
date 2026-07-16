# 题目整理 ZIP 包

发布包只使用 ZIP，不再附带或依赖 `Install.ps1` / `Uninstall.ps1`。

## 用户使用方式

1. 解压 ZIP 到任意目录。
2. 双击 `QuestionOrganizer.exe` 运行。
3. 删除软件时，直接删除解压目录即可。

用户项目、输出文件和 AI 配置保存在系统用户目录中，不会因为删除解压目录而被自动删除。

## 打包方式

本地发布目录固定为仓库根目录下的 `publish`，不要再使用 `publish-deepseek-fix` 或其他临时目录名。

```powershell
dotnet publish src/EaxmBuilder.App -c Release -r win-x64 --self-contained true -o publish
tar -a -c -f QuestionOrganizer-win-x64.zip -C publish .
```

ZIP 内应直接包含 `QuestionOrganizer.exe` 和运行所需文件，不再包含安装/卸载脚本。

## GeoGebra 接入方式

发布包不内置、不分发 GeoGebra。需要 GeoGebra 绘图能力的用户自行下载 GeoGebra Math Apps Bundle 后接入。

支持两种路径：

1. 将 bundle 中的 `GeoGebra` 文件夹放到 `QuestionOrganizer.exe` 同级目录，形成：

   ```text
   QuestionOrganizer.exe
   GeoGebra\deployggb.js
   ```

2. 设置环境变量 `QUESTION_ORGANIZER_GEOGEBRA_PATH`，值可以是：

   ```text
   C:\path\to\deployggb.js
   C:\path\to\GeoGebra
   C:\path\to\bundle-root
   ```

未接入 GeoGebra 时，外部工具/GeoGebra 图形模式会自动回退到几何图裁剪。GeoGebra 的许可由用户自行按官方条款确认。
