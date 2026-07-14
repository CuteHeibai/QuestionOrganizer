# Project UI Rules

- 在项目中，任何可视化界面都必须与应用当前主题匹配。
- 新增或修改窗口、对话框、提示层、菜单和控件时，必须复用现有的颜色、字体、圆角、间距、图标和轻量动画规范。
- 不要使用与应用视觉语言不一致的原始系统消息框或默认 WPF 控件样式；应用内提示应优先使用主题化组件。操作系统负责的文件选择器等原生界面除外。
- 新界面必须在常用 DPI 和窗口最小尺寸下检查文字截断、重叠和内容遮挡。

# 题目整理 Project Requirements

## Product Positioning

- 本项目是 Windows 原生桌面软件，不是网页应用；不要把功能改造成 Web 版或依赖浏览器作为主界面。
- 软件名称是“题目整理”，定位为利用 AI 自动整理数学题目的专业桌面软件。
- 首页保持干净，不在首页突出软件名称；软件名称只应出现在标题栏、设置、关于等位置。
- 首次启动必须是引导式欢迎流程：欢迎使用 → 选择 AI 提供商 → 填写并测试 API Key/Base URL/模型 → 完成后进入主界面。不要让用户感觉是在配置一个 API 工具。

## Visual And Interaction Style

- 设计语言参考 ChatGPT Desktop、Codex、Notion、Raycast：极简、留白、大圆角、弱边框、轻动画。
- 禁止 Office Ribbon 风格、复杂菜单、拟物图标、Emoji 图标和花哨配色。
- 默认浅色主题；深色和跟随系统只做架构预留。
- 图标使用 Material Symbols Rounded 或 Fluent Icons，并保持全局风格统一。
- 动画要轻：按钮 hover 约 150ms，淡入约 200ms，页面切换约 300ms；不要弹窗乱飞。

## Architecture Boundaries

- 保持模块解耦：GUI、Task Manager、AI Engine、Export Engine、项目管理/OCR 逻辑不要混写。
- AI 输出必须先落到统一结构化中间数据，再由导出模块生成 Word、PDF、LaTeX、SVG、JSON；不要让 AI 直接生成最终文档。
- 每道题都是可持久化项目，可重新打开继续处理，不是一次性转换工具。
- 任务必须可恢复：OCR、公式识别、图形重绘、Word、PDF、LaTeX、JSON 等步骤失败后应能单独重试，不需要整条流程重来。
- 处理任务不能阻塞界面；切换页面后任务仍应继续，未来要支持多个项目并行。

## AI Providers And Prompts

- AI Provider 采用可扩展/插件化思路。当前已支持或重点支持 OpenAI、OpenAI Compatible、火山豆包；不要重新接入 DeepSeek，除非它能完整支持本项目所需的图片/PDF 输入、OCR、公式识别和图形重绘链路。
- 自定义兼容接口需要兼容常见 OpenAI chat/completions 返回结构；OpenAI 官方/兼容 Responses API 需要兼容流式和非流式响应。
- AI 请求可能很慢，尤其公式识别和图形重绘；不要使用过短超时。超时后要给出明确提示，并允许单步重试。
- 内置提示词应保留用户要求：用 LaTeX 整理数学题到 Word，正文宋体、五号、不加粗、二倍行距、段前段后无空格；图形需要重新绘制，图中文字使用宋体。
- “AI 要求”是项目级附加要求，必须持久化，并在重新执行相关步骤时生效。

## Import, Project, And Output Behavior

- 上传区应支持拖拽、选择文件、粘贴图片；支持 PNG、JPG、PDF。
- 左侧项目入口应能列出之前处理过的多个项目。
- 用户可以选择输出哪些内容，例如只输出 Word；也可以把识别后的题目追加到已有 Word 文档。
- 输出目录结构应保持清晰，典型文件包括 `source.png`、`question.docx`、`question.pdf`、`question.tex`、`figure1.svg`、`metadata.json`。
- 日志不要遮挡主界面；优先使用右侧栏或不干扰内容的区域展示任务日志。

## Word, PDF, Formula, And Figure Quality

- Word 必须能被 Microsoft Word 正常打开；导出后避免生成损坏 OOXML。
- Word 正文默认宋体、五号、不加粗、二倍行距、段前 0、段后 0。
- 公式应尽量使用 Office 可编辑公式或稳定的 OMML 表达；不要以截图或普通图片替代公式。
- PDF 不应出现原始 LaTeX 代码如 `$x^2$`、`y_1`，公式也不要被拆成大块独立段落导致文字与公式间距过大。
- HTML/PDF 底稿中，行内公式应嵌回自然段；避免 `<div class="formula">$...$</div>` 这种导致错排的结构。
- 几何图、函数图、绳结图等必须重新绘制为 SVG；不要嵌入原图截图。绳结/交叉线要保留拓扑关系。
- 当没有选择自定义 Word 模板时，必须使用内置默认模板；打包时要保证默认模板不会缺失。

## Verification And Packaging

- 修改导出逻辑后必须至少跑烟雾测试，重点检查 DOCX 可打开性、公式结构、PDF/HTML 是否残留原始 LaTeX、输出选择和追加 Word。
- 能做视觉 QA 时，渲染 PDF/DOCX 页面检查是否有文字遮挡、截断、公式错排或图形缺失。
- 打包安装包时，不要为了覆盖发布目录而关闭用户正在运行的软件，除非用户明确允许；应发布到新的 staging 目录再打包。
- 安装包应尽量自包含，减少用户环境依赖；安装到用户级目录，创建桌面和开始菜单快捷方式。
- 卸载器应删除安装目录和快捷方式，但不要删除用户项目、输出文件、AI 配置或其他数据。

## Repository Upload And Filtering

- 上传或推送仓库前，必须先检查 `.gitignore` 和待提交文件，过滤本地运行产物、安装包、渲染 QA 输出、临时目录和个人上下文。
- 不要上传 `artifacts/`、`tmp/`、`bin/`、`obj/`、`.vs/`、`.agents/`、`.codex/`、日志、安装包、临时渲染图片或本地测试输出。
- 不要上传 API Key、Token、`.env*`、`settings.json`、用户项目数据、用户输出目录或任何包含个人路径/凭据的配置。
- 可以上传源码、测试、README、AGENTS、项目文件、默认模板等项目运行必需且不含私密信息的文件。
- 推送前用关键词扫描检查 `sk-`、`api key`、`token`、`password`、`secret` 等敏感内容；命中时必须人工判断并移除不应公开的内容。
