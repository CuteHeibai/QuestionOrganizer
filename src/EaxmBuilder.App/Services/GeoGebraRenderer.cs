using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EaxmBuilder.Core;
using EaxmBuilder.Export;

namespace EaxmBuilder.Services;

internal static class GeoGebraRenderer
{
    private const int Width = 720;
    private const int Height = 480;

    public static async Task<FigureDocument?> RenderAsync(
        QuestionProject project,
        FigureDocument figure,
        CancellationToken cancellationToken)
    {
        if (figure.GeoGebraCommands.Count == 0) return null;

        var commandPath = Path.Combine(project.DirectoryPath, $"{SanitizeFigureId(figure.Id)}.geogebra.txt");
        await File.WriteAllLinesAsync(commandPath, figure.GeoGebraCommands, cancellationToken);

        var deployPath = ResolveDeployScriptPath();
        var edgePath = PdfExporter.FindEdge();
        if (deployPath is null || edgePath is null) return null;

        var token = Guid.NewGuid().ToString("N");
        var htmlPath = Path.Combine(Path.GetTempPath(), $"QuestionOrganizer-GeoGebra-{token}.html");
        var pngPath = Path.Combine(Path.GetTempPath(), $"QuestionOrganizer-GeoGebra-{token}.png");
        try
        {
            await File.WriteAllTextAsync(htmlPath, CreateHtml(deployPath, figure.GeoGebraCommands),
                new UTF8Encoding(false), cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = edgePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--disable-gpu");
            startInfo.ArgumentList.Add("--hide-scrollbars");
            startInfo.ArgumentList.Add("--allow-file-access-from-files");
            startInfo.ArgumentList.Add("--virtual-time-budget=9000");
            startInfo.ArgumentList.Add($"--window-size={Width},{Height}");
            startInfo.ArgumentList.Add($"--screenshot={pngPath}");
            startInfo.ArgumentList.Add(new Uri(htmlPath).AbsoluteUri);

            using var process = Process.Start(startInfo);
            if (process is null) return null;
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(pngPath) || IsBlankPng(pngPath)) return null;

            var bounds = ReadImageBounds(pngPath);
            var bytes = await File.ReadAllBytesAsync(pngPath, cancellationToken);
            return new FigureDocument
            {
                Id = figure.Id,
                Description = "内嵌 GeoGebra 绘制",
                GeoGebraCommands = figure.GeoGebraCommands,
                Svg = $"""
                    <svg xmlns="http://www.w3.org/2000/svg" viewBox="{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}" overflow="hidden">
                      <image href="data:image/png;base64,{Convert.ToBase64String(bytes)}" x="0" y="0" width="{bounds.SourceWidth}" height="{bounds.SourceHeight}" preserveAspectRatio="none"/>
                    </svg>
                    """
            };
        }
        finally
        {
            TryDelete(htmlPath);
            TryDelete(pngPath);
        }
    }

    private static string CreateHtml(string deployPath, IReadOnlyList<string> commands)
    {
        var scriptUri = new Uri(deployPath).AbsoluteUri;
        var commandsJson = JsonSerializer.Serialize(commands);
        return $$"""
            <!doctype html>
            <meta charset="utf-8">
            <style>
            html,body{margin:0;width:{{Width}}px;height:{{Height}}px;overflow:hidden;background:#fff}
            #ggb{width:{{Width}}px;height:{{Height}}px}
            .logoPanel,.ggbLogo,.ggb_preview,.applet_scaler + div,[class*="logo"],[class*="Logo"]{display:none!important;visibility:hidden!important;opacity:0!important}
            </style>
            <div id="ggb"></div>
            <script src="{{WebUtility.HtmlEncode(scriptUri)}}"></script>
            <script>
            const commands = {{commandsJson}};
            const params = {
              appName: "geometry",
              id: "ggbApplet",
              width: {{Width}},
              height: {{Height}},
              showToolBar: false,
              showMenuBar: false,
              showAlgebraInput: false,
              showResetIcon: false,
              enableRightClick: false,
              enableLabelDrags: false,
              enableShiftDragZoom: false,
              useBrowserForJS: true,
              appletOnLoad: function(api) {
                try {
                  if (api.setAxesVisible) api.setAxesVisible(false, false);
                  if (api.setGridVisible) api.setGridVisible(false);
                  for (const command of commands) {
                    if (command && command.trim()) api.evalCommand(command);
                  }
                  normalizeStyle(api);
                  if (api.setCoordSystem) api.setCoordSystem(-1, 9, -1, 6);
                  if (api.setMode) api.setMode(0);
                  document.body.dataset.ready = "1";
                } catch (error) {
                  document.body.dataset.error = String(error && error.message || error);
                }
              }
            };
            function normalizeStyle(api) {
              const rawNames = api.getAllObjectNames ? api.getAllObjectNames() : [];
              const names = Array.isArray(rawNames) ? rawNames :
                (typeof rawNames === "string" ? rawNames.split(",") : Array.from(rawNames || []));
              for (const name of names) {
                try {
                  const type = api.getObjectType ? String(api.getObjectType(name)).toLowerCase() : "";
                  if (api.setLabelVisible) api.setLabelVisible(name, false);
                  if (api.setColor) api.setColor(name, 0, 0, 0);
                  if (api.setLineThickness && /line|segment|ray|vector|conic|circle|arc|polygon|function/.test(type)) {
                    api.setLineThickness(name, 5);
                  }
                  if (api.setPointSize && type.includes("point")) {
                    api.setPointSize(name, 1);
                  }
                  if (api.setVisible && type.includes("angle")) {
                    api.setVisible(name, false);
                  }
                } catch (_) {
                  // Keep rendering even if a GeoGebra object does not support a style method.
                }
              }
            }
            new GGBApplet(params, true).inject("ggb");
            </script>
            """;
    }

    private static string? ResolveDeployScriptPath()
    {
        foreach (var path in CandidateDeployScriptPaths())
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static IEnumerable<string> CandidateDeployScriptPaths()
    {
        var configuredPath = Environment.GetEnvironmentVariable("QUESTION_ORGANIZER_GEOGEBRA_PATH");
        foreach (var path in ExpandGeoGebraPath(configuredPath))
            yield return path;

        foreach (var path in ExpandGeoGebraPath(Path.Combine(AppContext.BaseDirectory, "GeoGebra")))
            yield return path;

        foreach (var path in ExpandGeoGebraPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "EaxmBuilder.App", "Assets", "GeoGebra")))
            yield return path;
    }

    private static IEnumerable<string> ExpandGeoGebraPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) yield break;
        yield return path;
        yield return Path.Combine(path, "deployggb.js");
        yield return Path.Combine(path, "GeoGebra", "deployggb.js");
    }

    private static bool IsBlankPng(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = Math.Max(1, source.PixelWidth);
        var height = Math.Max(1, source.PixelHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] < 16) continue;
            if (pixels[index] < 245 || pixels[index + 1] < 245 || pixels[index + 2] < 245) return false;
        }
        return true;
    }

    private static SourceImageBounds ReadImageBounds(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = Math.Max(1, source.PixelWidth);
        var height = Math.Max(1, source.PixelHeight);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var dark = new bool[width * height];
        var fullMinX = width;
        var fullMinY = height;
        var fullMaxX = -1;
        var fullMaxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var index = row + x * 4;
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                var alpha = pixels[index + 3];
                if (alpha < 16 || red > 245 && green > 245 && blue > 245) continue;
                dark[y * width + x] = true;
                fullMinX = Math.Min(fullMinX, x);
                fullMinY = Math.Min(fullMinY, y);
                fullMaxX = Math.Max(fullMaxX, x);
                fullMaxY = Math.Max(fullMaxY, y);
            }
        }

        if (fullMaxX < 0) return new SourceImageBounds(0, 0, width, height, width, height);

        var largest = FindLargestComponentBounds(dark, width, height);
        var useLargest = largest.Area >= Math.Max(32, width * height / 2000) &&
                         largest.Width >= width / 10 &&
                         largest.Height >= height / 12;
        var minX = useLargest ? largest.X : fullMinX;
        var minY = useLargest ? largest.Y : fullMinY;
        var maxX = useLargest ? largest.X + largest.Width - 1 : fullMaxX;
        var maxY = useLargest ? largest.Y + largest.Height - 1 : fullMaxY;

        const int padding = 18;
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(width - 1, maxX + padding);
        maxY = Math.Min(height - 1, maxY + padding);
        return new SourceImageBounds(minX, minY, maxX - minX + 1, maxY - minY + 1, width, height);
    }

    private static ComponentBounds FindLargestComponentBounds(bool[] dark, int width, int height)
    {
        var visited = new bool[dark.Length];
        var queue = new Queue<int>();
        var best = new ComponentBounds(0, 0, width, height, 0);
        for (var start = 0; start < dark.Length; start++)
        {
            if (!dark[start] || visited[start]) continue;

            var area = 0;
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var x = current % width;
                var y = current / width;
                area++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                Enqueue(x - 1, y);
                Enqueue(x + 1, y);
                Enqueue(x, y - 1);
                Enqueue(x, y + 1);
            }

            if (area > best.Area)
                best = new ComponentBounds(minX, minY, maxX - minX + 1, maxY - minY + 1, area);
        }
        return best;

        void Enqueue(int x, int y)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            var index = y * width + x;
            if (!dark[index] || visited[index]) return;
            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static string SanitizeFigureId(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "figure" : safe;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary files are best-effort cleanup.
        }
    }

    private sealed record SourceImageBounds(int X, int Y, int Width, int Height, int SourceWidth, int SourceHeight);
    private sealed record ComponentBounds(int X, int Y, int Width, int Height, int Area);
}
