using System.Diagnostics;
using EaxmBuilder.Core;

namespace EaxmBuilder.Export;

public sealed class PdfExporter : IQuestionExporter
{
    public TaskStep Step => TaskStep.PdfExport;

    public async Task ExportAsync(
        QuestionProject project,
        QuestionDocument document,
        CancellationToken cancellationToken)
    {
        var htmlPath = Path.Combine(project.DirectoryPath, "question.html");
        await File.WriteAllTextAsync(htmlPath, HtmlRenderer.Render(document), cancellationToken);

        var edgePath = FindEdge();
        if (edgePath is null)
            throw new InvalidOperationException("未找到 Microsoft Edge，无法生成 PDF。");

        var pdfPath = ProjectOutputPaths.GetFilePath(project, ".pdf");
        var startInfo = new ProcessStartInfo
        {
            FileName = edgePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--disable-gpu");
        startInfo.ArgumentList.Add("--no-pdf-header-footer");
        startInfo.ArgumentList.Add($"--print-to-pdf={pdfPath}");
        startInfo.ArgumentList.Add(new Uri(htmlPath).AbsoluteUri);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 PDF 导出进程。");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(pdfPath))
            throw new InvalidOperationException("Microsoft Edge 未能生成 PDF。");
    }

    internal static string? FindEdge()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return paths.FirstOrDefault(File.Exists);
    }
}
