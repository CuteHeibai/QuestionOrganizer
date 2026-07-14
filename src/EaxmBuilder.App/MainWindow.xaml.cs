using System.Diagnostics;
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
            ["gpt-5.6", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5"],
            "推荐 gpt-5.6；terra 侧重成本平衡，luna 适合高吞吐。也可以直接输入模型 ID。"),
        [AiProviderKind.OpenAiCompatible] = new(
            AiProviderKind.OpenAiCompatible,
            "OpenAI Compatible",
            "https://api.openai.com/v1",
            "gpt-5.5",
            ["gpt-5.6", "gpt-5.5", "gpt-4.1", "gpt-4o"],
            "请选择兼容服务支持的模型，或输入服务商文档中的模型 ID。"),
        [AiProviderKind.Doubao] = new(
            AiProviderKind.Doubao,
            "火山豆包",
            "https://ark.cn-beijing.volces.com/api/v3",
            "doubao-seed-2-1-pro-260628",
            ["doubao-seed-2-1-pro-260628", "doubao-seed-2-1-turbo-260628", "doubao-seed-evolving", "doubao-seed-1-6-vision-250815"],
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

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
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

            _currentProject = await _projectRepository.CreateAsync(sourcePath, _settings.OutputDirectory);
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
        var projects = await _projectRepository.GetRecentAsync(_settings.OutputDirectory, 200);
        AllProjectsList.ItemsSource = projects;
        NoProjectsText.Visibility = projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshProjects_Click(object sender, RoutedEventArgs e) => await LoadProjectsAsync();

    private void AllProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QuestionProject project }) ShowProject(project);
    }

    private void ShowProject(QuestionProject project)
    {
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
        TaskStepsList.ItemsSource = Enum.GetValues<TaskStep>().Select(step => new TaskStepView(step, project.Steps[step])).ToList();
        RefreshPreviewFiles(project);
        if (project.IsComplete)
        {
            RunProjectButton.Content = "已完成";
            RunProjectButton.IsEnabled = false;
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
        UpdateAiActivitySummary();
    }

    private static IReadOnlyList<PreviewFile> BuildPreviewFiles(QuestionProject project)
    {
        var files = new List<PreviewFile>();
        if (File.Exists(project.SourcePath))
            files.Add(new PreviewFile("源文件", project.SourcePath, PreviewKind.Source));
        Add("PDF", "question.pdf", PreviewKind.Browser);
        Add("Word", "question.docx", PreviewKind.DocumentInfo);
        Add("HTML", "question.html", PreviewKind.Browser);
        Add("SVG", "figure1.svg", PreviewKind.Browser);
        Add("JSON", "document.json", PreviewKind.Text);
        Add("LaTeX", "question.tex", PreviewKind.Text);
        return files;

        void Add(string label, string fileName, PreviewKind kind)
        {
            var path = Path.Combine(project.DirectoryPath, fileName);
            if (File.Exists(path)) files.Add(new PreviewFile(label, path, kind));
        }
    }

    private void PreviewFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PreviewFile file }) SelectPreviewFile(file);
    }

    private void SelectPreviewFile(PreviewFile? file)
    {
        _activePreviewFile = file;
        OpenPreviewButton.IsEnabled = file is not null;
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

        PreviewHintText.Text = file.Path;
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
        var oldAppendPath = _currentProject.OutputSelection.AppendToWordPath;
        _currentProject.OutputSelection.Word = OutputWordBox.IsChecked == true;
        _currentProject.OutputSelection.Pdf = OutputPdfBox.IsChecked == true;
        _currentProject.OutputSelection.Latex = OutputLatexBox.IsChecked == true;
        _currentProject.OutputSelection.Json = OutputJsonBox.IsChecked == true;
        _currentProject.OutputSelection.AppendToWordPath = appendPath;
        AppendWordTargetBox.IsEnabled = AppendWordBox.IsChecked == true;
        NormalizeExportStep(
            _currentProject,
            TaskStep.WordExport,
            oldWord != _currentProject.OutputSelection.Word ||
            !string.Equals(oldAppendPath, appendPath, StringComparison.OrdinalIgnoreCase),
            _currentProject.OutputSelection.Word || _currentProject.OutputSelection.AppendToWord);
        NormalizeExportStep(_currentProject, TaskStep.PdfExport, oldPdf != _currentProject.OutputSelection.Pdf, _currentProject.OutputSelection.Pdf);
        NormalizeExportStep(_currentProject, TaskStep.LatexExport, oldLatex != _currentProject.OutputSelection.Latex, _currentProject.OutputSelection.Latex);
        NormalizeExportStep(_currentProject, TaskStep.JsonExport, oldJson != _currentProject.OutputSelection.Json, _currentProject.OutputSelection.Json);
        await _projectRepository.SaveAsync(_currentProject);
        RefreshProjectView(_currentProject);
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
        Process.Start(new ProcessStartInfo("explorer.exe", _currentProject.DirectoryPath) { UseShellExecute = true });
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
        var recent = await _projectRepository.GetRecentAsync(_settings.OutputDirectory);
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
        var wordTemplatePath = WordTemplateBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("默认输出目录不能为空。");
        if (!string.IsNullOrWhiteSpace(wordTemplatePath) && !File.Exists(wordTemplatePath))
            throw new FileNotFoundException("找不到所选 Word 模板。", wordTemplatePath);

        Directory.CreateDirectory(outputDirectory);
        _settings.OutputDirectory = outputDirectory;
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
        var dialog = new OpenFolderDialog { Title = "选择默认输出目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        OutputDirectoryBox.Text = dialog.FolderName;
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
