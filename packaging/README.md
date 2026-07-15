# 题目整理 ZIP 包

发布包只使用 ZIP，不再附带或依赖 `Install.ps1` / `Uninstall.ps1`。

## 用户使用方式

1. 解压 ZIP 到任意目录。
2. 双击 `QuestionOrganizer.exe` 运行。
3. 删除软件时，直接删除解压目录即可。

用户项目、输出文件和 AI 配置保存在系统用户目录中，不会因为删除解压目录而被自动删除。

## 打包方式

先发布应用到 staging 目录，再把发布目录内容压缩为 ZIP。ZIP 内应直接包含 `QuestionOrganizer.exe` 和运行所需文件，不再包含安装/卸载脚本。
