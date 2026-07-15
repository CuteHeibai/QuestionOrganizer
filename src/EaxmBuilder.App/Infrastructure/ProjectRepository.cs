using System.Text.Json;
using System.Text.Json.Serialization;
using EaxmBuilder.Core;

namespace EaxmBuilder.Infrastructure;

public sealed class ProjectRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<QuestionProject> CreateAsync(string sourcePath, string outputRoot)
    {
        Directory.CreateDirectory(outputRoot);
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Question";

        var directory = GetUniqueDirectory(outputRoot, baseName);
        Directory.CreateDirectory(directory);
        var sourceName = "source" + Path.GetExtension(sourcePath).ToLowerInvariant();
        File.Copy(sourcePath, Path.Combine(directory, sourceName));

        var project = new QuestionProject
        {
            Name = baseName,
            DirectoryPath = directory,
            SourceFileName = sourceName
        };
        await SaveAsync(project);
        return project;
    }

    public async Task SaveAsync(QuestionProject project)
    {
        project.UpdatedAt = DateTimeOffset.Now;
        var path = Path.Combine(project.DirectoryPath, "project.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, project, JsonOptions);
    }

    public async Task<IReadOnlyList<QuestionProject>> GetRecentAsync(
        string outputRoot,
        int count = 8,
        IReadOnlySet<Guid>? activeProjectIds = null)
    {
        if (!Directory.Exists(outputRoot)) return [];

        var projects = new List<QuestionProject>();
        foreach (var path in Directory.EnumerateFiles(outputRoot, "project.json", SearchOption.AllDirectories))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var project = await JsonSerializer.DeserializeAsync<QuestionProject>(stream, JsonOptions);
                if (project is not null)
                {
                    project.DirectoryPath = Path.GetDirectoryName(path)!;
                    EnsureAllSteps(project);
                    if (activeProjectIds?.Contains(project.Id) != true)
                    {
                        foreach (var record in project.Steps.Values.Where(item => item.State == StepState.Running))
                        {
                            record.State = StepState.Failed;
                            record.Error = "上次运行意外中断，可以重新执行。";
                        }
                    }
                    projects.Add(project);
                }
            }
            catch (Exception) when (File.Exists(path))
            {
                // A damaged project is skipped without preventing access to other projects.
            }
        }

        return projects.OrderByDescending(project => project.UpdatedAt).Take(count).ToList();
    }

    public async Task SaveDataAsync<T>(QuestionProject project, string fileName, T data)
    {
        var path = Path.Combine(project.DirectoryPath, fileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
    }

    public async Task<T?> LoadDataAsync<T>(QuestionProject project, string fileName)
    {
        var path = Path.Combine(project.DirectoryPath, fileName);
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
    }

    private static string GetUniqueDirectory(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (!Directory.Exists(path)) return path;
        for (var suffix = 2; ; suffix++)
        {
            path = Path.Combine(root, $"{name}-{suffix}");
            if (!Directory.Exists(path)) return path;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(character => !invalid.Contains(character)).ToArray()).Trim();
    }

    private static void EnsureAllSteps(QuestionProject project)
    {
        foreach (var step in Enum.GetValues<TaskStep>())
        {
            project.Steps.TryAdd(step, new StepRecord());
        }
    }
}
