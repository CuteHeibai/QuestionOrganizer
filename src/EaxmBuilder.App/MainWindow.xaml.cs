using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using EaxmBuilder.AI;
using EaxmBuilder.Core;
using EaxmBuilder.Controls;
using EaxmBuilder.Export;
using EaxmBuilder.Infrastructure;
using EaxmBuilder.Services;
using Microsoft.Win32;

namespace EaxmBuilder;

public partial class MainWindow : Window
{
    private sealed record ProviderPreset(
        AiProviderKind Kind,
        string DisplayName,
        string DefaultBaseUrl,
        string DefaultModel,
        string[] ModelSuggestions,
        string ModelHint);

    private static readonly Dictionary<AiProviderKind, ProviderPreset> ProviderPresets = new()
    {
        [AiProviderKind.OpenAi] = new(
            AiProviderKind.OpenAi,
            "OpenAI",
            "https://api.openai.com/v1",
            "gpt-5.5",
            ["gpt-5.6", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.3-codex-spark", "codex-mini-latest", "gpt-4.1", "gpt-4.1-mini", "gpt-4o"],
            "推荐 gpt-5.6 / gpt-5.5 这类视觉和长上下文更强的模型；也可以直接输入服务商模型 ID。"),
        [AiProviderKind.OpenAiCompatible] = new(
            AiProviderKind.OpenAiCompatible,
            "OpenAI Compatible",
            "https://api.openai.com/v1",
            "gpt-5.5",
            ["gpt-5.6", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.3-codex-spark", "codex-mini-latest", "gpt-4.1", "gpt-4.1-mini", "gpt-4o", "qwen-vl-max", "qwen-plus", "moonshot-v1-128k", "claude-sonnet-4", "doubao-seed-1-6-vision-250815"],
            "请选择兼容服务支持的模型，或输入服务商文档中的模型 ID。"),
        [AiProviderKind.Doubao] = new(
            AiProviderKind.Doubao,
            "火山豆包",
            "https://ark.cn-beijing.volces.com/api/v3",
            "doubao-seed-2-1-pro-260628",
            ["doubao-seed-2-1-pro-260628", "doubao-seed-2-1-turbo-260628", "doubao-seed-evolving", "doubao-seed-1-6-vision-250815", "doubao-1-5-thinking-vision-pro-250428", "doubao-1-5-vision-pro-250328", "doubao-1-5-pro-32k-250115", "doubao-1-5-lite-32k-250115"],
            "题目图片处理建议选择支持多模态理解的模型；火山方舟中也可以直接输入你创建的推理接入点 ID。")
    };

    private readonly SettingsStore _settingsStore = new();
    private readonly ProjectRepository _projectRepository = new();
    private AppSettings _settings = new();
    private QuestionProject? _currentProject;
    private FrameworkElement? _activeShellPage;
    private readonly Dictionary<Guid, CancellationTokenSource> _runningProjects = new();
    private bool _logExpanded;
    private bool _isClipboardImporting;
    private bool _isPopulatingSettings;
    private bool _isPopulatingProjectOptions;
    private AiProviderKind _activeSettingsProvider = AiProviderKind.OpenAi;
    private PreviewFile? _activePreviewFile;
    private QuestionProject? _renamingProject;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AboutVersionText.Text = "版本 " + GetAppVersionText();
        WindowState = WindowState.Maximized;
        ApplyMaximizedWorkArea();
        _settings = await _settingsStore.LoadAsync();
        var providerMigrated = NormalizeUnsupportedProvider();
        if (providerMigrated) await _settingsStore.SaveAsync(_settings);
        PopulateSettingsFields();
        ConfigureModelSuggestions(ModelBox, ModelHintText, AiProviderKind.OpenAi, ModelBox.Text);

        if (_settings.OnboardingCompleted)
        {
            ShowShell();
            await ShowHomeAsync();
            if (providerMigrated)
            {
                ShowSettingsPage();
                SetFeedback(SettingsFeedback, "之前选择的 AI 提供商已移除，请重新选择并保存 AI 设置。", false);
            }
            else if (string.IsNullOrWhiteSpace(_settingsStore.ReadApiKey(_settings)))
            {
                ShowSettingsPage();
                SetFeedback(SettingsFeedback, "AI 配置已失效，请重新填写 API Key。", false);
            }
        }
        else
        {
            OnboardingRoot.Visibility = Visibility.Visible;
            ShellRoot.Visibility = Visibility.Collapsed;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && NoticeOverlay.Visibility == Visibility.Visible)
        {
            CloseNotice();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AiInstructionsOverlay.Visibility == Visibility.Visible)
        {
            CloseAiInstructionsOverlay();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && RenameProjectOverlay.Visibility == Visibility.Visible)
        {
            CloseRenameProjectOverlay();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V &&
            (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            HomePage.Visibility == Visibility.Visible &&
            AiInstructionsOverlay.Visibility != Visibility.Visible &&
            Keyboard.FocusedElement is not TextBox &&
            Keyboard.FocusedElement is not PasswordBox &&
            !_isClipboardImporting)
        {
            e.Handled = true;
            await ImportFromClipboardAsync();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void StartOnboarding_Click(object sender, RoutedEventArgs e) =>
        ShowOnboardingPage(ProviderPage, WelcomePage);

    private void ProviderContinue_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectedProviderToFields();
        ShowOnboardingPage(CredentialsPage, ProviderPage);
    }

    private void CredentialsBack_Click(object sender, RoutedEventArgs e) =>
        ShowOnboardingPage(ProviderPage, CredentialsPage);

    private void Provider_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ProviderSummaryText is null) return;
        ApplySelectedProviderToFields();
    }

    private void ApplySelectedProviderToFields()
    {
        var kind = GetSelectedOnboardingProvider();
        var preset = GetProviderPreset(kind);
        var profile = GetProviderProfile(kind);
        ProviderSummaryText.Text = preset.DisplayName;
        BaseUrlBox.Text = string.IsNullOrWhiteSpace(profile.BaseUrl) ? preset.DefaultBaseUrl : profile.BaseUrl;
        ModelBox.Text = string.IsNullOrWhiteSpace(profile.Model) ? preset.DefaultModel : profile.Model;
        ApiKeyBox.Password = string.Empty;
        ConfigureModelSuggestions(ModelBox, ModelHintText, kind, ModelBox.Text);
        ConnectionFeedback.Text = string.Empty;
        CredentialsContinueButton.IsEnabled = false;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        CredentialsContinueButton.IsEnabled = false;
        SetFeedback(ConnectionFeedback, "正在连接...", null);
        try
        {
            var candidate = GetOnboardingSettings();
            var apiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Password)
                ? _settingsStore.ReadApiKey(candidate, candidate.Provider)
                : ApiKeyBox.Password;
            using var provider = (IDisposable)new AiProviderFactory(_settingsStore).Create(candidate, apiKey);
            await ((IAiProvider)provider).TestConnectionAsync();
            SetFeedback(ConnectionFeedback, "连接成功", true);
            CredentialsContinueButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            SetFeedback(ConnectionFeedback, exception.Message, false);
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private async void CredentialsContinue_Click(object sender, RoutedEventArgs e)
    {
        var candidate = GetOnboardingSettings();
        var profile = candidate.ProviderProfiles[candidate.Provider];
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            profile.ProtectedApiKey = WindowsDataProtector.Protect(ApiKeyBox.Password);
        if (string.IsNullOrWhiteSpace(_settingsStore.ReadApiKey(candidate, candidate.Provider)))
        {
            SetFeedback(ConnectionFeedback, "请填写 API Key。", false);
            return;
        }
        MirrorActiveProvider(candidate);
        candidate.OutputDirectory = _settings.OutputDirectory;
        _settings = candidate;
        await _settingsStore.SaveAsync(_settings);
        ShowOnboardingPage(CompletePage, CredentialsPage);
    }

    private AppSettings GetOnboardingSettings()
    {
        var kind = GetSelectedOnboardingProvider();
        var candidate = CloneSettings(_settings);
        candidate.Provider = kind;
        candidate.OnboardingCompleted = false;
        var previous = GetProviderProfile(kind);
        candidate.ProviderProfiles[kind] = new AiProviderSettings
        {
            ProtectedApiKey = previous.ProtectedApiKey,
            BaseUrl = BaseUrlBox.Text.Trim(),
            Model = ModelBox.Text.Trim()
        };
        MirrorActiveProvider(candidate);
        return candidate;
    }

    private AiProviderKind GetSelectedOnboardingProvider()
    {
        if (OpenAiOption.IsChecked == true) return AiProviderKind.OpenAi;
        if (DoubaoOption.IsChecked == true) return AiProviderKind.Doubao;
        return AiProviderKind.OpenAiCompatible;
    }

    private static ProviderPreset GetProviderPreset(AiProviderKind kind) =>
        ProviderPresets.TryGetValue(kind, out var preset)
            ? preset
            : ProviderPresets[AiProviderKind.OpenAiCompatible];

    private AiProviderSettings GetProviderProfile(AiProviderKind kind)
    {
        var preset = GetProviderPreset(kind);
        if (!_settings.ProviderProfiles.TryGetValue(kind, out var profile))
        {
            profile = new AiProviderSettings();
            _settings.ProviderProfiles[kind] = profile;
        }

        return new AiProviderSettings
        {
            ProtectedApiKey = profile.ProtectedApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? preset.DefaultBaseUrl : profile.BaseUrl,
            Model = string.IsNullOrWhiteSpace(profile.Model) ? preset.DefaultModel : profile.Model
        };
    }

    private void CaptureSettingsProviderProfile(AiProviderKind kind)
    {
        if (SettingsBaseUrlBox is null || SettingsModelBox is null || SettingsApiKeyBox is null) return;
        var previous = GetProviderProfile(kind);
        _settings.ProviderProfiles[kind] = new AiProviderSettings
        {
            ProtectedApiKey = string.IsNullOrWhiteSpace(SettingsApiKeyBox.Password)
                ? previous.ProtectedApiKey
                : WindowsDataProtector.Protect(SettingsApiKeyBox.Password),
            BaseUrl = SettingsBaseUrlBox.Text.Trim(),
            Model = SettingsModelBox.Text.Trim()
        };
    }

    private static AppSettings CloneSettings(AppSettings settings) => new()
    {
        OnboardingCompleted = settings.OnboardingCompleted,
        Provider = settings.Provider,
        ProtectedApiKey = settings.ProtectedApiKey,
        BaseUrl = settings.BaseUrl,
        Model = settings.Model,
        ProviderProfiles = settings.ProviderProfiles.ToDictionary(
            item => item.Key,
            item => new AiProviderSettings
            {
                ProtectedApiKey = item.Value.ProtectedApiKey,
                BaseUrl = item.Value.BaseUrl,
                Model = item.Value.Model
            }),
            OutputDirectory = settings.OutputDirectory,
            FinalOutputDirectory = settings.FinalOutputDirectory,
            Theme = settings.Theme,
            WordTemplatePath = settings.WordTemplatePath
    };

    private static void MirrorActiveProvider(AppSettings settings)
    {
        if (!settings.ProviderProfiles.TryGetValue(settings.Provider, out var profile)) return;
        settings.ProtectedApiKey = profile.ProtectedApiKey;
        settings.BaseUrl = profile.BaseUrl;
        settings.Model = profile.Model;
    }

    private bool NormalizeUnsupportedProvider()
    {
        if (ProviderPresets.ContainsKey(_settings.Provider)) return false;

        var preset = ProviderPresets[AiProviderKind.OpenAiCompatible];
        _settings.Provider = preset.Kind;
        _settings.BaseUrl = preset.DefaultBaseUrl;
        _settings.Model = preset.DefaultModel;
        _settings.ProtectedApiKey = string.Empty;
        return true;
    }

    private static bool ShouldReplaceModel(string currentValue, AiProviderKind selectedKind)
    {
        if (string.IsNullOrWhiteSpace(currentValue)) return true;
        return ProviderPresets.Values
            .Where(preset => preset.Kind != selectedKind)
            .Any(preset => preset.ModelSuggestions.Contains(currentValue, StringComparer.OrdinalIgnoreCase));
    }

    private async void FinishOnboarding_Click(object sender, RoutedEventArgs e)
    {
        _settings.OnboardingCompleted = true;
        await _settingsStore.SaveAsync(_settings);
        PopulateSettingsFields();
        ShowShell();
        await ShowHomeAsync();
    }

    private void ShowOnboardingPage(FrameworkElement next, FrameworkElement current)
    {
        current.Visibility = Visibility.Collapsed;
        next.Visibility = Visibility.Visible;
        FadeIn(next);
    }

    private void ShowShell()
    {
        OnboardingRoot.Visibility = Visibility.Collapsed;
        ShellRoot.Visibility = Visibility.Visible;
    }

    private async void Home_Click(object sender, RoutedEventArgs e) => await ShowHomeAsync();

    private async Task ShowHomeAsync()
    {
        NavigateShell(HomePage);
        await LoadRecentAsync();
    }

    private async void ProjectNav_Click(object sender, RoutedEventArgs e) => await ShowProjectsAsync();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettingsPage();

    private void ShowSettingsPage()
    {
        PopulateSettingsFields();
        NavigateShell(SettingsPage);
    }

    private void About_Click(object sender, RoutedEventArgs e) => NavigateShell(AboutPage);

    private void NavigateShell(FrameworkElement page)
    {
        foreach (var candidate in new[] { HomePage, ProjectsPage, ProjectPage, SettingsPage, AboutPage })
            candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;
        _activeShellPage = page;
        FadeIn(page);
    }

    private static void FadeIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择题目文件",
            Filter = "题目文件|*.png;*.jpg;*.jpeg;*.pdf|PNG 图片|*.png|JPG 图片|*.jpg;*.jpeg|PDF 文件|*.pdf"
        };
        if (dialog.ShowDialog(this) == true) await ImportProjectAsync(dialog.FileName);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Background = e.Effects == DragDropEffects.Copy
            ? new SolidColorBrush(Color.FromRgb(244, 244, 244))
            : new SolidColorBrush(Color.FromRgb(250, 250, 250));
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) =>
        DropZone.Background = new SolidColorBrush(Color.FromRgb(250, 250, 250));

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.Background = new SolidColorBrush(Color.FromRgb(250, 250, 250));
        if (!HasSupportedFile(e.Data)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await ImportProjectAsync(files[0]);
    }

    private static bool HasSupportedFile(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = data.GetData(DataFormats.FileDrop) as string[];
        return files is { Length: > 0 } && IsSupported(files[0]);
    }

    private static bool IsSupported(string path) =>
        new[] { ".png", ".jpg", ".jpeg", ".pdf" }.Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private async Task ImportProjectAsync(string sourcePath)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Length > 25 * 1024 * 1024)
            {
                ShowNotice("文件过大", "文件不能超过 25 MB。");
                return;
            }

        _currentProject = await _projectRepository.CreateAsync(
            sourcePath,
            _settings.OutputDirectory,
            GetDefaultFinalOutputRoot());
            AddLog("已创建项目");
            ShowProject(_currentProject);
        }
        catch (Exception exception)
        {
            ShowNotice("无法创建项目", exception.Message);
        }
    }

    private async Task ImportFromClipboardAsync()
    {
        _isClipboardImporting = true;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var path = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsSupported);
                if (path is not null)
                {
                    await ImportProjectAsync(path);
                    return;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is null) return;
                var temporaryPath = Path.Combine(
                    Path.GetTempPath(), $"QuestionOrganizer-{Guid.NewGuid():N}.png");
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    await using (var stream = File.Create(temporaryPath))
                        encoder.Save(stream);
                    await ImportProjectAsync(temporaryPath);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                return;
            }

            ShowNotice("无法粘贴", "剪贴板中没有可用的图片或题目文件。");
        }
        catch (Exception exception)
        {
            ShowNotice("无法从剪贴板导入", exception.Message);
        }
        finally
        {
            _isClipboardImporting = false;
        }
    }

    private async Task ShowProjectsAsync()
    {
        NavigateShell(ProjectsPage);
        await LoadProjectsAsync();
    }

    private async Task LoadProjectsAsync()
    {
        var projects = await _projectRepository.GetRecentAsync(
            _settings.OutputDirectory,
            200,
            _runningProjects.Keys.ToHashSet());
        AllProjectsList.ItemsSource = projects;
        NoProjectsText.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshProjects_Click(object sender, RoutedEventArgs e) => await LoadProjectsAsync();

    private void AllProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuestionProject project }) ShowProject(project);
    }

    private void OpenProjectFolderFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuestionProject project })
            OpenFolder(project.DirectoryPath);
    }

    private void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuestionProject project }) return;
        if (_runningProjects.ContainsKey(project.Id))
        {
            ShowNotice("项目正在处理", "请在项目处理结束后再改名。");
            return;
        }

        _renamingProject = project;
        RenameProjectNameBox.Text = project.Name;
        RenameProjectNameBox.CaretIndex = RenameProjectNameBox.Text.Length;
        RenameProjectOverlay.Visibility = Visibility.Visible;
        FadeIn(RenameProjectOverlay);
        RenameProjectNameBox.Focus();
        RenameProjectNameBox.SelectAll();
    }

    private async void SaveRenameProject_Click(object sender, RoutedEventArgs e)
    {
        await SaveRenameProjectAsync();
    }

    private async Task SaveRenameProjectAsync()
    {
        if (_renamingProject is null) return;
        try
        {
            await _projectRepository.RenameAsync(_renamingProject, RenameProjectNameBox.Text);
            if (_currentProject?.Id == _renamingProject.Id)
            {
                _currentProject = _renamingProject;
                ProjectTitle.Text = _renamingProject.Name;
                PopulateProjectOutputOptions(_renamingProject);
                RefreshProjectView(_renamingProject);
            }
            CloseRenameProjectOverlay();
            await LoadProjectsAsync();
            await LoadRecentAsync();
        }
        catch (Exception exception)
        {
            ShowNotice("无法改名项目", exception.Message);
        }
    }

    private void RenameProjectNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SaveRenameProjectAsync();
    }

    private void CancelRenameProject_Click(object sender, RoutedEventArgs e) => CloseRenameProjectOverlay();

    private void CloseRenameProjectOverlay()
    {
        RenameProjectOverlay.Visibility = Visibility.Collapsed;
        RenameProjectNameBox.Clear();
        _renamingProject = null;
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuestionProject project }) return;
        await DeleteProjectsAsync([project]);
    }

    private async void DeleteSelectedProjects_Click(object sender, RoutedEventArgs e)
    {
        var selected = AllProjectsList.SelectedItems
            .OfType<QuestionProject>()
            .ToList();
        if (selected.Count == 0)
        {
            ShowNotice("没有选中项目", "请先在项目总览中选中一个或多个项目。");
            return;
        }
        await DeleteProjectsAsync(selected);
    }

    private async Task DeleteProjectsAsync(IReadOnlyList<QuestionProject> projects)
    {
        var deletable = projects
            .Where(project => !_runningProjects.ContainsKey(project.Id))
            .ToList();
        var skipped = projects.Count - deletable.Count;
        foreach (var project in deletable)
            await _projectRepository.DeleteAsync(project);
        if (_currentProject is not null && deletable.Any(project => project.Id == _currentProject.Id))
            _currentProject = null;
        await LoadProjectsAsync();
        await LoadRecentAsync();
        ShowNotice("项目已删除", skipped == 0
            ? $"已将 {deletable.Count} 个项目移入回收站。"
            : $"已将 {deletable.Count} 个项目移入回收站；{skipped} 个正在处理的项目已跳过。");
    }

    private void ShowProject(QuestionProject project)
    {
        ApplyDefaultFinalOutput(project);
        _currentProject = project;
        ProjectTitle.Text = project.Name;
        ProjectPath.Text = project.DirectoryPath;
        AiInstructionsButton.Content = string.IsNullOrWhiteSpace(project.AiInstructions)
            ? "AI 要求"
            : "AI 要求（已设置）";
        PopulateProjectOutputOptions(project);
        RefreshProjectView(project);
        RefreshPreviewFiles(project, forceSelect: true);
        UpdateAiActivitySummary();
        NavigateShell(ProjectPage);
    }

    private void RefreshPreviewFiles(QuestionProject project, bool forceSelect = false)
    {
        var files = BuildPreviewFiles(project);
        PreviewFilesList.ItemsSource = files;
        var previous = files.FirstOrDefault(item =>
            _activePreviewFile is not null &&
            string.Equals(item.Path, _activePreviewFile.Path, StringComparison.OrdinalIgnoreCase));
        var preferred = files.FirstOrDefault(item => item.Label == "PDF")
                        ?? files.FirstOrDefault(item => item.Label == "HTML")
                        ?? files.FirstOrDefault();
        SelectPreviewFile(forceSelect ? preferred : previous ?? _activePreviewFile ?? preferred);
    }

    private void RefreshProjectView(QuestionProject project)
    {
        TaskStepsList.ItemsSource = GetVisibleTaskSteps(project)
            .Select(step => new TaskStepView(step, project.Steps[step]))
            .ToList();
        RefreshPreviewFiles(project);
        if (project.IsComplete)
        {
            RunProjectButton.Content = "再次生成";
            RunProjectButton.IsEnabled = true;
        }
        else if (_runningProjects.ContainsKey(project.Id))
        {
            RunProjectButton.Content = "处理中";
            RunProjectButton.IsEnabled = false;
        }
        else
        {
            RunProjectButton.Content = project.CompletedStepCount == 0 ? "开始处理" : "继续处理";
            RunProjectButton.IsEnabled = true;
        }
        var isRunning = _runningProjects.ContainsKey(project.Id);
        OutputConfigCard.Visibility = isRunning ? Visibility.Collapsed : Visibility.Visible;
        TaskProgressCard.Visibility = isRunning || HasStartedStep(project)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshGenerationSummary(project, isRunning);
        UpdateAiActivitySummary();
    }

    private static IReadOnlyList<PreviewFile> BuildPreviewFiles(QuestionProject project)
    {
        var files = new List<PreviewFile>();
        if (File.Exists(project.SourcePath))
            files.Add(new PreviewFile("源文件", project.SourcePath, PreviewKind.Source));
        if (project.OutputSelection.Pdf)
            Add("PDF", ProjectOutputPaths.GetFilePath(project, ".pdf"), PreviewKind.Browser);
        if (project.OutputSelection.Word)
            Add("Word", ProjectOutputPaths.GetFilePath(project, ".docx"), PreviewKind.DocumentInfo);
        if (project.OutputSelection.Svg)
            AddFirstSvg();
        if (project.OutputSelection.Json)
            Add("JSON", ProjectOutputPaths.GetFilePath(project, ".json"), PreviewKind.Text);
        if (project.OutputSelection.Latex)
            Add("LaTeX", ProjectOutputPaths.GetFilePath(project, ".tex"), PreviewKind.Text);
        return files;

        void Add(string label, string path, PreviewKind kind)
        {
            if (File.Exists(path)) files.Add(new PreviewFile(label, path, kind));
        }

        void AddFirstSvg()
        {
            var directory = ProjectOutputPaths.GetFinalDirectory(project);
            if (!Directory.Exists(directory)) return;
            var svgPath = Directory
                .EnumerateFiles(directory, ProjectOutputPaths.GetBaseFileName(project) + "-*.svg")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (svgPath is not null) files.Add(new PreviewFile("SVG", svgPath, PreviewKind.Browser));
        }
    }

    private void ApplyDefaultFinalOutput(QuestionProject project)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(project.OutputSelection.FileName))
        {
            project.OutputSelection.FileName = ProjectOutputPaths.GetBaseFileName(project);
            changed = true;
        }
        var legacyLocalOutput = Path.Combine(project.DirectoryPath, "output");
        if (string.IsNullOrWhiteSpace(project.OutputSelection.OutputDirectory) ||
            string.Equals(project.OutputSelection.OutputDirectory.Trim(), legacyLocalOutput, StringComparison.OrdinalIgnoreCase))
        {
            project.OutputSelection.OutputDirectory = Path.Combine(
                GetDefaultFinalOutputRoot(),
                Path.GetFileName(project.DirectoryPath));
            changed = true;
        }
        if (changed) _ = _projectRepository.SaveAsync(project);
    }

    private string GetDefaultFinalOutputRoot() =>
        string.IsNullOrWhiteSpace(_settings.FinalOutputDirectory)
            ? Path.Combine(_settings.OutputDirectory, "最终输出")
            : _settings.FinalOutputDirectory.Trim();

    private static bool HasStartedStep(QuestionProject project) =>
        project.Steps.Values.Any(step => step.Attempts > 0 || step.State is StepState.Running or StepState.Completed);

    private void RefreshGenerationSummary(QuestionProject project, bool isRunning)
    {
        var summary = project.LastGeneration;
        var hasSummary = isRunning ||
                         summary.StartedAt is not null ||
                         summary.CompletedAt is not null ||
                         !string.IsNullOrWhiteSpace(summary.Message);
        GenerationSummaryCard.Visibility = hasSummary ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSummary) return;

        if (isRunning)
        {
            GenerationSummaryTitle.Text = "正在生成";
            GenerationSummaryText.Text = "运行中只显示进度，输出配置已临时收起，防止小窗口下流程被挤出。";
            GenerationSummaryFilesText.Text = string.Empty;
            GenerationReviewText.Text = string.Empty;
            return;
        }

        GenerationSummaryTitle.Text = summary.Succeeded == false ? "本次生成未完成" : "本次生成结果";
        GenerationSummaryText.Text = BuildGenerationStatusText(summary);
        GenerationSummaryFilesText.Text = summary.Files.Count == 0
            ? "暂未发现最终输出文件。"
            : "文件：" + string.Join("、", summary.Files);
        GenerationReviewText.Text = string.IsNullOrWhiteSpace(summary.ReviewSummary)
            ? string.Empty
            : "AI 复核：" + summary.ReviewSummary;
    }

    private static string BuildGenerationStatusText(GenerationSummary summary)
    {
        var started = summary.StartedAt?.ToString("HH:mm:ss");
        var completed = summary.CompletedAt?.ToString("HH:mm:ss");
        var status = summary.Succeeded switch
        {
            true => "已完成",
            false => "有步骤失败，可在下方单独重试",
            _ => "正在处理"
        };
        var timeText = started is not null && completed is not null
            ? $"（{started} → {completed}）"
            : started is not null
                ? $"（开始于 {started}）"
                : string.Empty;
        return string.IsNullOrWhiteSpace(summary.Message)
            ? status + timeText
            : $"{summary.Message}{timeText}";
    }

    private async Task StartGenerationSummaryAsync(QuestionProject project, TaskStep? singleStep)
    {
        project.LastGeneration = new GenerationSummary
        {
            StartedAt = DateTimeOffset.Now,
            Succeeded = null,
            Message = singleStep is null
                ? "已开始新一轮生成。"
                : $"正在重试 {ProcessingTaskManager.DisplayName(singleStep.Value)}。",
            Files = []
        };
        await _projectRepository.SaveAsync(project);
    }

    private async Task FinishGenerationSummaryAsync(QuestionProject project)
    {
        var visibleSteps = GetVisibleTaskSteps(project);
        var failedStep = visibleSteps.FirstOrDefault(step => project.Steps[step].State == StepState.Failed);
        var hasFailure = visibleSteps.Any(step => project.Steps[step].State == StepState.Failed);
        var hasUnfinished = visibleSteps.Any(step => project.Steps[step].State is StepState.Pending or StepState.Running);
        var succeeded = !hasFailure && !hasUnfinished;
        var summary = project.LastGeneration;
        summary.CompletedAt = DateTimeOffset.Now;
        summary.Succeeded = succeeded;
        summary.Message = succeeded
            ? "本次生成完成。"
            : hasFailure
                ? $"{ProcessingTaskManager.DisplayName(failedStep)}失败，可在下方单独重试。"
                : "本次生成尚未完成，可继续处理。";
        summary.Files = BuildGeneratedFileSummary(project).ToList();
        summary.ReviewSummary = await ReadReviewSummaryAsync(project);
        await _projectRepository.SaveAsync(project);
    }

    private static IReadOnlyList<string> BuildGeneratedFileSummary(QuestionProject project)
    {
        var files = new List<string>();
        var directory = ProjectOutputPaths.GetFinalDirectory(project);
        var baseName = ProjectOutputPaths.GetBaseFileName(project);
        AddIfExists(project.OutputSelection.Word, baseName + ".docx");
        AddIfExists(project.OutputSelection.Pdf, baseName + ".pdf");
        AddIfExists(project.OutputSelection.Latex, baseName + ".tex");
        AddIfExists(project.OutputSelection.Json, baseName + ".json");
        if (project.OutputSelection.Svg && Directory.Exists(directory))
        {
            files.AddRange(Directory
                .EnumerateFiles(directory, baseName + "-*.svg")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))!);
        }
        if (project.OutputSelection.AppendToWord && File.Exists(project.OutputSelection.AppendToWordPath))
            files.Add("已追加：" + Path.GetFileName(project.OutputSelection.AppendToWordPath));
        return files;

        void AddIfExists(bool enabled, string fileName)
        {
            if (!enabled) return;
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path)) files.Add(fileName);
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => ApplyMaximizedWorkArea();

    private void ApplyMaximizedWorkArea()
    {
        if (WindowState != WindowState.Maximized) return;
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width);
        MaxHeight = Math.Max(MinHeight, workArea.Height - 1);
    }

    private async Task<string> ReadReviewSummaryAsync(QuestionProject project)
    {
        try
        {
            var review = await _projectRepository.LoadDataAsync<OutputReviewResult>(project, "review.json");
            return review?.Summary ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void PreviewFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PreviewFile file }) SelectPreviewFile(file);
    }

    private void SelectPreviewFile(PreviewFile? file)
    {
        _activePreviewFile = file;
        var isRunningGeneratedFile = file is not null && file.Kind != PreviewKind.Source && IsProjectRunning(_currentProject);
        OpenPreviewButton.IsEnabled = file is not null && !isRunningGeneratedFile;
        SourcePreview.Visibility = Visibility.Collapsed;
        FilePreviewBrowser.Visibility = Visibility.Collapsed;
        TextFilePreview.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = file is null ? Visibility.Visible : Visibility.Collapsed;
        SourcePreview.Source = null;
        TextFilePreview.Text = string.Empty;
        if (file is null)
        {
            PreviewHintText.Text = "生成过程中会自动出现可预览文件";
            return;
        }

        PreviewHintText.Text = isRunningGeneratedFile
            ? $"{file.Path}（项目仍在处理/复核，完成后再打开可避免读取未最终写完的文件）"
            : file.Path;
        try
        {
            if (file.Kind == PreviewKind.Source &&
                !Path.GetExtension(file.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                SourcePreview.Source = LoadBitmap(file.Path);
                SourcePreview.Visibility = Visibility.Visible;
                return;
            }

            if (file.Kind == PreviewKind.Text)
            {
                TextFilePreview.Text = File.ReadAllText(file.Path);
                TextFilePreview.Visibility = Visibility.Visible;
                return;
            }

            if (file.Kind == PreviewKind.DocumentInfo)
            {
                TextFilePreview.Text = $"Word 文件已生成：{Path.GetFileName(file.Path)}\r\n\r\n" +
                                       "内置预览区可直接查看 PDF、HTML、SVG、JSON 和 LaTeX。若要检查 Word 的可编辑公式与最终版式，请点击右上角打开当前文件。";
                TextFilePreview.Visibility = Visibility.Visible;
                return;
            }

            FilePreviewBrowser.Source = new Uri(file.Path, UriKind.Absolute);
            FilePreviewBrowser.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            TextFilePreview.Text = $"无法预览该文件。\r\n\r\n{exception.Message}";
            TextFilePreview.Visibility = Visibility.Visible;
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void OpenPreviewFile_Click(object sender, RoutedEventArgs e)
    {
        if (_activePreviewFile is null || !File.Exists(_activePreviewFile.Path)) return;
        Process.Start(new ProcessStartInfo(_activePreviewFile.Path) { UseShellExecute = true });
    }

    private void PopulateProjectOutputOptions(QuestionProject project)
    {
        _isPopulatingProjectOptions = true;
        try
        {
            OutputWordBox.IsChecked = project.OutputSelection.Word;
            OutputPdfBox.IsChecked = project.OutputSelection.Pdf;
            OutputLatexBox.IsChecked = project.OutputSelection.Latex;
            OutputJsonBox.IsChecked = project.OutputSelection.Json;
            OutputSvgBox.IsChecked = project.OutputSelection.Svg;
            OutputFileNameBox.Text = string.IsNullOrWhiteSpace(project.OutputSelection.FileName)
                ? ProjectOutputPaths.GetBaseFileName(project)
                : project.OutputSelection.FileName;
            ProjectOutputDirectoryBox.Text = ProjectOutputPaths.GetFinalDirectory(project);
            SelectFigureMode(project.FigureMode);
            UpdateFigureModeHint(project.FigureMode);
            AppendWordBox.IsChecked = project.OutputSelection.AppendToWord;
            AppendWordTargetBox.Text = project.OutputSelection.AppendToWordPath;
            AppendWordTargetBox.IsEnabled = project.OutputSelection.AppendToWord;
        }
        finally
        {
            _isPopulatingProjectOptions = false;
        }
    }

    private async void OutputOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulatingProjectOptions || _currentProject is null) return;
        await SaveProjectOutputOptionsAsync();
    }

    private async void FigureModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingProjectOptions || _currentProject is null) return;
        UpdateFigureModeHint(ReadSelectedFigureMode());
        await SaveProjectOutputOptionsAsync();
    }

    private async void AppendWordTarget_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isPopulatingProjectOptions || _currentProject is null) return;
        await SaveProjectOutputOptionsAsync();
    }

    private async void AppendWordTarget_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _currentProject is null) return;
        e.Handled = true;
        await SaveProjectOutputOptionsAsync();
    }

    private async void OutputTarget_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isPopulatingProjectOptions || _currentProject is null) return;
        await SaveProjectOutputOptionsAsync();
    }

    private async void OutputTarget_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _currentProject is null) return;
        e.Handled = true;
        await SaveProjectOutputOptionsAsync();
    }

    private async void ChooseProjectOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        var dialog = new OpenFolderDialog
        {
            Title = "选择最终输出文件夹",
            InitialDirectory = Directory.Exists(ProjectOutputDirectoryBox.Text)
                ? ProjectOutputDirectoryBox.Text
                : _currentProject.DirectoryPath
        };
        if (dialog.ShowDialog(this) != true) return;
        ProjectOutputDirectoryBox.Text = dialog.FolderName;
        await SaveProjectOutputOptionsAsync();
    }

    private async void ChooseAppendWordTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        var dialog = new OpenFileDialog { Title = "选择要追加的 Word 文档", Filter = "Word 文档|*.docx" };
        if (dialog.ShowDialog() != true) return;
        AppendWordBox.IsChecked = true;
        AppendWordTargetBox.Text = dialog.FileName;
        await SaveProjectOutputOptionsAsync();
    }

    private async Task SaveProjectOutputOptionsAsync()
    {
        if (_currentProject is null) return;
        var appendPath = AppendWordBox.IsChecked == true ? AppendWordTargetBox.Text.Trim() : string.Empty;
        var oldWord = _currentProject.OutputSelection.Word;
        var oldPdf = _currentProject.OutputSelection.Pdf;
        var oldLatex = _currentProject.OutputSelection.Latex;
        var oldJson = _currentProject.OutputSelection.Json;
        var oldSvg = _currentProject.OutputSelection.Svg;
        var oldFileName = _currentProject.OutputSelection.FileName;
        var oldOutputDirectory = _currentProject.OutputSelection.OutputDirectory;
        var oldAppendPath = _currentProject.OutputSelection.AppendToWordPath;
        var oldFigureMode = _currentProject.FigureMode;
        _currentProject.OutputSelection.Word = OutputWordBox.IsChecked == true;
        _currentProject.OutputSelection.Pdf = OutputPdfBox.IsChecked == true;
        _currentProject.OutputSelection.Latex = OutputLatexBox.IsChecked == true;
        _currentProject.OutputSelection.Json = OutputJsonBox.IsChecked == true;
        _currentProject.OutputSelection.Svg = OutputSvgBox.IsChecked == true;
        _currentProject.OutputSelection.FileName = OutputFileNameBox.Text.Trim();
        _currentProject.OutputSelection.OutputDirectory = ProjectOutputDirectoryBox.Text.Trim();
        _currentProject.OutputSelection.AppendToWordPath = appendPath;
        _currentProject.FigureMode = ReadSelectedFigureMode();
        AppendWordTargetBox.IsEnabled = AppendWordBox.IsChecked == true;
        var outputTargetChanged =
            !string.Equals(oldFileName, _currentProject.OutputSelection.FileName, StringComparison.Ordinal) ||
            !string.Equals(oldOutputDirectory, _currentProject.OutputSelection.OutputDirectory, StringComparison.OrdinalIgnoreCase);
        NormalizeFigureSteps(_currentProject, oldFigureMode != _currentProject.FigureMode);
        NormalizeExportStep(
            _currentProject,
            TaskStep.WordExport,
            outputTargetChanged ||
            oldWord != _currentProject.OutputSelection.Word ||
            !string.Equals(oldAppendPath, appendPath, StringComparison.OrdinalIgnoreCase),
            _currentProject.OutputSelection.Word || _currentProject.OutputSelection.AppendToWord);
        NormalizeExportStep(_currentProject, TaskStep.PdfExport, outputTargetChanged || oldPdf != _currentProject.OutputSelection.Pdf, _currentProject.OutputSelection.Pdf);
        NormalizeExportStep(_currentProject, TaskStep.LatexExport, outputTargetChanged || oldLatex != _currentProject.OutputSelection.Latex, _currentProject.OutputSelection.Latex);
        NormalizeExportStep(_currentProject, TaskStep.JsonExport, outputTargetChanged || oldJson != _currentProject.OutputSelection.Json, _currentProject.OutputSelection.Json);
        if (outputTargetChanged || oldSvg != _currentProject.OutputSelection.Svg) ResetStep(_currentProject, TaskStep.AiReview);
        await _projectRepository.SaveAsync(_currentProject);
        RefreshProjectView(_currentProject);
    }

    private static IReadOnlyList<TaskStep> GetVisibleTaskSteps(QuestionProject project)
    {
        var steps = new List<TaskStep>
        {
            TaskStep.Ocr,
            TaskStep.FormulaRecognition,
            TaskStep.FigureRedraw
        };
        if (project.OutputSelection.Word || project.OutputSelection.AppendToWord) steps.Add(TaskStep.WordExport);
        if (project.OutputSelection.Pdf) steps.Add(TaskStep.PdfExport);
        if (project.OutputSelection.Latex) steps.Add(TaskStep.LatexExport);
        if (project.OutputSelection.Json) steps.Add(TaskStep.JsonExport);
        if (project.OutputSelection.HasAnyOutput) steps.Add(TaskStep.AiReview);
        return steps;
    }

    private bool IsProjectRunning(QuestionProject? project) =>
        project is not null && _runningProjects.ContainsKey(project.Id);

    private void SelectFigureMode(FigureProcessingMode mode)
    {
        foreach (var item in FigureModeBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal)) continue;
            FigureModeBox.SelectedItem = item;
            return;
        }
        FigureModeBox.SelectedIndex = 0;
    }

    private FigureProcessingMode ReadSelectedFigureMode()
    {
        if (FigureModeBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<FigureProcessingMode>(item.Tag?.ToString(), out var mode))
            return mode;
        return FigureProcessingMode.AiRedraw;
    }

    private void UpdateFigureModeHint(FigureProcessingMode mode)
    {
        FigureModeHintText.Foreground = mode == FigureProcessingMode.ExternalToolThenOriginalImage
            ? (Brush)FindResource("ErrorBrush")
            : (Brush)FindResource("MutedBrush");
        FigureModeHintText.Text = mode switch
        {
            FigureProcessingMode.AiRedraw => "AI 会生成可缩放 SVG，适合几何图和函数图；复杂绳结或拓扑图可能需要人工复核。",
            FigureProcessingMode.ExternalToolThenOriginalImage => "不建议使用：外部工具绘图目前可能出现坐标偏移、线段未连接或直角符号异常。仅用于实验，失败时会保留原图。",
            FigureProcessingMode.OriginalImage => "跳过 AI 重绘，直接把源图作为图形保留。最接近原题，但不是可编辑矢量图；多图精确裁切仍需后续坐标识别。",
            _ => string.Empty
        };
    }

    private static void NormalizeFigureSteps(QuestionProject project, bool changed)
    {
        if (!changed) return;
        ResetStep(project, TaskStep.FigureRedraw);
        if (project.OutputSelection.Word || project.OutputSelection.AppendToWord) ResetStep(project, TaskStep.WordExport);
        if (project.OutputSelection.Pdf) ResetStep(project, TaskStep.PdfExport);
        if (project.OutputSelection.Latex) ResetStep(project, TaskStep.LatexExport);
        if (project.OutputSelection.Json) ResetStep(project, TaskStep.JsonExport);
        if (project.OutputSelection.HasAnyOutput) ResetStep(project, TaskStep.AiReview);
    }

    private static void ResetStep(QuestionProject project, TaskStep step)
    {
        var record = project.Steps[step];
        if (record.State == StepState.Running) return;
        record.State = StepState.Pending;
        record.Error = string.Empty;
        record.CompletedAt = null;
    }

    private static void NormalizeExportStep(
        QuestionProject project,
        TaskStep step,
        bool changed,
        bool enabled)
    {
        var record = project.Steps[step];
        if (record.State == StepState.Running) return;
        if (!enabled)
        {
            record.State = StepState.Skipped;
            record.Error = string.Empty;
            record.CompletedAt = DateTimeOffset.Now;
            return;
        }
        if (changed || record.State == StepState.Skipped)
        {
            record.State = StepState.Pending;
            record.Error = string.Empty;
            record.CompletedAt = null;
        }
    }

    private async void RunProject_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        await SaveProjectOutputOptionsAsync();
        if (!ValidateOutputSelection(_currentProject)) return;
        if (_currentProject.IsComplete) QuestionProjectWorkflow.ResetFinalGenerationSteps(_currentProject);
        _ = RunProjectInBackgroundAsync(_currentProject);
    }

    private async void RetryStep_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || sender is not Button { Tag: TaskStep step }) return;
        await SaveProjectOutputOptionsAsync();
        if (!ValidateOutputSelection(_currentProject)) return;
        if (_runningProjects.ContainsKey(_currentProject.Id))
        {
            ShowNotice("任务正在处理", "这个项目当前正在处理，请等待当前步骤结束后再重试。");
            return;
        }
        ((Button)sender).IsEnabled = false;
        _ = RunProjectInBackgroundAsync(_currentProject, step);
    }

    private async Task RunProjectInBackgroundAsync(QuestionProject project, TaskStep? singleStep = null)
    {
        if (_runningProjects.ContainsKey(project.Id))
        {
            ShowNotice("任务正在处理", "这个项目已经在后台处理。你可以切换到其他项目继续工作。");
            return;
        }

        if (!TryCreateTaskManager(project, out var manager, out var provider)) return;
        var cancellation = new CancellationTokenSource();
        _runningProjects[project.Id] = cancellation;
        await StartGenerationSummaryAsync(project, singleStep);
        AddLog(singleStep is null ? "AI 开始处理项目" : $"AI 准备重试 {ProcessingTaskManager.DisplayName(singleStep.Value)}");
        UpdateAiActivitySummary();
        if (_currentProject?.Id == project.Id) RefreshProjectView(project);
        try
        {
            if (singleStep is null)
                await manager.RunPendingAsync(project, cancellation.Token);
            else
                await manager.RunStepAsync(project, singleStep.Value, cancellation.Token);
        }
        catch (Exception exception)
        {
            AddLog($"处理失败：{exception.Message}");
            if (_currentProject?.Id == project.Id) ShowNotice("处理失败", exception.Message);
        }
        finally
        {
            cancellation.Dispose();
            _runningProjects.Remove(project.Id);
            provider?.Dispose();
            await FinishGenerationSummaryAsync(project);
            UpdateAiActivitySummary();
            if (_currentProject?.Id == project.Id) RefreshProjectView(project);
        }
    }

    private bool TryCreateTaskManager(
        QuestionProject project,
        out ProcessingTaskManager manager,
        out IDisposable? providerToDispose)
    {
        manager = default!;
        providerToDispose = null;
        try
        {
            var provider = new AiProviderFactory(_settingsStore).Create(_settings);
            providerToDispose = provider as IDisposable;
            IQuestionExporter[] exporters =
            [
                new DocxExporter(new WordExportOptions(
                    _settings.WordTemplatePath,
                    project.OutputSelection.Word,
                    project.OutputSelection.AppendToWordPath)),
                new PdfExporter(),
                new LatexExporter(),
                new JsonExporter()
            ];
            manager = new ProcessingTaskManager(_projectRepository, provider, exporters);
            manager.LogAdded += message => Dispatcher.Invoke(() => AddLog(message));
            manager.ProjectChanged += changedProject => Dispatcher.Invoke(() =>
            {
                if (_currentProject?.Id == changedProject.Id)
                {
                    _currentProject = changedProject;
                    RefreshProjectView(changedProject);
                }
            });
            return true;
        }
        catch (Exception exception)
        {
            ShowSettingsPage();
            SetFeedback(SettingsFeedback, exception.Message, false);
            return false;
        }
    }

    private bool ValidateOutputSelection(QuestionProject project)
    {
        if (!project.OutputSelection.HasAnyOutput)
        {
            ShowNotice("请选择输出内容", "至少选择一种输出，或选择追加到现有 Word 文档。");
            return false;
        }
        var safeName = ProjectOutputPaths.SanitizeFileName(
            Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(project.OutputSelection.FileName)
                ? project.Name
                : project.OutputSelection.FileName));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            ShowNotice("请填写文件名", "文件名会用于生成最终文件，例如 题目6.docx、题目6.pdf。");
            return false;
        }
        try
        {
            ProjectOutputPaths.EnsureFinalDirectory(project);
        }
        catch (Exception exception)
        {
            ShowNotice("输出文件夹不可用", exception.Message);
            return false;
        }
        if (project.OutputSelection.AppendToWord && !File.Exists(project.OutputSelection.AppendToWordPath))
        {
            ShowNotice("Word 文档不存在", "请选择一个存在的 .docx 文档用于追加。");
            return false;
        }
        return true;
    }

    private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || !Directory.Exists(_currentProject.DirectoryPath)) return;
        OpenFolder(_currentProject.DirectoryPath);
    }

    private void OpenFinalOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        try
        {
            var directory = ProjectOutputPaths.EnsureFinalDirectory(_currentProject);
            OpenFolder(directory);
        }
        catch (Exception exception)
        {
            ShowNotice("无法打开输出文件夹", exception.Message);
        }
    }

    private void EditAiInstructions_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        if (_runningProjects.ContainsKey(_currentProject.Id))
        {
            ShowNotice("任务正在处理", "请在当前处理结束后修改 AI 要求。");
            return;
        }

        AiInstructionsEditor.Text = _currentProject.AiInstructions;
        AiInstructionsEditor.CaretIndex = AiInstructionsEditor.Text.Length;
        AiInstructionsOverlay.Visibility = Visibility.Visible;
        FadeIn(AiInstructionsOverlay);
        AiInstructionsEditor.Focus();
    }

    private async void SaveAiInstructions_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        var instructions = AiInstructionsEditor.Text.Trim();
        CloseAiInstructionsOverlay();
        if (instructions == _currentProject.AiInstructions) return;

        _currentProject.AiInstructions = instructions;
        foreach (var record in _currentProject.Steps.Values)
        {
            record.State = StepState.Pending;
            record.Error = string.Empty;
            record.CompletedAt = null;
        }
        await _projectRepository.SaveAsync(_currentProject);
        AddLog("AI 要求已更新，处理步骤已重置");
        RefreshProjectView(_currentProject);
        AiInstructionsButton.Content = string.IsNullOrWhiteSpace(_currentProject.AiInstructions)
            ? "AI 要求"
            : "AI 要求（已设置）";
    }

    private void CancelAiInstructions_Click(object sender, RoutedEventArgs e) => CloseAiInstructionsOverlay();

    private void CloseAiInstructionsOverlay()
    {
        AiInstructionsOverlay.Visibility = Visibility.Collapsed;
        AiInstructionsEditor.Clear();
    }

    private void ShowNotice(string title, string message)
    {
        NoticeTitle.Text = title;
        NoticeMessage.Text = message;
        NoticeOverlay.Visibility = Visibility.Visible;
        FadeIn(NoticeOverlay);
    }

    private void CloseNotice_Click(object sender, RoutedEventArgs e) => CloseNotice();

    private void CloseNotice()
    {
        NoticeOverlay.Visibility = Visibility.Collapsed;
        NoticeTitle.Text = string.Empty;
        NoticeMessage.Text = string.Empty;
    }

    private async void RefreshRecent_Click(object sender, RoutedEventArgs e) => await LoadRecentAsync();

    private async Task LoadRecentAsync()
    {
        var recent = await _projectRepository.GetRecentAsync(
            _settings.OutputDirectory,
            activeProjectIds: _runningProjects.Keys.ToHashSet());
        RecentProjectsList.ItemsSource = recent;
        NoRecentText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecentProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuestionProject project }) ShowProject(project);
    }

    private void PopulateSettingsFields()
    {
        if (SettingsProviderBox is null) return;
        _isPopulatingSettings = true;
        try
        {
            SelectSettingsProvider(_settings.Provider);
            _activeSettingsProvider = _settings.Provider;
            var profile = GetProviderProfile(_settings.Provider);
            SettingsBaseUrlBox.Text = profile.BaseUrl;
            SettingsModelBox.Text = profile.Model;
            ConfigureModelSuggestions(
                SettingsModelBox,
                SettingsModelHintText,
                _settings.Provider,
                profile.Model);
            OutputDirectoryBox.Text = _settings.OutputDirectory;
            FinalOutputDirectoryBox.Text = GetDefaultFinalOutputRoot();
            WordTemplateBox.Text = _settings.WordTemplatePath;
            ThemeBox.SelectedIndex = 0;
            SettingsApiKeyBox.Password = string.Empty;
        }
        finally
        {
            _isPopulatingSettings = false;
        }
    }

    private void SettingsProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isPopulatingSettings || SettingsBaseUrlBox is null) return;
        CaptureSettingsProviderProfile(_activeSettingsProvider);
        var kind = GetSelectedSettingsProvider();
        _activeSettingsProvider = kind;
        var profile = GetProviderProfile(kind);
        SettingsBaseUrlBox.Text = profile.BaseUrl;
        SettingsModelBox.Text = profile.Model;
        SettingsApiKeyBox.Password = string.Empty;
        ConfigureModelSuggestions(SettingsModelBox, SettingsModelHintText, kind, SettingsModelBox.Text);
        SetFeedback(SettingsFeedback, $"已切换到 {GetProviderPreset(kind).DisplayName}，此供应商会使用自己的 URL、模型和 Key。", null);
    }

    private void SelectSettingsProvider(AiProviderKind provider)
    {
        for (var index = 0; index < SettingsProviderBox.Items.Count; index++)
        {
            if (SettingsProviderBox.Items[index] is ComboBoxItem { Tag: string tag } &&
                Enum.TryParse<AiProviderKind>(tag, out var kind) &&
                kind == provider)
            {
                SettingsProviderBox.SelectedIndex = index;
                return;
            }
        }
        SettingsProviderBox.SelectedIndex = 1;
    }

    private AiProviderKind GetSelectedSettingsProvider()
    {
        if (SettingsProviderBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<AiProviderKind>(tag, out var kind))
            return kind;
        return AiProviderKind.OpenAiCompatible;
    }

    private static void ConfigureModelSuggestions(
        ComboBox target,
        TextBlock hint,
        AiProviderKind kind,
        string currentValue)
    {
        var preset = GetProviderPreset(kind);
        var suggestions = preset.ModelSuggestions;
        target.ItemsSource = suggestions
            .Append(currentValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        target.Text = currentValue;
        hint.Text = preset.ModelHint;
    }

    private async void SettingsTestConnection_Click(object sender, RoutedEventArgs e)
    {
        SetFeedback(SettingsFeedback, "正在连接...", null);
        try
        {
            CaptureSettingsProviderProfile(_activeSettingsProvider);
            var candidate = BuildSettingsCandidate(_activeSettingsProvider);
            var apiKey = string.IsNullOrWhiteSpace(SettingsApiKeyBox.Password)
                ? _settingsStore.ReadApiKey(candidate, candidate.Provider)
                : SettingsApiKeyBox.Password;
            using var provider = (IDisposable)new AiProviderFactory(_settingsStore).Create(candidate, apiKey);
            await ((IAiProvider)provider).TestConnectionAsync();
            SetFeedback(SettingsFeedback, "连接成功", true);
        }
        catch (Exception exception)
        {
            SetFeedback(SettingsFeedback, exception.Message, false);
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureSettingsProviderProfile(_activeSettingsProvider);
            var candidate = BuildSettingsCandidate(_activeSettingsProvider);
            candidate.OnboardingCompleted = true;
            if (string.IsNullOrWhiteSpace(_settingsStore.ReadApiKey(candidate, candidate.Provider)))
                throw new InvalidOperationException("请填写 API Key。");
            MirrorActiveProvider(candidate);
            _settings = candidate;
            await _settingsStore.SaveAsync(_settings);
            SettingsApiKeyBox.Password = string.Empty;
            SetFeedback(SettingsFeedback, "AI 设置已保存", true);
        }
        catch (Exception exception)
        {
            SetFeedback(SettingsFeedback, exception.Message, false);
        }
    }

    private AppSettings BuildSettingsCandidate(AiProviderKind provider)
    {
        var candidate = CloneSettings(_settings);
        candidate.Provider = provider;
        MirrorActiveProvider(candidate);
        return candidate;
    }

    private async Task SaveCommonSettingsAsync()
    {
        if (_isPopulatingSettings) return;
        var outputDirectory = OutputDirectoryBox.Text.Trim();
        var finalOutputDirectory = FinalOutputDirectoryBox.Text.Trim();
        var wordTemplatePath = WordTemplateBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("项目保存目录不能为空。");
        if (string.IsNullOrWhiteSpace(finalOutputDirectory))
            throw new InvalidOperationException("默认最终输出目录不能为空。");
        if (!string.IsNullOrWhiteSpace(wordTemplatePath) && !File.Exists(wordTemplatePath))
            throw new FileNotFoundException("找不到所选 Word 模板。", wordTemplatePath);

        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(finalOutputDirectory);
        _settings.OutputDirectory = outputDirectory;
        _settings.FinalOutputDirectory = finalOutputDirectory;
        _settings.WordTemplatePath = wordTemplatePath;
        _settings.Theme = "浅色";
        await _settingsStore.SaveAsync(_settings);
        SetFeedback(SettingsFeedback, "常规设置已自动保存", true);
    }

    private async void CommonSettings_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        try
        {
            await SaveCommonSettingsAsync();
        }
        catch (Exception exception)
        {
            SetFeedback(SettingsFeedback, exception.Message, false);
        }
    }

    private async void CommonSettings_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        try
        {
            await SaveCommonSettingsAsync();
            Keyboard.ClearFocus();
        }
        catch (Exception exception)
        {
            SetFeedback(SettingsFeedback, exception.Message, false);
        }
    }

    private async void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isPopulatingSettings || SettingsFeedback is null) return;
        try
        {
            await SaveCommonSettingsAsync();
        }
        catch (Exception exception)
        {
            SetFeedback(SettingsFeedback, exception.Message, false);
        }
    }

    private async void ChooseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择项目保存目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        OutputDirectoryBox.Text = dialog.FolderName;
        try { await SaveCommonSettingsAsync(); }
        catch (Exception exception) { SetFeedback(SettingsFeedback, exception.Message, false); }
    }

    private static void OpenFolder(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var fullPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (TryFocusExplorerWindow(fullPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", fullPath) { UseShellExecute = true });
    }

    private static bool TryFocusExplorerWindow(string directory)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                string? locationUrl = window.LocationURL as string;
                if (string.IsNullOrWhiteSpace(locationUrl)) continue;
                var localPath = Path.GetFullPath(Uri.UnescapeDataString(new Uri(locationUrl).LocalPath))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(localPath, directory, StringComparison.OrdinalIgnoreCase)) continue;
                var handle = new IntPtr((int)window.HWND);
                ShowWindow(handle, 9);
                SetForegroundWindow(handle);
                return true;
            }
        }
        catch
        {
            // Falling back to explorer.exe is preferable to blocking the user with a COM failure.
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private async void ChooseFinalOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择默认最终输出目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        FinalOutputDirectoryBox.Text = dialog.FolderName;
        try { await SaveCommonSettingsAsync(); }
        catch (Exception exception) { SetFeedback(SettingsFeedback, exception.Message, false); }
    }

    private async void ChooseWordTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Word 模板", Filter = "Word 模板|*.dotx;*.docx" };
        if (dialog.ShowDialog(this) != true) return;
        WordTemplateBox.Text = dialog.FileName;
        try { await SaveCommonSettingsAsync(); }
        catch (Exception exception) { SetFeedback(SettingsFeedback, exception.Message, false); }
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        _logExpanded = !_logExpanded;
        LogSidebarColumn.Width = new GridLength(_logExpanded ? 330 : 44);
        LogTitle.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        LogListContainer.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
        LogChevron.Kind = _logExpanded ? FluentIconKind.ArrowRight : FluentIconKind.ArrowLeft;
        LogToggleButton.ToolTip = _logExpanded ? "收起 AI 活动" : "展开 AI 活动";
        UpdateAiActivitySummary();
    }

    private void AddLog(string message)
    {
        var projectName = _currentProject?.Name;
        var prefix = string.IsNullOrWhiteSpace(projectName) ? string.Empty : $"[{projectName}] ";
        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {prefix}{message}");
        if (LogList.Items.Count > 100) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
        UpdateAiActivitySummary();
    }

    private void UpdateAiActivitySummary()
    {
        if (AiActivityProjectText is null) return;
        AiActivityProjectText.Text = _currentProject?.Name ?? "暂无项目";
        AiActivityStatusText.Text = _runningProjects.Count == 0
            ? "AI 等待任务。开始处理后，这里会实时显示 OCR、公式识别、图形重绘、导出和复核过程。"
            : $"正在后台处理 {_runningProjects.Count} 个项目。你可以切换页面或打开其他项目，任务会继续运行。";
    }

    private static void SetFeedback(TextBlock target, string message, bool? success)
    {
        target.Text = message;
        target.Foreground = success switch
        {
            true => (Brush)Application.Current.Resources["SuccessBrush"],
            false => (Brush)Application.Current.Resources["ErrorBrush"],
            null => (Brush)Application.Current.Resources["MutedBrush"]
        };
    }

    private static string GetAppVersionText()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString(3) ?? "开发版";
        try
        {
            var startInfo = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            var revision = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit(800);
            if (!string.IsNullOrWhiteSpace(revision)) return $"{version} ({revision})";
        }
        catch
        {
            // Published ZIPs do not contain .git; assembly version is enough there.
        }
        return version;
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        UpdateFeedback.Text = "尚未配置更新源";

    protected override void OnClosed(EventArgs e)
    {
        foreach (var cancellation in _runningProjects.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _runningProjects.Clear();
        base.OnClosed(e);
    }

    private sealed class TaskStepView
    {
        public TaskStepView(TaskStep step, StepRecord record)
        {
            Step = step;
            Name = ProcessingTaskManager.DisplayName(step);
            (Icon, StatusBrush, Detail) = record.State switch
            {
                StepState.Pending => (FluentIconKind.Circle, new SolidColorBrush(Color.FromRgb(160, 160, 160)), "等待中"),
                StepState.Running => (FluentIconKind.Sync, new SolidColorBrush(Color.FromRgb(23, 23, 23)), "处理中..."),
                StepState.Completed => (FluentIconKind.Checkmark, (Brush)Application.Current.Resources["SuccessBrush"], "已完成"),
                StepState.Failed => (FluentIconKind.ErrorCircle, (Brush)Application.Current.Resources["ErrorBrush"], record.Error),
                StepState.Skipped => (FluentIconKind.Subtract, new SolidColorBrush(Color.FromRgb(160, 160, 160)), "未选择输出，已跳过"),
                _ => (FluentIconKind.Circle, Brushes.Gray, string.Empty)
            };
            RetryVisibility = record.State == StepState.Failed ? Visibility.Visible : Visibility.Collapsed;
        }

        public TaskStep Step { get; }
        public string Name { get; }
        public FluentIconKind Icon { get; }
        public Brush StatusBrush { get; }
        public string Detail { get; }
        public Visibility RetryVisibility { get; }
        public bool IsRunning => Icon == FluentIconKind.Sync;
    }

    private sealed record PreviewFile(string Label, string Path, PreviewKind Kind);

    private enum PreviewKind
    {
        Source,
        Browser,
        Text,
        DocumentInfo
    }
}
