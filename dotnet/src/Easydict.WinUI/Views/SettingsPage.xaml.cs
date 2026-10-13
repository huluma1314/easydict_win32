using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Easydict.BobPlugin;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.AgentCli;
using Easydict.TranslationService.Services.ModelCatalog;
using TranslationLanguage = Easydict.TranslationService.Models.Language;
using Easydict.OpenVINO.Services;
using Easydict.WindowsAI.Services;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.SavedItems;
using Easydict.SidecarClient.Protocol;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Easydict.WinUI.Views;

internal enum SettingsTabId
{
    General,
    Services,
    Views,
    Hotkeys,
    Plugins,
    Advanced,
    Labs,
    Language,
    About
}

internal sealed record SettingsTabBrushes(
    Brush? Background,
    Brush? BorderBrush,
    Brush? Foreground,
    Brush? SelectedBackground,
    Brush? SelectedBorderBrush,
    Brush? SelectedForeground);

internal sealed class SettingsTabItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _label = string.Empty;
    private string _tooltip = string.Empty;
    private Brush? _background;
    private Brush? _borderBrush;
    private Brush? _foreground;
    private Brush? _selectedBackground;
    private Brush? _selectedBorderBrush;
    private Brush? _selectedForeground;

    public required SettingsTabId Id { get; init; }
    public required string IconGlyph { get; init; }
    public string AutomationId => $"SettingsTab_{Id}";

    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    public string Tooltip
    {
        get => _tooltip;
        set => SetField(ref _tooltip, value);
    }

    public Brush? Background
    {
        get => _background;
        private set => SetField(ref _background, value);
    }

    public Brush? BorderBrush
    {
        get => _borderBrush;
        private set => SetField(ref _borderBrush, value);
    }

    public Brush? Foreground
    {
        get => _foreground;
        private set => SetField(ref _foreground, value);
    }

    public Brush? SelectedBackground
    {
        get => _selectedBackground;
        private set => SetField(ref _selectedBackground, value);
    }

    public Brush? SelectedBorderBrush
    {
        get => _selectedBorderBrush;
        private set => SetField(ref _selectedBorderBrush, value);
    }

    public Brush? SelectedForeground
    {
        get => _selectedForeground;
        private set => SetField(ref _selectedForeground, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshThemeBindings(SettingsTabBrushes brushes)
    {
        Background = brushes.Background;
        BorderBrush = brushes.BorderBrush;
        Foreground = brushes.Foreground;
        SelectedBackground = brushes.SelectedBackground;
        SelectedBorderBrush = brushes.SelectedBorderBrush;
        SelectedForeground = brushes.SelectedForeground;
        OnPropertyChanged(nameof(IsSelected));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Settings page for configuring translation services, hotkeys, and behavior.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private static readonly Regex NonServiceIdCharRegex = new("[^a-z0-9-]", RegexOptions.Compiled);
    private const string ServiceConfigurationIconTag = "ServiceConfigurationIcon";
    private const string ReorderButtonEmoji = "\u2195\uFE0F";
    private const int PasswordTailVisibleLength = 3;
    private const double PasswordTailHintMaxWidth = 72;
    private const double PasswordTailHintTrailingMargin = 44;
    private const string PasswordTailHintPrefix = "...";
    private const int SettingsTabSwitchIndicatorDelayMs = 50;
    private const int SettingsTabSwitchIndicatorFrameDelayMs = 16;
    private const int DeferredUnloadTeardownDelayMs = 250;
    private const string TtsVoicePreviewText = "This is a text-to-speech preview.";

    private static readonly Uri _storeReviewUri = new("ms-windows-store://review/?ProductId=9P7NQVXF9DZJ");
    private static readonly Uri _storeProductPageUri = new("https://apps.microsoft.com/detail/9P7NQVXF9DZJ");
    private static readonly Dictionary<string, int> PreferredServiceDisplayOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bing"] = 0,
        ["google"] = 10,
        ["google_web"] = 20,
        ["deepl"] = 30,
        [LocalAITranslationService.ServiceIdValue] = 40,
        ["ollama"] = 60,
        ["openai"] = 70,
        ["deepseek"] = 90,
        ["zhipu"] = 100,
        ["kimi"] = 105,
        ["groq"] = 110,
        ["gemini"] = 120,
        ["github"] = 130,
        ["custom-openai"] = 140,
        ["openrouter"] = 145,
        ["orcarouter"] = 148,
        ["doubao"] = 150,
        ["caiyun"] = 160,
        ["niutrans"] = 170,
        ["youdao"] = 180,
        ["volcano"] = 190,
#if ENABLE_LINGUEE_SERVICE
        ["linguee"] = 200,
#endif
    };
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly ModelCatalogCacheService _modelCatalogCache = new();
    private readonly PpOcrV6ModelStore _ppOcrV6ModelStore = new();
    private bool _isLoading = true; // Prevent change detection during initial load
    private bool _isInitialized;
    private bool _isUnloaded;
    private bool _isTornDown;
    private bool _changeHandlersRegistered;
    private bool _suppressTtsVoiceSelectionChanged;
    private string _pendingTtsVoiceId = string.Empty;
    private bool _hasUnsavedChanges; // Track whether any settings have been modified since last save
    private bool _suppressAgentCliToggleDialog; // Reentrancy guard while reverting an agent CLI toggle
    private bool _hasRefreshedClaudeCodeModels;
    private bool _hasRefreshedCodexModels;
    private bool _hasRefreshedDeepSeekModels;
    private bool _hasRefreshedZhipuModels;
    private bool _hasRefreshedKimiModels;
    private bool _hasRefreshedOpenRouterModels;
    private bool _hasRefreshedOrcaRouterModels;
    private bool _isMainWindowReorderModeEnabled;
    private bool _isMiniWindowReorderModeEnabled;
    private bool _isFixedWindowReorderModeEnabled;
    private bool _themeChromeRefreshQueued;
    private bool _deferredSettingsIoStarted;
    private bool _teardownQueued;
    private bool _releaseVisualTreeImmediatelyOnUnload;
    private int _settingsTabSwitchVersion;
    private readonly Dictionary<PasswordBox, bool> _visiblePasswordBoxes = new();
    private readonly Dictionary<PasswordBox, PasswordTailHint> _passwordTailHints = new();
    private ContentDialog? _currentDialog; // Track open dialog to prevent COMException
    private readonly CancellationTokenSource _lifetimeCts = new();

    // Service selection collections for each window (populated from TranslationManager.Services)
    private readonly ObservableCollection<ServiceCheckItem> _mainWindowServices = [];
    private readonly ObservableCollection<ServiceCheckItem> _miniWindowServices = [];
    private readonly ObservableCollection<ServiceCheckItem> _fixedWindowServices = [];

    // Available language checkbox items for the ItemsRepeater
    private List<LanguageCheckboxItem> _languageItems = [];

    // Snapshot of SelectedLanguages at page load, restored on discard
    private List<string> _originalSelectedLanguages = [];

    // Dynamic UI references for encrypted MDX dictionary credential fields
    private readonly Dictionary<string, (TextBox EmailBox, PasswordBox RegcodeBox)> _mdxCredentialFields = new();
    private readonly List<(Image Icon, string IconName)> _serviceConfigurationHeaderIcons = [];

    private readonly ObservableCollection<SettingsTabItem> _settingsTabs =
    [
        new() { Id = SettingsTabId.General, IconGlyph = "\uE713", IsSelected = true },
        new() { Id = SettingsTabId.Services, IconGlyph = "\uE90F" },
        new() { Id = SettingsTabId.Views, IconGlyph = "\uE8A7" },
        new() { Id = SettingsTabId.Hotkeys, IconGlyph = "\uE765" },
        new() { Id = SettingsTabId.Advanced, IconGlyph = "\uE771" },
        new() { Id = SettingsTabId.Labs, IconGlyph = "\uEA80" },
        new() { Id = SettingsTabId.Language, IconGlyph = "\uE774" },
        new() { Id = SettingsTabId.About, IconGlyph = "\uE946" }
    ];
    private readonly HashSet<SettingsTabId> _initializedSettingsTabData = [];

    private sealed record PasswordTailHint(Border Container, TextBlock Text);


#if DEBUG
    private static readonly object DebugInstanceTrackerGate = new();
    private static readonly List<WeakReference<SettingsPage>> DebugTrackedInstances = [];
    private static int s_nextDebugInstanceId;
    private static int s_lastFullGcSurvivorCount;
    private static int s_lastTrackedGen2CollectionCount = GC.CollectionCount(2);

    private readonly Stopwatch _perfWatch = new();
    private readonly int _debugInstanceId;
    private string _debugDeferredIoState = "idle";

    private static int RegisterDebugInstance(SettingsPage page)
    {
        lock (DebugInstanceTrackerGate)
        {
            PruneDeadTrackedInstancesUnsafe();
            DebugTrackedInstances.Add(new WeakReference<SettingsPage>(page));
            return ++s_nextDebugInstanceId;
        }
    }

    private static int PruneDeadTrackedInstancesUnsafe()
    {
        for (int i = DebugTrackedInstances.Count - 1; i >= 0; i--)
        {
            if (!DebugTrackedInstances[i].TryGetTarget(out _))
            {
                DebugTrackedInstances.RemoveAt(i);
            }
        }

        return DebugTrackedInstances.Count;
    }

    private static void ForceTrackedFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static (int LiveInstances, int SurvivingAfterFullGc, int LastTrackedGen2Count) GetTrackedInstanceCounts(
        bool forceFullCollection)
    {
        if (forceFullCollection)
        {
            ForceTrackedFullCollection();
        }

        lock (DebugInstanceTrackerGate)
        {
            var liveInstances = PruneDeadTrackedInstancesUnsafe();
            if (forceFullCollection)
            {
                s_lastFullGcSurvivorCount = liveInstances;
                s_lastTrackedGen2CollectionCount = GC.CollectionCount(2);
            }

            return (liveInstances, s_lastFullGcSurvivorCount, s_lastTrackedGen2CollectionCount);
        }
    }

    [Conditional("DEBUG")]
    private void PerfLog(string event_)
    {
        Debug.WriteLine($"[SettingsPage] {_perfWatch.ElapsedMilliseconds,5}ms | {event_}");
    }

    [Conditional("DEBUG")]
    private void LogLifetimeState(string label, bool forceFullCollection = false)
    {
        var (liveInstances, survivingAfterFullGc, lastTrackedGen2Count) = GetTrackedInstanceCounts(forceFullCollection);
        Debug.WriteLine(
            $"[SettingsPage][Lifetime] #{_debugInstanceId} {label} | liveInstances={liveInstances} | globalLiveInstances={liveInstances} | survivorsAfterLastFullGC={survivingAfterFullGc} | globalSurvivorsAfterLastTrackedFullGC={survivingAfterFullGc} | lastTrackedGen2={lastTrackedGen2Count}");
    }

    [Conditional("DEBUG")]
    private void LogObjectState(string label)
    {
        var mdxPanelCount = ImportedMdxConfigPanel?.Children.Count ?? -1;
        var backStackDepth = Frame?.BackStackDepth ?? -1;
        Debug.WriteLine(
            $"[SettingsPage][Objects] #{_debugInstanceId} {label} | services={_mainWindowServices.Count}/{_miniWindowServices.Count}/{_fixedWindowServices.Count} | languages={_languageItems.Count} | activeTab={_settingsTabs.FirstOrDefault(t => t.IsSelected)?.Id} | mdxPanels={mdxPanelCount} | mdxCredentialFields={_mdxCredentialFields.Count} | backStack={backStackDepth} | deferredIo={_debugDeferredIoState}");
    }

    [Conditional("DEBUG")]
    private void LogDebugState(string label, bool forceFullCollection = false)
    {
        LogLifetimeState(label, forceFullCollection);
        LogObjectState(label);
    }

    [Conditional("DEBUG")]
    private void UpdateDeferredIoState(string state)
    {
        _debugDeferredIoState = state;
        Debug.WriteLine($"[SettingsPage][DeferredIO] #{_debugInstanceId} state={state}");
    }

    [Conditional("DEBUG")]
    private void ScheduleDelayedLifetimeCheck(string label, int delayMs = 250)
    {
        var instanceId = _debugInstanceId;
        var pageReference = new WeakReference<SettingsPage>(this);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            var (liveInstances, survivingAfterFullGc, lastTrackedGen2Count) =
                GetTrackedInstanceCounts(MemoryDiagnostics.ForceFullGcForDiagnostics);
            var trackedInstanceAliveAfterDelayedFullGC = pageReference.TryGetTarget(out _);
            Debug.WriteLine(
                $"[SettingsPage][Lifetime] #{instanceId} {label} | trackedInstanceAliveAfterDelayedFullGC={trackedInstanceAliveAfterDelayedFullGC} | liveInstances={liveInstances} | globalLiveInstances={liveInstances} | survivorsAfterDelayedFullGC={survivingAfterFullGc} | globalSurvivorsAfterDelayedFullGC={survivingAfterFullGc} | lastTrackedGen2={lastTrackedGen2Count}");
        });
    }
#endif

    public SettingsPage()
    {
#if DEBUG
        _perfWatch.Start();
        _debugInstanceId = RegisterDebugInstance(this);
        LogLifetimeState("ctor registered");
        PerfLog("ctor: begin InitializeComponent");
#endif
        // Resolve page-local theme resources using the app preference even before
        // the page is attached to its Frame (whose theme otherwise arrives later).
        RequestedTheme = MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme);
            this.InitializeComponent();
            InitializeFluentSettingsLayout();
        ApplyThemeChrome();
        ApplyLingueeAvailability();
        InitializeServiceConfigurationHeaderIcons();
        InitializePasswordTailHints();
#if DEBUG
        PerfLog("ctor: end InitializeComponent");
        MemoryDiagnostics.LogSnapshot("SettingsPage.ctor after InitializeComponent");
        LogObjectState("ctor after InitializeComponent");
#endif
        this.Loaded += OnPageLoaded;
        this.Unloaded += OnPageUnloaded;
        this.ActualThemeChanged += OnActualThemeChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Frame.BackStack.Clear();
        Frame.ForwardStack.Clear();
    }

    private void ApplyLingueeAvailability()
    {
#if ENABLE_LINGUEE_SERVICE
        LingueeFreeServicePanel.Visibility = Visibility.Visible;
#endif
    }

    private void InitializeServiceConfigurationHeaderIcons()
    {
        _serviceConfigurationHeaderIcons.Clear();
        foreach (var expander in ServiceConfigurationSection.Children.OfType<Expander>())
        {
            if (expander.Tag is string iconName && !string.IsNullOrWhiteSpace(iconName))
            {
                AddServiceConfigurationHeaderIcon(expander, iconName);
            }
        }

        UpdateServiceConfigurationHeaderIconSources();
    }

    private void AddServiceConfigurationHeaderIcon(Expander expander, string iconName)
    {
        var icon = CreateServiceConfigurationHeaderIcon();
        switch (expander.Header)
        {
            case Grid grid:
                AddIconToHeaderGrid(grid, icon);
                _serviceConfigurationHeaderIcons.Add((icon, iconName));
                break;
            case TextBlock title:
                expander.Header = CreateServiceConfigurationHeaderGrid(icon, title);
                _serviceConfigurationHeaderIcons.Add((icon, iconName));
                break;
        }
    }

    private static Image CreateServiceConfigurationHeaderIcon()
    {
        var icon = new Image
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 10, 0),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = ServiceConfigurationIconTag
        };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        icon.ImageFailed += (_, _) => icon.Visibility = Visibility.Collapsed;
        return icon;
    }

    private static void AddIconToHeaderGrid(Grid grid, Image icon)
    {
        if (grid.Children.OfType<Image>().Any(image => image.Tag as string == ServiceConfigurationIconTag))
        {
            return;
        }

        var existingChildren = grid.Children.ToList();
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        foreach (var child in existingChildren)
        {
            var isTrailingStatus = child is TextBlock textBlock
                && textBlock.HorizontalAlignment == HorizontalAlignment.Right;
            if (child is FrameworkElement element)
            {
                Grid.SetColumn(element, isTrailingStatus ? 2 : 1);
                element.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);
    }

    private static Grid CreateServiceConfigurationHeaderGrid(Image icon, TextBlock title)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleCopy = new TextBlock
        {
            Text = title.Text,
            FontWeight = title.FontWeight,
            FontSize = title.FontSize,
            FontStyle = title.FontStyle,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(titleCopy, 1);
        grid.Children.Add(icon);
        grid.Children.Add(titleCopy);
        return grid;
    }

    private void UpdateServiceConfigurationHeaderIconSources()
    {
        foreach (var (icon, iconName) in _serviceConfigurationHeaderIcons)
        {
            icon.Visibility = Visibility.Visible;
            icon.Source = new BitmapImage(ServiceIconAssetResolver.GetIconUri(iconName, ActualTheme));
        }
    }

    public void ApplyThemeChrome()
    {
        RequestedTheme = MinimalThemeService.ToElementTheme(SettingsService.Instance.AppTheme);
        var minimal = MinimalThemeService.IsActive;
        var chrome = CreateSettingsThemeChrome();
        Background = chrome.PageBackground;
        SaveButton.Shadow = minimal ? null : new ThemeShadow();
        if (LoadingOverlayRing is not null)
        {
            LoadingOverlayRing.IsActive = !minimal;
            LoadingOverlayRing.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        }
        if (NavigationLoadingRing is not null
            && NavigationLoadingOverlay is { Visibility: Visibility.Visible })
        {
            NavigationLoadingRing.IsActive = true;
            NavigationLoadingRing.Visibility = Visibility.Visible;
        }

        var tabBrushes = CreateSettingsTabBrushes();
        foreach (var tab in _settingsTabs)
        {
            tab.RefreshThemeBindings(tabBrushes);
        }

        RefreshSettingsControlChrome(chrome);
        UpdateAllPasswordTailHints();
    }

    private void RefreshSettingsControlChrome(SettingsThemeChrome chrome)
    {
        var tabStyle = Resources["SettingsTabButtonStyle"] as Style;
        var accentButtonStyle = Resources["SettingsAccentButtonStyle"] as Style;
        var inlineIconButtonStyle = Resources["SettingsInlineIconButtonStyle"] as Style;
        var sectionStyle = Resources["SettingsSectionStyle"] as Style;
        var placeholderForeground = ThemeResourceService.GetBrush("TextControlPlaceholderForeground", this);

        var pending = new Queue<DependencyObject>();
        pending.Enqueue(this);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            if (current is Button { Style: { } buttonStyle } && ReferenceEquals(buttonStyle, tabStyle))
            {
                continue;
            }

            if (current is Border border && ReferenceEquals(border.Style, sectionStyle))
            {
                border.Background = chrome.CardBackground;
                border.BorderBrush = chrome.CardBorder;
            }
            else if (current is ComboBox comboBox)
            {
                ApplyComboBoxChrome(comboBox, chrome);
            }
            else if (current is TextBox textBox)
            {
                ApplyTextBoxChrome(textBox, chrome, placeholderForeground);
            }
            else if (current is PasswordBox passwordBox)
            {
                ApplyPasswordBoxChrome(passwordBox, chrome);
            }
            else if (current is Expander expander)
            {
                ApplyExpanderChrome(expander, chrome);
            }
            else if (current is TextBlock textBlock)
            {
                textBlock.Foreground = GetSettingsTextForeground(
                    textBlock,
                    chrome.PrimaryForeground,
                    chrome.SecondaryForeground);
            }
            else if (current is FontIcon fontIcon)
            {
                fontIcon.Foreground = chrome.SecondaryForeground;
            }
            else if (current is HyperlinkButton hyperlinkButton)
            {
                hyperlinkButton.Foreground = chrome.HyperlinkForeground;
                SetResourceIfNotNull(hyperlinkButton.Resources, "TextFillColorPrimaryBrush", chrome.HyperlinkForeground);
                SetResourceIfNotNull(hyperlinkButton.Resources, "TextFillColorSecondaryBrush", chrome.HyperlinkForeground);
                continue;
            }
            else if (current is Button button && !ReferenceEquals(button.Style, tabStyle))
            {
                if (ReferenceEquals(button.Style, accentButtonStyle) ||
                    ReferenceEquals(button, BackButton) ||
                    ReferenceEquals(button, SaveButton))
                {
                    ApplyButtonChrome(
                        button,
                        chrome.AccentBackground,
                        chrome.AccentHoverBackground,
                        chrome.AccentPressedBackground,
                        chrome.AccentForeground,
                        chrome.InputBorder);
                }
                else if (ReferenceEquals(button.Style, inlineIconButtonStyle))
                {
                    if (chrome.ButtonForeground is not null)
                    {
                        button.Foreground = chrome.ButtonForeground;
                    }

                    SetResourceIfNotNull(button.Resources, "ButtonForeground", chrome.ButtonForeground);
                    SetResourceIfNotNull(button.Resources, "ButtonForegroundPointerOver", chrome.ButtonForeground);
                    SetResourceIfNotNull(button.Resources, "ButtonForegroundPressed", chrome.ButtonForeground);
                }
                else
                {
                    ApplyButtonChrome(
                        button,
                        chrome.CardBackground,
                        chrome.InputBackground,
                        chrome.InputBackground,
                        chrome.ButtonForeground,
                        chrome.InputBorder);
                }

                continue;
            }
            else if (current is ToggleSwitch toggleSwitch)
            {
                ApplyToggleSwitchChrome(toggleSwitch, chrome);
            }
            else if (current is CheckBox checkBox)
            {
                ApplyCheckBoxChrome(checkBox, chrome);
            }
            else if (current is Slider slider)
            {
                ApplySliderChrome(slider, chrome);
            }
            else if (current is RadioButton radioButton)
            {
                radioButton.Foreground = chrome.PrimaryForeground;
            }
            else if (current is ComboBoxItem comboBoxItem)
            {
                comboBoxItem.Background = chrome.CardBackground;
                comboBoxItem.Foreground = chrome.PrimaryForeground;
            }

            EnqueueVisualChildren(current, pending);
        }
    }

    private static void ApplyComboBoxChrome(ComboBox comboBox, SettingsThemeChrome chrome)
    {
        comboBox.Background = chrome.CardBackground;
        comboBox.Foreground = chrome.PrimaryForeground;
        comboBox.BorderBrush = chrome.InputBorder;
    }

    private static void ApplyTextBoxChrome(
        TextBox textBox,
        SettingsThemeChrome chrome,
        Brush? placeholderForeground)
    {
        textBox.Background = chrome.InputBackground;
        textBox.Foreground = chrome.PrimaryForeground;
        textBox.BorderBrush = chrome.InputBorder;
        textBox.PlaceholderForeground = placeholderForeground;
    }

    private static void ApplyPasswordBoxChrome(PasswordBox passwordBox, SettingsThemeChrome chrome)
    {
        passwordBox.Background = chrome.InputBackground;
        passwordBox.Foreground = chrome.PrimaryForeground;
        passwordBox.BorderBrush = chrome.InputBorder;
    }

    private static void ApplySliderChrome(Slider slider, SettingsThemeChrome chrome)
    {
        slider.Foreground = chrome.PrimaryForeground;
    }

    private static void ApplyToggleSwitchChrome(ToggleSwitch toggleSwitch, SettingsThemeChrome chrome)
    {
        toggleSwitch.Foreground = chrome.PrimaryForeground;
    }

    private static void ApplyExpanderChrome(Expander expander, SettingsThemeChrome chrome)
    {
        expander.Foreground = chrome.PrimaryForeground;
    }

    private static void ApplyCheckBoxChrome(CheckBox checkBox, SettingsThemeChrome chrome)
    {
        checkBox.Foreground = chrome.PrimaryForeground;
    }

    private SettingsTabBrushes CreateSettingsTabBrushes()
    {
        return new SettingsTabBrushes(
            Background: CreateThemeBrush("SettingsTabBackgroundColor"),
            BorderBrush: CreateThemeBrush("SettingsTabBorderColor"),
            Foreground: CreateThemeBrush("SettingsTabForegroundColor"),
            SelectedBackground: CreateThemeBrush("SettingsTabSelectedBackgroundColor"),
            SelectedBorderBrush: CreateThemeBrush("SettingsTabSelectedBorderColor"),
            SelectedForeground: CreateThemeBrush("SettingsTabSelectedForegroundColor"));
    }

    private SettingsThemeChrome CreateSettingsThemeChrome()
    {
        var primaryForeground = CreateThemeBrush("QueryTextColor");
        var inputBackground = CreateThemeBrush("FloatingInputBackgroundColor");
        var inputBorder = CreateThemeBrush("FloatingInputBorderColor");
        var accentBackground = CreateThemeBrush("AccentColor");

        return new SettingsThemeChrome(
            PageBackground: CreateThemeBrush("FloatingWindowBackgroundColor"),
            CardBackground: CreateThemeBrush("EasydictCardBackgroundColor"),
            CardBorder: CreateThemeBrush("EasydictCardBorderColor"),
            InputBackground: inputBackground,
            InputBorder: inputBorder,
            PrimaryForeground: primaryForeground,
            SecondaryForeground: CreateThemeBrush("ServiceResultHeaderSecondaryForegroundColor")
                ?? primaryForeground,
            ButtonForeground: CreateThemeBrush("FloatingIconForegroundColor")
                ?? primaryForeground,
            AccentBackground: accentBackground,
            AccentForeground: CreateThemeBrush("AccentForegroundColor"),
            AccentHoverBackground: CreateThemeBrush("AccentPointerOverColor") ?? accentBackground,
            AccentPressedBackground: CreateThemeBrush("AccentPressedColor") ?? accentBackground,
            HyperlinkForeground: CreateThemeBrush("BlueAccentColor"));
    }

    private static Brush? GetSettingsTextForeground(
        TextBlock textBlock,
        Brush? primaryForeground,
        Brush? secondaryForeground)
    {
        if (IsStatusOrSuccessText(textBlock))
        {
            return textBlock.Foreground;
        }

        if (!HasLocalValue(textBlock, TextBlock.ForegroundProperty) || IsPrimarySettingsText(textBlock))
        {
            return primaryForeground;
        }

        return secondaryForeground ?? primaryForeground;
    }

    private static bool IsPrimarySettingsText(TextBlock textBlock)
    {
        return textBlock.FontWeight.Weight >= 600 || textBlock.FontSize >= 18;
    }

    private static bool IsStatusOrSuccessText(TextBlock textBlock)
    {
        return string.Equals(textBlock.Text, "✓", StringComparison.Ordinal);
    }

    private sealed record SettingsThemeChrome(
        Brush? PageBackground,
        Brush? CardBackground,
        Brush? CardBorder,
        Brush? InputBackground,
        Brush? InputBorder,
        Brush? PrimaryForeground,
        Brush? SecondaryForeground,
        Brush? ButtonForeground,
        Brush? AccentBackground,
        Brush? AccentForeground,
        Brush? AccentHoverBackground,
        Brush? AccentPressedBackground,
        Brush? HyperlinkForeground);

    private Brush? CreateThemeBrush(string colorKey)
    {
        return ThemeResourceService.GetColor(colorKey, this) is { } color
            ? new SolidColorBrush(color)
            : null;
    }

    private static void ApplyButtonChrome(
        Button button,
        Brush? background,
        Brush? hoverBackground,
        Brush? pressedBackground,
        Brush? foreground,
        Brush? border)
    {
        if (background is not null)
        {
            button.Background = background;
        }

        if (foreground is not null)
        {
            button.Foreground = foreground;
        }

        if (border is not null)
        {
            button.BorderBrush = border;
        }

        SetResourceIfNotNull(button.Resources, "ButtonBackground", background);
        SetResourceIfNotNull(button.Resources, "ButtonBackgroundPointerOver", hoverBackground ?? background);
        SetResourceIfNotNull(button.Resources, "ButtonBackgroundPressed", pressedBackground ?? hoverBackground ?? background);
        SetResourceIfNotNull(button.Resources, "ButtonForeground", foreground);
        SetResourceIfNotNull(button.Resources, "ButtonForegroundPointerOver", foreground);
        SetResourceIfNotNull(button.Resources, "ButtonForegroundPressed", foreground);
        SetResourceIfNotNull(button.Resources, "ButtonBorderBrush", border);
        SetResourceIfNotNull(button.Resources, "ButtonBorderBrushPointerOver", border);
        SetResourceIfNotNull(button.Resources, "ButtonBorderBrushPressed", border);
        SetResourceIfNotNull(button.Resources, "AccentTextFillColorPrimaryBrush", foreground);
    }

    private static void SetResourceIfNotNull(ResourceDictionary resources, string key, object? value)
    {
        if (value is not null)
        {
            resources[key] = value;
        }
    }

    private static bool HasLocalValue(DependencyObject obj, DependencyProperty property)
    {
        return !ReferenceEquals(obj.ReadLocalValue(property), DependencyProperty.UnsetValue);
    }

    private static void EnqueueVisualChildren(
        DependencyObject parent,
        Queue<DependencyObject> pending)
    {
        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(parent);
        }
        catch (COMException)
        {
            return;
        }
        catch (ArgumentException)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            pending.Enqueue(VisualTreeHelper.GetChild(parent, i));
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateServiceConfigurationHeaderIconSources();
        QueueApplyThemeChrome();
    }

    private void QueueApplyThemeChrome()
    {
        if (_themeChromeRefreshQueued || _isUnloaded || _isTornDown)
        {
            return;
        }

        _themeChromeRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _themeChromeRefreshQueued = false;
            if (!_isUnloaded && !_isTornDown)
            {
                ApplyThemeChrome();
            }
        }))
        {
            _themeChromeRefreshQueued = false;
        }
    }

    private static bool IsDeferredIoDisabledForDebug()
    {
#if DEBUG
        var value = Environment.GetEnvironmentVariable("EASYDICT_DEBUG_DISABLE_SETTINGS_DEFERRED_IO");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    private static bool TryGetLivePage(WeakReference<SettingsPage> pageReference, [NotNullWhen(true)] out SettingsPage? page)
    {
        if (pageReference.TryGetTarget(out page) && !page._isUnloaded)
        {
            return true;
        }

        page = null;
        return false;
    }

    private static async Task<long> GetCacheEntryCountAsync()
    {
        using var cacheService = new TranslationCacheService();
        return await cacheService.GetEntryCountAsync().ConfigureAwait(false);
    }

    private void InitializeSettingsTabs()
    {
        SettingsTabsHost.ItemsSource = _settingsTabs;
        SelectSettingsTab(SettingsTabId.General, resetScroll: false);
    }

    private async void OnSettingsTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SettingsTabId tabId })
        {
            return;
        }

        await SelectSettingsTabAsync(tabId, resetScroll: true);
    }

    private async void OnRateAppLinkClick(object sender, RoutedEventArgs e)
    {
        if (!await LaunchStoreRatingAsync(LaunchUriAsync))
        {
            Debug.WriteLine("[Settings] Could not open Microsoft Store rating page.");
        }
    }

    internal static async Task<bool> LaunchStoreRatingAsync(Func<Uri, Task<bool>> launchAsync)
    {
        ArgumentNullException.ThrowIfNull(launchAsync);

        if (await TryLaunchUriAsync(_storeReviewUri, launchAsync))
        {
            return true;
        }

        return await TryLaunchUriAsync(_storeProductPageUri, launchAsync);
    }

    private static Task<bool> LaunchUriAsync(Uri uri) => Launcher.LaunchUriAsync(uri).AsTask();

    private static async Task<bool> TryLaunchUriAsync(Uri uri, Func<Uri, Task<bool>> launchAsync)
    {
        try
        {
            return await launchAsync(uri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Could not launch URI: {ex.Message}");
            return false;
        }
    }

    private void OnSettingsTabButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not SettingsTabItem tab)
        {
            return;
        }

        AutomationProperties.SetAutomationId(button, tab.AutomationId);
        AutomationProperties.SetName(button, tab.Label);
    }

    private async Task SelectSettingsTabAsync(SettingsTabId tabId, bool resetScroll)
    {
        if (_isUnloaded || _isTornDown)
        {
            return;
        }

        var switchVersion = ++_settingsTabSwitchVersion;
        var needsDeferredInitialization = _isInitialized && !IsSettingsTabReadyForImmediateSwitch(tabId);
        if (needsDeferredInitialization && IsSettingsTabContentAvailableForImmediateReveal(tabId))
        {
            ApplySettingsTabSelection(tabId, resetScroll);
            ShowSettingsTabSwitchProgress();

            await Task.Delay(SettingsTabSwitchIndicatorFrameDelayMs);
            if (_isUnloaded || _isTornDown || switchVersion != _settingsTabSwitchVersion)
            {
                return;
            }

            try
            {
                EnsureSettingsTabDataInitialized(tabId);
            }
            finally
            {
                if (switchVersion == _settingsTabSwitchVersion)
                {
                    HideSettingsTabSwitchProgress();
                }
            }

            return;
        }

        var showProgress = ShouldShowSettingsTabSwitchProgress(tabId);
        if (showProgress)
        {
            ShowSettingsTabSwitchProgress();
            if (RequiresSettingsTabSwitchPreRenderDelay(tabId))
            {
                await Task.Delay(SettingsTabSwitchIndicatorDelayMs);
            }
            else
            {
                await Task.Delay(SettingsTabSwitchIndicatorFrameDelayMs);
            }

            if (_isUnloaded || _isTornDown || switchVersion != _settingsTabSwitchVersion)
            {
                return;
            }
        }
        else
        {
            HideSettingsTabSwitchProgress();
        }

        try
        {
            SelectSettingsTab(tabId, resetScroll);
        }
        finally
        {
            if (showProgress && switchVersion == _settingsTabSwitchVersion)
            {
                HideSettingsTabSwitchProgress();
            }
        }
    }

    private bool ShouldShowSettingsTabSwitchProgress(SettingsTabId tabId)
    {
        if (!_isInitialized || _settingsTabs.FirstOrDefault(tab => tab.IsSelected)?.Id == tabId)
        {
            return false;
        }

        return !IsSettingsTabReadyForImmediateSwitch(tabId);
    }

    private bool RequiresSettingsTabSwitchPreRenderDelay(SettingsTabId tabId)
    {
        return !IsSettingsTabReadyForImmediateSwitch(tabId);
    }

    private bool IsSettingsTabReadyForImmediateSwitch(SettingsTabId tabId)
    {
        return _initializedSettingsTabData.Contains(tabId)
            && (tabId != SettingsTabId.Views || ViewsTabContent != null);
    }

    private bool IsSettingsTabContentAvailableForImmediateReveal(SettingsTabId tabId)
    {
        return tabId != SettingsTabId.Views || ViewsTabContent != null;
    }

    private void ShowSettingsTabSwitchProgress()
    {
        if (SettingsTabSwitchRing is null)
        {
            return;
        }

        SettingsTabSwitchRing.Visibility = Visibility.Visible;
        SettingsTabSwitchRing.IsActive = true;
    }

    private void HideSettingsTabSwitchProgress()
    {
        if (SettingsTabSwitchRing is null)
        {
            return;
        }

        SettingsTabSwitchRing.IsActive = false;
        SettingsTabSwitchRing.Visibility = Visibility.Collapsed;
    }

    private void SelectSettingsTab(SettingsTabId tabId, bool resetScroll)
    {
        EnsureTabContentLoaded(tabId);
        if (_isInitialized)
        {
            EnsureSettingsTabDataInitialized(tabId);
        }

        ApplySettingsTabSelection(tabId, resetScroll);
    }

    private void ApplySettingsTabSelection(SettingsTabId tabId, bool resetScroll)
    {
        foreach (var item in _settingsTabs)
        {
            item.IsSelected = item.Id == tabId;
        }

        GeneralTabContent.Visibility = tabId == SettingsTabId.General ? Visibility.Visible : Visibility.Collapsed;
        ServicesTabContent.Visibility = tabId == SettingsTabId.Services ? Visibility.Visible : Visibility.Collapsed;
        if (ViewsTabContent != null)
        {
            ViewsTabContent.Visibility = tabId == SettingsTabId.Views ? Visibility.Visible : Visibility.Collapsed;
        }
        HotkeysTabContent.Visibility = tabId == SettingsTabId.Hotkeys ? Visibility.Visible : Visibility.Collapsed;
        AdvancedTabContent.Visibility = tabId == SettingsTabId.Advanced ? Visibility.Visible : Visibility.Collapsed;
        LabsTabContent.Visibility = tabId == SettingsTabId.Labs ? Visibility.Visible : Visibility.Collapsed;
        LanguageTabContent.Visibility = tabId == SettingsTabId.Language ? Visibility.Visible : Visibility.Collapsed;
        AboutTabContent.Visibility = tabId == SettingsTabId.About ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(MainScrollViewer, $"SelectedSettingsTab:{tabId}");

        if (resetScroll)
        {
            MainScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            SettingsDetailsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
            _settingsDetailsOffset = _settingsStackedOffset = 0;
        }

        // Theme chrome is refreshed on actual theme changes (OnActualThemeChanged →
        // QueueApplyThemeChrome) and when a tab's XAML is first inflated
        // (EnsureTabContentLoaded above). Visibility-toggle switches reuse the brushes
        // already assigned on the inflated subtrees, so re-walking the visual tree here
        // costs ~ms-to-tens-of-ms on the Services tab for no observable change.
        QueueSettingsTabRenderProbe(tabId);
    }

    partial void QueueSettingsTabRenderProbe(SettingsTabId tabId);
    partial void CancelSettingsTabRenderProbe();

    private void EnsureTabContentLoaded(SettingsTabId tabId)
    {
        switch (tabId)
        {
            case SettingsTabId.Views when ViewsTabContent == null:
                FindName(nameof(ViewsTabContent));
                BindWindowServicePanels();
                ApplyWindowResultsLocalization(LocalizationService.Instance);
                // Chrome the newly-inflated subtree — the rest of the page already has
                // brushes assigned from the initial ctor pass. ApplyThemeChrome walks the
                // entire visual tree (~thousands of elements on the Services tab), so
                // confining it to this inflation site avoids paying the cost on every
                // visibility-toggle switch in SelectSettingsTab below.
                ApplyThemeChrome();
                break;
        }
    }

    private void ReleaseViewsTabContent()
    {
        if (ViewsTabContent == null)
        {
            return;
        }

        if (MainWindowServicesPanel != null)
        {
            MainWindowServicesPanel.ItemsSource = null;
        }

        if (MiniWindowServicesPanel != null)
        {
            MiniWindowServicesPanel.ItemsSource = null;
        }

        if (FixedWindowServicesPanel != null)
        {
            FixedWindowServicesPanel.ItemsSource = null;
        }

        UnloadObject(ViewsTabContent);
    }

    private bool ShouldLoadSettingsTab(SettingsTabId tabId, bool deferLazyTabData)
    {
        return !deferLazyTabData || _initializedSettingsTabData.Contains(tabId);
    }

    private void EnsureSettingsTabDataInitialized(SettingsTabId tabId)
    {
        if (!_initializedSettingsTabData.Add(tabId))
        {
            return;
        }

        var wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            if (tabId == SettingsTabId.Language)
            {
                PopulateLanguageCheckboxGrid();
                var loc = LocalizationService.Instance;
                PopulateSettingsLanguageCombo(FirstLanguageCombo, loc);
                PopulateSettingsLanguageCombo(SecondLanguageCombo, loc);
            }

            LoadSettingsForTab(tabId);
            ApplyLocalization();

            if (tabId == SettingsTabId.Views)
            {
                BindWindowServicePanels();
                ApplyWindowResultsLocalization(LocalizationService.Instance);
            }

            if (tabId == SettingsTabId.Advanced)
            {
                QueueDeferredSettingsIo(_lifetimeCts.Token);
            }
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }

    private void LoadSettingsForTab(SettingsTabId tabId)
    {
        var initializedTabs = _initializedSettingsTabData.ToArray();
        _initializedSettingsTabData.Clear();
        _initializedSettingsTabData.Add(tabId);
        try
        {
            LoadSettings(deferLazyTabData: true);
        }
        finally
        {
            _initializedSettingsTabData.Clear();
            foreach (var initializedTab in initializedTabs)
            {
                _initializedSettingsTabData.Add(initializedTab);
            }
        }
    }

    private void EnsureAllSettingsTabDataInitialized()
    {
        foreach (var tabId in Enum.GetValues<SettingsTabId>())
        {
            EnsureSettingsTabDataInitialized(tabId);
        }
    }

    private void BindWindowServicePanels()
    {
        if (MainWindowServicesPanel != null)
        {
            MainWindowServicesPanel.ItemsSource = _mainWindowServices;
        }

        if (MiniWindowServicesPanel != null)
        {
            MiniWindowServicesPanel.ItemsSource = _miniWindowServices;
        }

        if (FixedWindowServicesPanel != null)
        {
            FixedWindowServicesPanel.ItemsSource = _fixedWindowServices;
        }
    }


    private static void SetLocalAiRating(TextBlock? ratingText, string rating, string tooltip)
    {
        if (ratingText == null)
        {
            return;
        }

        ratingText.Text = rating;
        ToolTipService.SetToolTip(ratingText, tooltip);
    }

    private void ApplyTtsLocalization(LocalizationService loc)
    {
        TtsSettingsHeaderText.Text = loc.GetString("TtsSettingsHeader");
        TtsSpeedLabelText.Text = loc.GetString("TtsSpeedLabel");
        AutoPlayTranslationToggle.Header = loc.GetString("AutoPlayTranslation");
        TtsVoiceLabelText.Text = loc.GetString("TtsVoiceLabel");
        AutomationProperties.SetName(TtsVoiceCombo, TtsVoiceLabelText.Text);
        TtsVoicePreviewButtonText.Text = loc.GetString("TtsVoicePreviewButton");
        AutomationProperties.SetName(TtsVoicePreviewButton, TtsVoicePreviewButtonText.Text);
        var voiceRefreshText = loc.GetString("TtsVoiceRefreshTooltip");
        ToolTipService.SetToolTip(TtsVoiceRefreshButton, voiceRefreshText);
        AutomationProperties.SetName(TtsVoiceRefreshButton, voiceRefreshText);
    }

    private void ApplyBehaviorLocalization(LocalizationService loc)
    {
        BehaviorHeaderText.Text = loc.GetString("Behavior");
        MinimizeToTrayToggle.Header = loc.GetString("MinimizeToTray");
        MinimizeToTrayOnStartupToggle.Header = loc.GetString("MinimizeToTrayOnStartup");
        ClipboardMonitorToggle.Header = loc.GetString("MonitorClipboard");
        MouseSelectionTranslateToggle.Header = loc.GetString("MouseSelectionTranslate");
        MouseSelectionExcludedAppsBox.Header = loc.GetString("ExcludedApps");
        MouseSelectionExcludedAppsBox.PlaceholderText = loc.GetString("ExcludedAppsPlaceholder");
        MouseSelectionExcludedAppsDescriptionText.Text = loc.GetString("ExcludedAppsDescription");
        HoverWordLookupToggle.Header = loc.GetString("HoverWordLookup");
        HoverWordLookupModifierLabel.Text = loc.GetString("HoverWordLookupModifier");
        HoverWordLookupTriggerDescriptionRun.Text = " " + loc.GetString("HoverWordLookupTriggerDescription");
        AutomationProperties.SetName(HoverWordLookupModifierCombo, loc.GetString("HoverWordLookupModifier"));
        HoverWordLookupModifierNoneItem.Content = loc.GetString("HoverWordLookupModifierNone");
        HoverWordLookupModifierCtrlItem.Content = loc.GetString("HoverWordLookupModifierCtrl");
        HoverWordLookupModifierShiftItem.Content = loc.GetString("HoverWordLookupModifierShift");
        HoverWordLookupModifierAltItem.Content = loc.GetString("HoverWordLookupModifierAlt");
        HoverWordLookupServiceCombo.Header = loc.GetString("HoverWordLookupService");
        if (_hoverWordLookupServiceAutoItem != null)
            _hoverWordLookupServiceAutoItem.Content = loc.GetString("HoverWordLookupServiceAuto");
        HoverWordLookupOcrFallbackToggle.Header = loc.GetString("HoverWordLookupOcrFallback");
        HoverWordLookupDescriptionText.Text = loc.GetString("HoverWordLookupDescription");
        HoverWordLookupDelayLabel.Text = loc.GetString("HoverWordLookupDelay");
        HoverWordLookupAdvancedExpander.Header = loc.GetString("HoverWordLookupAdvanced");
        AutomationProperties.SetName(HoverWordLookupDelaySlider, loc.GetString("HoverWordLookupDelay"));
        HoverWordLookupDelayDescriptionText.Text = loc.GetString("HoverWordLookupDelayDescription");
        AlwaysOnTopToggle.Header = loc.GetString("AlwaysOnTop");
        CompactModeToggle.Header = loc.GetString("CompactMode");
        CompactModeDescriptionText.Text = loc.GetString("CompactModeDescription");
        ResultFontSizeLabel.Text = loc.GetString("ResultFontSize");
        ResultFontSizeDescriptionText.Text = loc.GetString("ResultFontSizeDescription");
        QuickActionsHeaderText.Text = loc.GetString("QuickActionsHeader");
        ShowOcrButtonToggle.Header = loc.GetString("ShowOcrButton");
        ShowPinButtonToggle.Header = loc.GetString("ShowPinButton");
        ShowSourcePlayButtonToggle.Header = loc.GetString("ShowSourcePlayButton");
        ShowSwapButtonToggle.Header = loc.GetString("ShowSwapButton");
        LaunchAtStartupToggle.Header = loc.GetString("LaunchAtStartup");
        HideEmptyServiceResultsToggle.Header = loc.GetString("HideEmptyServiceResults");
        EnableLocalDictionarySuggestionsLabelText.Text = loc.GetString("EnableLocalDictionarySuggestions");
        AutomationProperties.SetName(EnableLocalDictionarySuggestionsToggle, loc.GetString("EnableLocalDictionarySuggestions"));
        LabsHeaderText.Text = loc.GetString("SettingsTab_Labs");
        LabsDescriptionText.Text = loc.GetString("SettingsTab_Labs_Tooltip");
        MouseSelectionPopDelayLabel.Text = loc.GetString("MouseSelectionPopDelay");
        MouseSelectionPopDelayDescriptionText.Text = loc.GetString("MouseSelectionPopDelayDescription");
    }

    private void ApplyUILanguageLocalization(LocalizationService loc)
    {
        UILanguageCombo.Header = loc.GetString("UILanguage");
        UILanguageDescriptionText.Text = loc.GetString("UILanguageDescription");
    }

    private void ApplyOcrLocalization(LocalizationService loc)
    {
        OcrSettingsHeaderText.Text = loc.GetString("OcrSettingsHeader");
        OcrEngineCombo.Header = loc.GetString("OcrEngine");
        OcrEngineNativeItem.Content = loc.GetString("OcrEngineNative");
        OcrEngineCustomApiItem.Content = loc.GetString("OcrEngineCustomApi");
        OcrApiKeyHeaderText.Text = loc.GetString("OcrApiKey");
        OcrEndpointBox.Header = loc.GetString("OcrEndpoint");
        OcrModelBox.Header = loc.GetString("OcrModel");
        OcrSystemPromptBox.Header = loc.GetString("OcrSystemPrompt");
        OcrEnableThinkingToggle.Header = loc.GetString("OcrEnableThinking");
        OcrEnableThinkingDescriptionText.Text = loc.GetString("OcrEnableThinkingDescription");
        TestOcrConnectionButton.Content = loc.GetString("TestOcrConnection");
        OcrTestStatusBox.Header = loc.GetString("OcrTestResult");
    }

    private void ApplyPasswordRevealLocalization(LocalizationService loc)
    {
        foreach (var button in GetPasswordRevealButtons())
        {
            UpdatePasswordRevealButtonState(button, loc);
        }
    }


    private void InitializePasswordTailHints()
    {
        foreach (var button in GetPasswordRevealButtons())
        {
            if (button.Tag is not PasswordBox passwordBox
                || _passwordTailHints.ContainsKey(passwordBox)
                || passwordBox.Parent is not Grid grid)
            {
                continue;
            }

            var hintText = new TextBlock
            {
                FontSize = 12,
                MaxWidth = PasswordTailHintMaxWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAccessibilityView(hintText, AccessibilityView.Raw);

            var hintContainer = new Border
            {
                Child = hintText,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsHitTestVisible = false,
                Margin = new Thickness(0, 0, PasswordTailHintTrailingMargin, 0),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAccessibilityView(hintContainer, AccessibilityView.Raw);

            Canvas.SetZIndex(hintContainer, 1);
            Canvas.SetZIndex(button, 2);
            grid.Children.Add(hintContainer);
            _passwordTailHints[passwordBox] = new PasswordTailHint(hintContainer, hintText);
            passwordBox.PasswordChanged += OnPasswordTailHintPasswordChanged;
            UpdatePasswordTailHint(passwordBox);
        }
    }

    private void UpdatePasswordRevealButtonState(Button button, LocalizationService loc)
    {
        if (button.Tag is not PasswordBox passwordBox)
        {
            return;
        }

        var isVisible = _visiblePasswordBoxes.TryGetValue(passwordBox, out var visible) && visible;
        if (button.Content is FontIcon icon)
        {
            icon.Glyph = isVisible ? "\uE8F5" : "\uE890";
        }

        var secretName = loc.GetString(GetPasswordRevealSecretResourceKey(passwordBox));
        var label = loc.GetString(isVisible ? "HideSecretFormat" : "ShowSecretFormat", secretName);
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        UpdatePasswordTailHint(passwordBox);
    }

    private void OnPasswordTailHintPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            UpdatePasswordTailHint(passwordBox);
        }
    }

    private void UpdateAllPasswordTailHints()
    {
        foreach (var passwordBox in _passwordTailHints.Keys)
        {
            UpdatePasswordTailHint(passwordBox);
        }
    }

    private void UpdatePasswordTailHint(PasswordBox passwordBox)
    {
        if (!_passwordTailHints.TryGetValue(passwordBox, out var hint))
        {
            return;
        }

        var password = passwordBox.Password ?? string.Empty;
        var isFullyVisible = _visiblePasswordBoxes.TryGetValue(passwordBox, out var visible) && visible;
        if (isFullyVisible || password.Length == 0)
        {
            hint.Container.Visibility = Visibility.Collapsed;
            return;
        }

        var tailLength = Math.Min(PasswordTailVisibleLength, password.Length);
        hint.Text.Text = PasswordTailHintPrefix + password.Substring(password.Length - tailLength);
        hint.Text.Foreground = CreateThemeBrush("ServiceResultHeaderSecondaryForegroundColor")
            ?? CreateThemeBrush("QueryTextColor");
        hint.Container.Background = CreateThemeBrush("FloatingInputBackgroundColor");
        hint.Container.Visibility = Visibility.Visible;
    }

    private void UnregisterPasswordTailHints()
    {
        foreach (var (passwordBox, hint) in _passwordTailHints.ToList())
        {
            passwordBox.PasswordChanged -= OnPasswordTailHintPasswordChanged;
            if (hint.Container.Parent is Grid grid)
            {
                grid.Children.Remove(hint.Container);
            }
        }

        _passwordTailHints.Clear();
    }


    private void ApplyLayoutDetectionLocalization(LocalizationService loc)
    {
    }

    private void UpdateSettingsTabLabels(LocalizationService loc)
    {
        foreach (var tab in _settingsTabs)
        {
            tab.Label = loc.GetString($"SettingsTab_{tab.Id}");
            tab.Tooltip = loc.GetString($"SettingsTab_{tab.Id}_Tooltip");
        }
    }

    private void ApplyWindowResultsLocalization(LocalizationService loc)
    {
        if (WindowResultsHeaderText != null)
            WindowResultsHeaderText.Text = loc.GetString("WindowResults");
        if (WindowResultsDescriptionText != null)
            WindowResultsDescriptionText.Text = loc.GetString("WindowResultsDescription");
        if (MainWindowHeaderText != null)
            MainWindowHeaderText.Text = loc.GetString("MainWindow");
        if (MiniWindowHeaderText != null)
            MiniWindowHeaderText.Text = loc.GetString("MiniWindow");
        if (FixedWindowHeaderText != null)
            FixedWindowHeaderText.Text = loc.GetString("FixedWindow");
        UpdateAllServiceReorderModeButtonText();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        InitializePasswordTailHints();

        if (_isUnloaded || _isInitialized)
        {
            return;
        }

#if DEBUG
        LogDebugState("OnPageLoaded");
        MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageLoaded");
#endif
#if DEBUG
        PerfLog("OnPageLoaded: first load, dispatching deferred init");
        UpdateDeferredIoState("queued");
#endif
        var token = _lifetimeCts.Token;
        // First load: show loading overlay, defer heavy work
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_isUnloaded || _isInitialized || token.IsCancellationRequested)
                {
                    return;
                }

                InitializeSettingsContent(token);
            });
    }

    /// <summary>
    /// Performs all heavy initialization work on first load, then reveals content.
    /// Dispatched at Low priority so the loading overlay renders first.
    /// </summary>
    private void InitializeSettingsContent(CancellationToken cancellationToken)
    {
        if (_isUnloaded || _isInitialized || cancellationToken.IsCancellationRequested)
        {
            return;
        }

#if DEBUG
        PerfLog("InitializeSettingsContent: begin");
        var initializeBaseline = MemoryDiagnostics.GetTotalMemoryForDiagnostics();
        MemoryDiagnostics.LogSnapshot("SettingsPage.InitializeSettingsContent begin");
        LogObjectState("InitializeSettingsContent begin");
#endif
        _isLoading = true;
        InitializeSettingsTabs();
        _initializedSettingsTabData.Clear();
        foreach (var tabId in Enum.GetValues<SettingsTabId>())
        {
            _initializedSettingsTabData.Add(tabId);
        }

        EnsureTabContentLoaded(SettingsTabId.Views);

        // Snapshot original SelectedLanguages for discard/restore
        _originalSelectedLanguages = new List<string>(_settings.SelectedLanguages);

#if DEBUG
        PerfLog("PopulateLanguageCheckboxGrid: begin");
#endif
        // Populate available languages checkbox grid
        PopulateLanguageCheckboxGrid();
#if DEBUG
        PerfLog("PopulateLanguageCheckboxGrid: end");
        MemoryDiagnostics.LogSnapshot("SettingsPage.PopulateLanguageCheckboxGrid complete");
        LogObjectState("PopulateLanguageCheckboxGrid complete");
#endif

        // Populate First/Second Language combos dynamically
        var loc = LocalizationService.Instance;
        PopulateSettingsLanguageCombo(FirstLanguageCombo, loc);
        PopulateSettingsLanguageCombo(SecondLanguageCombo, loc);

#if DEBUG
        PerfLog("LoadSettings: begin");
#endif
        LoadSettings(deferLazyTabData: false);
#if DEBUG
        PerfLog("LoadSettings: end");
        MemoryDiagnostics.LogSnapshot("SettingsPage.LoadSettings complete");
        LogObjectState("LoadSettings complete");
        PerfLog("ApplyLocalization: begin");
#endif

        // Apply localization to all UI elements
        ApplyLocalization();
#if DEBUG
        PerfLog("ApplyLocalization: end");
#endif

#if DEBUG
        PerfLog("RegisterChangeHandlers: begin");
#endif
        RegisterChangeHandlers();
#if DEBUG
        PerfLog("RegisterChangeHandlers: end");
#endif
        _isLoading = false;
        _isInitialized = true;

        // Reveal content, hide loading overlay
        LoadingOverlay.Visibility = Visibility.Collapsed;
        MainScrollViewer.Visibility = Visibility.Visible;
        MainScrollViewer.IsEnabled = true;
        BackButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
#if DEBUG
        PerfLog("Content revealed");
        MemoryDiagnostics.LogDelta("SettingsPage.InitializeSettingsContent retained after init", initializeBaseline);
        MemoryDiagnostics.LogSnapshot("SettingsPage.InitializeSettingsContent complete");
        LogDebugState("InitializeSettingsContent complete");
#endif

        // Defer disk I/O (ONNX model check, SQLite cache) to after content is visible
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_isUnloaded || cancellationToken.IsCancellationRequested)
                {
#if DEBUG
                    UpdateDeferredIoState("canceled-before-start");
#endif
                    return;
                }

                if (IsDeferredIoDisabledForDebug())
                {
#if DEBUG
                    UpdateDeferredIoState("skipped");
                    PerfLog("Deferred I/O: skipped by EASYDICT_DEBUG_DISABLE_SETTINGS_DEFERRED_IO");
                    MemoryDiagnostics.LogSnapshot("SettingsPage.Deferred I/O skipped");
                    LogObjectState("Deferred I/O skipped");
#endif
                    return;
                }

#if DEBUG
                UpdateDeferredIoState("io-dispatched");
                PerfLog("Deferred I/O: dispatching queued work");
#endif
                if (!TryBeginDeferredSettingsIo(cancellationToken))
                {
                    return;
                }

                RunDeferredSettingsIo(cancellationToken);
            });
    }

    private void QueueDeferredSettingsIo(CancellationToken cancellationToken)
    {
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (_isUnloaded || cancellationToken.IsCancellationRequested || IsDeferredIoDisabledForDebug())
                {
                    return;
                }

                if (!TryBeginDeferredSettingsIo(cancellationToken))
                {
                    return;
                }

                RunDeferredSettingsIo(cancellationToken);
            });
    }

    private bool TryBeginDeferredSettingsIo(CancellationToken cancellationToken)
    {
        if (_deferredSettingsIoStarted || _isUnloaded || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        _deferredSettingsIoStarted = true;
        return true;
    }

    private void RunDeferredSettingsIo(CancellationToken cancellationToken)
    {
#if DEBUG
        UpdateDeferredIoState("onnx-running");
        PerfLog("Deferred I/O: begin UpdateOnnxModelStatus");
#endif
        UpdateOnnxModelStatus();
#if DEBUG
        UpdateDeferredIoState("onnx-complete");
        PerfLog("Deferred I/O: end UpdateOnnxModelStatus");
        UpdateDeferredIoState("cache-dispatched");
        PerfLog("Deferred I/O: begin UpdateCacheStatusAsync");
#endif
        _ = UpdateCacheStatusAsync(new WeakReference<SettingsPage>(this), DispatcherQueue, cancellationToken);
#if DEBUG
        PerfLog("Deferred I/O: end (UpdateCacheStatusAsync dispatched)");
#endif
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
#if DEBUG
        LogDebugState("OnPageUnloaded queued deferred teardown");
        MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageUnloaded (deferred teardown queued)");
#endif
        QueueTeardownOnUnload(deferVisualTreeRelease: !_releaseVisualTreeImmediatelyOnUnload);
        _releaseVisualTreeImmediatelyOnUnload = false;
    }

    private void QueueTeardownOnUnload(bool deferVisualTreeRelease = true)
    {
        if (_isTornDown || _teardownQueued)
        {
            return;
        }

        _teardownQueued = true;
        _isUnloaded = true;
        _isLoading = true;
#if DEBUG
        UpdateDeferredIoState("teardown-queued");
#endif

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore if already disposed by shutdown path.
        }

        // Stop new UI callbacks immediately; release the heavy visual tree after
        // MainPage has had a chance to present its first frame.
        this.Loaded -= OnPageLoaded;
        this.Unloaded -= OnPageUnloaded;
        this.ActualThemeChanged -= OnActualThemeChanged;

        CancelSettingsTabRenderProbe();

        if (deferVisualTreeRelease)
        {
            _ = CompleteTeardownOnUnloadAsync();
            return;
        }

        TeardownOnUnload();
    }

    private async Task CompleteTeardownOnUnloadAsync()
    {
        await Task.Delay(DeferredUnloadTeardownDelayMs).ConfigureAwait(false);

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
#if DEBUG
                PerfLog("OnPageUnloaded: deferred teardown begin");
                MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageUnloaded deferred teardown begin");
#endif
                TeardownOnUnload();
#if DEBUG
                MemoryDiagnostics.LogSnapshot("SettingsPage.OnPageUnloaded deferred teardown complete");
                LogDebugState("OnPageUnloaded deferred teardown complete");
                ScheduleDelayedLifetimeCheck("OnPageUnloaded delayed full GC (250ms)", 250);
                ScheduleDelayedLifetimeCheck("OnPageUnloaded delayed full GC (1000ms)", 1000);
#endif
            });
    }

    private void TeardownOnUnload()
    {
        if (_isTornDown)
        {
            return;
        }

        _isTornDown = true;
        _isUnloaded = true;
        _isLoading = true;
        CancelSettingsTabRenderProbe();
#if DEBUG
        UpdateDeferredIoState("teardown");
#endif

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore if already disposed by shutdown path.
        }
        _lifetimeCts.Dispose();

        // Disconnect lifecycle handlers to avoid retaining the page.
        this.Loaded -= OnPageLoaded;
        this.Unloaded -= OnPageUnloaded;
        this.ActualThemeChanged -= OnActualThemeChanged;

        UnregisterPasswordTailHints();
        UnregisterChangeHandlers();
        UnregisterLanguageCheckboxHandlers();
        HideSettingsTabSwitchProgress();

        try { _currentDialog?.Hide(); } catch (COMException) { }
        _currentDialog = null;

        ReleaseViewsTabContent();
        LanguageCheckboxGrid.ItemsSource = null;
        SettingsTabsHost.ItemsSource = null;

        ImportedMdxConfigPanel.Children.Clear();
        _mdxCredentialFields.Clear();


        _mainWindowServices.Clear();
        _miniWindowServices.Clear();
        _fixedWindowServices.Clear();
        _languageItems.Clear();
        _settingsTabs.Clear();

        Content = null;
        _isInitialized = false;

#if DEBUG
        LogObjectState("TeardownOnUnload complete");
#endif
    }



    /// <summary>
    /// Named handler for ServiceCheckItem.PropertyChanged, replacing the lambda
    /// so it can be properly unregistered to prevent memory leaks.
    /// </summary>
    private void OnServiceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnSettingChanged(sender!, e);
    }

    private void RegisterServiceCollectionHandlers(ObservableCollection<ServiceCheckItem> collection)
    {
        foreach (var item in collection)
        {
            item.PropertyChanged -= OnServiceItemPropertyChanged;
            item.PropertyChanged += OnServiceItemPropertyChanged;
        }
    }

    private void UnregisterServiceCollectionHandlers(ObservableCollection<ServiceCheckItem> collection)
    {
        foreach (var item in collection)
        {
            item.PropertyChanged -= OnServiceItemPropertyChanged;
        }
    }

    /// <summary>
    /// Apply localized On/Off content to service ToggleSwitches in DataTemplates.
    /// Called when each ToggleSwitch is loaded.
    /// </summary>
    private void OnServiceToggleSwitchLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
        {
            var loc = LocalizationService.Instance;
            toggle.OnContent = loc.GetString("Auto");
            toggle.OffContent = loc.GetString("Manual");
        }
    }

    private void OnToggleMainWindowReorderModeClicked(object sender, RoutedEventArgs e)
    {
        SetMainWindowReorderMode(!_isMainWindowReorderModeEnabled);
    }

    private void OnToggleMiniWindowReorderModeClicked(object sender, RoutedEventArgs e)
    {
        SetMiniWindowReorderMode(!_isMiniWindowReorderModeEnabled);
    }

    private void OnToggleFixedWindowReorderModeClicked(object sender, RoutedEventArgs e)
    {
        SetFixedWindowReorderMode(!_isFixedWindowReorderModeEnabled);
    }

    private void SetMainWindowReorderMode(bool isEnabled)
    {
        _isMainWindowReorderModeEnabled = isEnabled;
        ApplyServiceReorderMode(_mainWindowServices, isEnabled);
        UpdateServiceReorderModeButtonText(MainWindowReorderModeButton, isEnabled);
    }

    private void SetMiniWindowReorderMode(bool isEnabled)
    {
        _isMiniWindowReorderModeEnabled = isEnabled;
        ApplyServiceReorderMode(_miniWindowServices, isEnabled);
        UpdateServiceReorderModeButtonText(MiniWindowReorderModeButton, isEnabled);
    }

    private void SetFixedWindowReorderMode(bool isEnabled)
    {
        _isFixedWindowReorderModeEnabled = isEnabled;
        ApplyServiceReorderMode(_fixedWindowServices, isEnabled);
        UpdateServiceReorderModeButtonText(FixedWindowReorderModeButton, isEnabled);
    }

    private void ResetServiceReorderModes()
    {
        SetMainWindowReorderMode(false);
        SetMiniWindowReorderMode(false);
        SetFixedWindowReorderMode(false);
    }

    private static void ApplyServiceReorderMode(ObservableCollection<ServiceCheckItem> collection, bool isEnabled)
    {
        foreach (var item in collection)
        {
            item.IsReorderModeEnabled = isEnabled;
        }
    }

    private void UpdateAllServiceReorderModeButtonText()
    {
        UpdateServiceReorderModeButtonText(MainWindowReorderModeButton, _isMainWindowReorderModeEnabled);
        UpdateServiceReorderModeButtonText(MiniWindowReorderModeButton, _isMiniWindowReorderModeEnabled);
        UpdateServiceReorderModeButtonText(FixedWindowReorderModeButton, _isFixedWindowReorderModeEnabled);
    }

    private static void UpdateServiceReorderModeButtonText(Button? button, bool isEnabled)
    {
        if (button == null)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        button.Content = $"{ReorderButtonEmoji} {loc.GetString(
            isEnabled
                ? "EnabledServicesDoneReorderingButton"
                : "EnabledServicesReorderButton")}";
    }

    /// <summary>
    /// Move a service one position up in the given collection.
    /// </summary>
    private static void MoveServiceUp(ObservableCollection<ServiceCheckItem> collection, string? serviceId)
    {
        if (string.IsNullOrEmpty(serviceId)) return;
        for (int i = 1; i < collection.Count; i++)
        {
            if (collection[i].ServiceId == serviceId)
            {
                collection.Move(i, i - 1);
                return;
            }
        }
    }

    /// <summary>
    /// Move a service one position down in the given collection.
    /// </summary>
    private static void MoveServiceDown(ObservableCollection<ServiceCheckItem> collection, string? serviceId)
    {
        if (string.IsNullOrEmpty(serviceId)) return;
        for (int i = 0; i < collection.Count - 1; i++)
        {
            if (collection[i].ServiceId == serviceId)
            {
                collection.Move(i, i + 1);
                return;
            }
        }
    }

    private void OnMoveMainServiceUp(object sender, RoutedEventArgs e)
    {
        MoveServiceUp(_mainWindowServices, (sender as FrameworkElement)?.Tag as string);
        OnSettingChanged(sender, e);
    }

    private void OnMoveMainServiceDown(object sender, RoutedEventArgs e)
    {
        MoveServiceDown(_mainWindowServices, (sender as FrameworkElement)?.Tag as string);
        OnSettingChanged(sender, e);
    }

    private void OnMoveMiniServiceUp(object sender, RoutedEventArgs e)
    {
        MoveServiceUp(_miniWindowServices, (sender as FrameworkElement)?.Tag as string);
        OnSettingChanged(sender, e);
    }

    private void OnMoveMiniServiceDown(object sender, RoutedEventArgs e)
    {
        MoveServiceDown(_miniWindowServices, (sender as FrameworkElement)?.Tag as string);
        OnSettingChanged(sender, e);
    }

    private void OnMoveFixedServiceUp(object sender, RoutedEventArgs e)
    {
        MoveServiceUp(_fixedWindowServices, (sender as FrameworkElement)?.Tag as string);
        OnSettingChanged(sender, e);
    }

    private void OnMoveFixedServiceDown(object sender, RoutedEventArgs e)
    {
        MoveServiceDown(_fixedWindowServices, (sender as FrameworkElement)?.Tag as string);
        OnSettingChanged(sender, e);
    }

    /// <summary>
    /// Show the floating save button only when a user-editable setting differs
    /// from the persisted settings snapshot. Some UI events are raised while
    /// refreshing status panels or reselecting an already-saved value; those
    /// should not create an unsaved-changes state.
    /// </summary>
    private void OnSettingChanged(object sender, object e)
    {
        if (_isLoading) return;

        if (!DoesChangedControlDifferFromSettings(sender, e) && !HasAnyTrackedSettingChanges())
        {
            _hasUnsavedChanges = false;
            SaveButton.Visibility = Visibility.Collapsed;
            return;
        }

        _hasUnsavedChanges = true;
        SaveButton.Visibility = Visibility.Visible;
    }

    private bool DoesChangedControlDifferFromSettings(object? sender, object? e)
    {
        if (e is PropertyChangedEventArgs { PropertyName: nameof(ServiceCheckItem.IsReorderModeEnabled) or nameof(ServiceCheckItem.IsAvailable) })
        {
            return false;
        }


        if (ReferenceEquals(sender, AppThemeCombo))
        {
            return !SameSetting(GetSelectedTag(AppThemeCombo) ?? "System", _settings.AppTheme);
        }

        if (ReferenceEquals(sender, HoverWordLookupModifierCombo))
        {
            return !SameSetting(GetTagComboValue(HoverWordLookupModifierCombo, "Ctrl"), _settings.HoverWordLookupModifier);
        }

        if (ReferenceEquals(sender, HoverWordLookupServiceCombo))
        {
            return !SameSetting(GetTagComboValue(HoverWordLookupServiceCombo, ""), _settings.HoverWordLookupServiceId ?? "");
        }

        if (ReferenceEquals(sender, UILanguageCombo))
        {
            return !SameUiLanguageSetting(GetSelectedTag(UILanguageCombo), _settings.UILanguage);
        }

        if (ReferenceEquals(sender, FirstLanguageCombo))
        {
            return !SameSetting(GetSelectedTag(FirstLanguageCombo) ?? "zh", _settings.FirstLanguage);
        }

        if (ReferenceEquals(sender, SecondLanguageCombo))
        {
            return !SameSetting(GetSelectedTag(SecondLanguageCombo) ?? "en", _settings.SecondLanguage);
        }

        if (ReferenceEquals(sender, EnableInternationalServicesToggle))
        {
            return EnableInternationalServicesToggle.IsOn != _settings.EnableInternationalServices;
        }

        if (ReferenceEquals(sender, AutoSelectTargetToggle))
        {
            return AutoSelectTargetToggle.IsOn != _settings.AutoSelectTargetLanguage;
        }



        if (sender is ServiceCheckItem)
        {
            return ServicesDifferFromSettings();
        }

        return false;
    }

    private bool HasAnyTrackedSettingChanges()
    {
        return IsSettingsTabInitialized(SettingsTabId.Services) && ServicesTabSettingsDifferFromSettings()
            || IsSettingsTabInitialized(SettingsTabId.General) && GeneralTabSettingsDifferFromSettings()
            || IsSettingsTabInitialized(SettingsTabId.Labs) && LabsTabSettingsDifferFromSettings()
            || IsSettingsTabInitialized(SettingsTabId.Language) && LanguageTabSettingsDifferFromSettings()
            || IsSettingsTabInitialized(SettingsTabId.Hotkeys) && HotkeyTabSettingsDifferFromSettings()
            || IsSettingsTabInitialized(SettingsTabId.Advanced) && AdvancedTabSettingsDifferFromSettings()
            || IsSettingsTabInitialized(SettingsTabId.Views) && ServicesDifferFromSettings();
    }

    private bool IsSettingsTabInitialized(SettingsTabId tabId)
    {
        return _initializedSettingsTabData.Contains(tabId);
    }


    private bool GeneralTabSettingsDifferFromSettings()
    {
        return !SameSetting(GetSelectedTag(AppThemeCombo) ?? "System", _settings.AppTheme)
            || MinimizeToTrayToggle.IsOn != _settings.MinimizeToTray
            || MinimizeToTrayOnStartupToggle.IsOn != _settings.MinimizeToTrayOnStartup
            || ClipboardMonitorToggle.IsOn != _settings.ClipboardMonitoring
            || MouseSelectionTranslateToggle.IsOn != _settings.MouseSelectionTranslate
            || !SameSequence(GetCommaSeparatedValues(MouseSelectionExcludedAppsBox.Text), _settings.MouseSelectionExcludedApps)
            || AlwaysOnTopToggle.IsOn != _settings.AlwaysOnTop
            || LaunchAtStartupToggle.IsOn != _settings.LaunchAtStartup
            || !SameDouble(TtsSpeedSlider.Value, _settings.TtsSpeed)
            || !SameSetting(_pendingTtsVoiceId, _settings.SelectedTtsVoiceId)
            || AutoPlayTranslationToggle.IsOn != _settings.AutoPlayTranslation
            || HideEmptyServiceResultsToggle.IsOn != _settings.HideEmptyServiceResults
            || EnableLocalDictionarySuggestionsToggle.IsOn != _settings.EnableLocalDictionarySuggestions
            || !SameDouble(ResultFontScaleSlider.Value, _settings.ResultFontScale)
            || (int)Math.Round(MouseSelectionPopDelaySlider.Value) != _settings.MouseSelectionPopDelayMs
            || CompactModeToggle.IsOn != _settings.CompactMode
            || ShowOcrButtonToggle.IsOn != _settings.ShowOcrButton
            || ShowPinButtonToggle.IsOn != _settings.ShowPinButton
            || ShowSourcePlayButtonToggle.IsOn != _settings.ShowSourcePlayButton
            || ShowSwapButtonToggle.IsOn != _settings.ShowSwapButton;
    }

    private bool LabsTabSettingsDifferFromSettings()
    {
        return HoverWordLookupToggle.IsOn != _settings.HoverWordLookupEnabled
            || !SameSetting(GetTagComboValue(HoverWordLookupModifierCombo, "Ctrl"), _settings.HoverWordLookupModifier)
            || HoverWordLookupOcrFallbackToggle.IsOn != _settings.HoverWordLookupUseOcrFallback
            || (int)Math.Round(HoverWordLookupDelaySlider.Value) != _settings.HoverWordLookupDelayMs
            || !SameSetting(GetTagComboValue(HoverWordLookupServiceCombo, ""), _settings.HoverWordLookupServiceId ?? "");
    }

    private bool LanguageTabSettingsDifferFromSettings()
    {
        return !SameSetting(GetSelectedTag(FirstLanguageCombo) ?? "zh", _settings.FirstLanguage)
            || !SameSetting(GetSelectedTag(SecondLanguageCombo) ?? "en", _settings.SecondLanguage)
            || !SameUiLanguageSetting(GetSelectedTag(UILanguageCombo), _settings.UILanguage)
            || AutoSelectTargetToggle.IsOn != _settings.AutoSelectTargetLanguage
            || !SettingsPresentationComparison.LanguagesEqual(_languageItems.Where(item => item.IsSelected).Select(item => item.Tag), _settings.SelectedLanguages);
    }

    private bool HotkeyTabSettingsDifferFromSettings()
    {
        return !SameSetting(ShowHotkeyBox.Text, _settings.ShowWindowHotkey)
            || !SameSetting(TranslateHotkeyBox.Text, _settings.TranslateSelectionHotkey)
            || !SameSetting(ShowMiniHotkeyBox.Text, _settings.ShowMiniWindowHotkey)
            || !SameSetting(ShowFixedHotkeyBox.Text, _settings.ShowFixedWindowHotkey)
            || !SameSetting(OcrTranslateHotkeyBox.Text, _settings.OcrTranslateHotkey)
            || !SameSetting(SilentOcrHotkeyBox.Text, _settings.SilentOcrHotkey)
            || ShowHotkeyEnabledToggle.IsOn != _settings.EnableShowWindowHotkey
            || TranslateHotkeyEnabledToggle.IsOn != _settings.EnableTranslateSelectionHotkey
            || ShowMiniHotkeyEnabledToggle.IsOn != _settings.EnableShowMiniWindowHotkey
            || ShowFixedHotkeyEnabledToggle.IsOn != _settings.EnableShowFixedWindowHotkey
            || OcrTranslateHotkeyEnabledToggle.IsOn != _settings.EnableOcrTranslateHotkey
            || SilentOcrHotkeyEnabledToggle.IsOn != _settings.EnableSilentOcrHotkey;
    }

    private bool AdvancedTabSettingsDifferFromSettings()
    {
        var ocrOptions = GetCurrentOcrServiceOptions();

        return ocrOptions.Engine != _settings.OcrEngine
            || !SameSetting(ocrOptions.ApiKey, _settings.OcrApiKey)
            || !SameSetting(ocrOptions.Endpoint, _settings.OcrEndpoint)
            || (ocrOptions.Engine != OcrEngineType.PpOcrV6 && !SameSetting(ocrOptions.Model, _settings.OcrModel))
            || !SameSetting(ocrOptions.SystemPrompt, _settings.OcrSystemPrompt)
            || ocrOptions.EnableThinking != _settings.OcrEnableThinking
            || ProxyEnabledToggle.IsOn != _settings.ProxyEnabled
            || ProxyBypassLocalToggle.IsOn != _settings.ProxyBypassLocal
            || !SameSetting(ProxyUriBox.Text?.Trim() ?? "", _settings.ProxyUri)
            || TranslationCacheToggle.IsOn != _settings.EnableTranslationCache
            || !SameSetting(CustomPromptBox.Text?.Trim() ?? "", _settings.LongDocCustomPrompt);
    }

    private bool ServicesDifferFromSettings()
    {
        return !SameSequence(GetEnabledServicesFromCollection(_mainWindowServices), _settings.MainWindowEnabledServices)
            || !SameSequence(GetEnabledServicesFromCollection(_miniWindowServices), _settings.MiniWindowEnabledServices)
            || !SameSequence(GetEnabledServicesFromCollection(_fixedWindowServices), _settings.FixedWindowEnabledServices)
            || !SettingsPresentationComparison.QueryModesEqual(GetEnabledQueryFromCollection(_mainWindowServices), _settings.MainWindowServiceEnabledQuery)
            || !SettingsPresentationComparison.QueryModesEqual(GetEnabledQueryFromCollection(_miniWindowServices), _settings.MiniWindowServiceEnabledQuery)
            || !SettingsPresentationComparison.QueryModesEqual(GetEnabledQueryFromCollection(_fixedWindowServices), _settings.FixedWindowServiceEnabledQuery);
    }

    private static bool SameSetting(string? current, string? saved)
    {
        return string.Equals(current ?? "", saved ?? "", StringComparison.Ordinal);
    }

    private static bool SameSecret(string? current, string? saved)
    {
        var normalizedCurrent = string.IsNullOrWhiteSpace(current) ? null : current;
        return string.Equals(normalizedCurrent, saved, StringComparison.Ordinal);
    }

    private static bool SameUiLanguageSetting(string? current, string? saved)
    {
        var currentLanguage = current ?? LocalizationService.Instance.CurrentLanguage;
        var savedLanguage = string.IsNullOrWhiteSpace(saved)
            ? LocalizationService.Instance.CurrentLanguage
            : saved;
        return string.Equals(currentLanguage, savedLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameDouble(double current, double saved)
    {
        return Math.Abs(current - saved) < 0.0001;
    }

    private static List<string> GetCommaSeparatedValues(string? value)
    {
        return (value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static bool SameSequence(IReadOnlyList<string> current, IReadOnlyList<string> saved)
    {
        return current.SequenceEqual(saved, StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameDictionary(IReadOnlyDictionary<string, bool> current, IReadOnlyDictionary<string, bool> saved)
    {
        return current.Count == saved.Count
            && current.All(pair => saved.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private void OnDeepLModeChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        ApplyDeepLModeMutualExclusion();
        OnSettingChanged(sender, e);
    }





    /// <summary>
    /// Restores test success indicators based on persisted ServiceTestStatus.
    /// </summary>
    private void RestoreTestStatusIndicators()
    {
        var statusMap = new Dictionary<string, TextBlock>
        {
        };

        foreach (var (serviceId, indicator) in statusMap)
        {
            if (_settings.ServiceTestStatus.TryGetValue(serviceId, out var passed) && passed)
            {
                indicator.Visibility = Visibility.Visible;
            }
        }

        UpdateImportedMdxSummary();
        BuildImportedMdxConfigUI();
    }

    private void UpdateImportedMdxSummary()
    {
        ImportedMdxSummaryText.Text = _settings.ImportedMdxDictionaries.Count switch
        {
            0 => "No MDX dictionaries imported",
            1 => "1 MDX dictionary imported",
            var c => $"{c} MDX dictionaries imported"
        };
    }

    internal static (bool IsEnabled, string HintText) GetLocalDictionarySuggestionsToggleState(int importedDictionaryCount)
    {
        if (importedDictionaryCount > 0)
        {
            return (true, string.Empty);
        }

        return (false, "Import a custom MDX dictionary to enable input suggestions.");
    }

    /// <summary>
    /// Dynamically builds Expander config UI for each imported MDX dictionary.
    /// Encrypted dictionaries show email/regcode fields + test button.
    /// Non-encrypted dictionaries show a "ready to use" status.
    /// All dictionaries show file path and a delete button.
    /// </summary>
    private void BuildImportedMdxConfigUI()
    {
        ImportedMdxConfigPanel.Children.Clear();
        _mdxCredentialFields.Clear();

        var allDicts = _settings.ImportedMdxDictionaries;
        if (allDicts.Count == 0)
        {
#if DEBUG
            LogObjectState("BuildImportedMdxConfigUI empty");
#endif
            return;
        }

        // Sync IsEncrypted from live services (handles stale settings data)
        using var handle = TranslationManagerService.Instance.AcquireHandle();
        bool settingsChanged = false;
        foreach (var d in allDicts)
        {
            if (handle.Manager.Services.TryGetValue(d.ServiceId, out var svc)
                && svc is MdxDictionaryTranslationService mdx
                && mdx.IsEncrypted != d.IsEncrypted)
            {
                d.IsEncrypted = mdx.IsEncrypted;
                settingsChanged = true;
            }
        }
        if (settingsChanged) _settings.Save();

        var loc = LocalizationService.Instance;

        foreach (var dict in allDicts)
        {
            var expander = new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // Header with name (icon varies by encryption status) and status indicator
            var headerGrid = new Grid();
            // Strip any existing emoji prefix from DisplayName (import hardcodes 📚)
            var rawName = dict.DisplayName;
            if (rawName.StartsWith("\U0001f4da ")) rawName = rawName["\U0001f4da ".Length..];
            if (rawName.StartsWith("\U0001f512 ")) rawName = rawName["\U0001f512 ".Length..];
            var icon = dict.IsEncrypted ? "\U0001f512" : "\U0001f4da";
            var nameText = new TextBlock
            {
                Text = $"{icon} {rawName}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var statusText = new TextBlock
            {
                Text = "\u2713",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"],
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = _settings.ServiceTestStatus.TryGetValue(dict.ServiceId, out var passed) && passed
                    ? Visibility.Visible : Visibility.Collapsed
            };
            headerGrid.Children.Add(nameText);
            headerGrid.Children.Add(statusText);
            expander.Header = headerGrid;

            // Content panel
            var contentPanel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 8, 0, 0) };

            // File path (read-only, selectable)
            var filePathBox = new TextBox
            {
                Header = loc.GetString("MdxFilePath"),
                Text = dict.FilePath ?? string.Empty,
                IsReadOnly = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            contentPanel.Children.Add(filePathBox);

            if (dict.IsEncrypted)
            {
                // Encrypted: email + regcode + help text + test button
                var emailBox = new TextBox
                {
                    Header = "Email",
                    Width = 350,
                    PlaceholderText = loc.GetString("MdxEmailPlaceholder"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Text = dict.Email ?? string.Empty
                };
                contentPanel.Children.Add(emailBox);

                var regcodeBox = new PasswordBox
                {
                    Header = loc.GetString("MdxRegistrationCode"),
                    Width = 350,
                    PlaceholderText = loc.GetString("MdxRegistrationCode"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Password = dict.Regcode ?? string.Empty
                };
                contentPanel.Children.Add(regcodeBox);

                var helpText = new TextBlock
                {
                    Text = loc.GetString("MdxEncryptedHelpText"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                };
                contentPanel.Children.Add(helpText);

                var testButton = new Button { Content = loc.GetString("Test"), Padding = new Thickness(8, 4, 8, 4) };

                // Capture variables for the closure
                var capturedDict = dict;
                var capturedEmailBox = emailBox;
                var capturedRegcodeBox = regcodeBox;
                var capturedStatusText = statusText;

                testButton.Click += async (s, args) =>
                {
                    await TestEncryptedMdxAsync(capturedDict, capturedEmailBox, capturedRegcodeBox, (Button)s!, capturedStatusText);
                };
                contentPanel.Children.Add(testButton);

                // Register for Save button credential persistence
                _mdxCredentialFields[dict.ServiceId] = (emailBox, regcodeBox);
            }
            else
            {
                // Non-encrypted: ready to use info
                var readyText = new TextBlock
                {
                    Text = loc.GetString("MdxDictionaryReady"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                };
                contentPanel.Children.Add(readyText);
            }

            // MDD Resource Files section
            var mddLabel = new TextBlock
            {
                Text = loc.GetString("MddAssociatedFiles") ?? "Resource Files",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 2)
            };
            contentPanel.Children.Add(mddLabel);

            if (dict.MddFilePaths.Count > 0)
            {
                foreach (var mddPath in dict.MddFilePaths)
                {
                    var mddFileText = new TextBlock
                    {
                        Text = Path.GetFileName(mddPath),
                        FontSize = 12,
                        Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    contentPanel.Children.Add(mddFileText);
                }
            }
            else
            {
                var noMddText = new TextBlock
                {
                    Text = loc.GetString("MddNoFilesDetected") != null
                        ? string.Format(loc.GetString("MddNoFilesDetected")!, string.Empty).Trim()
                        : "No companion MDD resource files found.",
                    FontSize = 12,
                    Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(8, 0, 0, 0)
                };
                contentPanel.Children.Add(noMddText);
            }

            // MDD button row: Add MDD + Re-scan
            var mddButtonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 4)
            };

            var capturedDictForMdd = dict;
            var addMddButton = new Button
            {
                Content = loc.GetString("MddAddFile") ?? "Add MDD File...",
                Padding = new Thickness(8, 4, 8, 4)
            };
            addMddButton.Click += async (s, args) =>
            {
                try
                {
                    var mainWindow = App.MainWindow;
                    if (mainWindow is null) return;

                    var path = await Services.Storage.PickerFactory.PickSingleFileAsync(
                        mainWindow,
                        Services.Storage.PickerFactory.SettingsIdentifiers.MddAdd,
                        new[] { ".mdd" });
                    if (string.IsNullOrWhiteSpace(path)) return;

                    if (!capturedDictForMdd.MddFilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        capturedDictForMdd.MddFilePaths.Add(path);
                        _settings.Save();

                        // Load into live service
                        using var h = TranslationManagerService.Instance.AcquireHandle();
                        if (h.Manager.Services.TryGetValue(capturedDictForMdd.ServiceId, out var svc)
                            && svc is MdxDictionaryTranslationService mdxSvc)
                        {
                            mdxSvc.LoadMddFiles([path]);
                        }

                        BuildImportedMdxConfigUI();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SettingsPage] Failed to add MDD file: {ex.Message}");
                }
            };
            mddButtonPanel.Children.Add(addMddButton);

            var rescanMddButton = new Button
            {
                Content = loc.GetString("MddRescanFiles") ?? "Re-scan MDD Files",
                Padding = new Thickness(8, 4, 8, 4)
            };
            var capturedDictForRescan = dict;
            rescanMddButton.Click += (s, args) =>
            {
                var discovered = MdxDictionaryTranslationService.DiscoverMddFiles(capturedDictForRescan.FilePath ?? string.Empty);
                capturedDictForRescan.MddFilePaths = discovered;
                _settings.Save();

                // Reconfigure services to reload MDD files
                TranslationManagerService.Instance.ReconfigureServices();
                BuildImportedMdxConfigUI();
            };
            mddButtonPanel.Children.Add(rescanMddButton);
            contentPanel.Children.Add(mddButtonPanel);

            // Button row: re-detect encryption + delete
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var redetectButton = new Button
            {
                Content = loc.GetString("MdxRedetectEncryption"),
                Padding = new Thickness(8, 4, 8, 4)
            };
            var capturedDictForRedetect = dict;
            redetectButton.Click += (s, args) =>
            {
                var btn = (Button)s!;
                btn.IsEnabled = false;
                try
                {
                    // Reconfigure services to re-detect encryption via constructor
                    TranslationManagerService.Instance.ReconfigureServices();

                    // Sync IsEncrypted from live services back to settings
                    foreach (var d in _settings.ImportedMdxDictionaries)
                    {
                        if (TranslationManagerService.Instance.Manager.Services.TryGetValue(d.ServiceId, out var svc)
                            && svc is MdxDictionaryTranslationService mdxSvc)
                        {
                            d.IsEncrypted = mdxSvc.IsEncrypted;
                        }
                    }
                    _settings.Save();

                    // Rebuild UI
                    BuildImportedMdxConfigUI();
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            };
            buttonPanel.Children.Add(redetectButton);

            var capturedDictForDelete = dict;
            var deleteButton = new Button
            {
                Content = loc.GetString("MdxDeleteDictionary"),
                Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                Padding = new Thickness(8, 4, 8, 4)
            };
            deleteButton.Click += async (s, args) =>
            {
                await DeleteMdxDictionaryAsync(capturedDictForDelete);
            };
            buttonPanel.Children.Add(deleteButton);

            contentPanel.Children.Add(buttonPanel);

            expander.Content = contentPanel;
            ImportedMdxConfigPanel.Children.Add(expander);
        }

#if DEBUG
        LogObjectState($"BuildImportedMdxConfigUI complete (dicts={allDicts.Count})");
#endif
    }

    /// <summary>
    /// Deletes an imported MDX dictionary after user confirmation.
    /// Removes from settings, service lists, test status, and TranslationManager.
    /// </summary>
    private async Task DeleteMdxDictionaryAsync(SettingsService.ImportedMdxDictionary dict)
    {
        var loc = LocalizationService.Instance;

        var confirmDialog = new ContentDialog
        {
            Title = loc.GetString("MdxDeleteConfirmTitle"),
            Content = string.Format(loc.GetString("MdxDeleteConfirmMessage"), dict.DisplayName),
            PrimaryButtonText = loc.GetString("MdxDeleteDictionary"),
            CloseButtonText = loc.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await ShowDialogAsync(confirmDialog);
        if (result != ContentDialogResult.Primary) return;

        var serviceId = dict.ServiceId;

        // Remove from imported dictionaries list
        _settings.ImportedMdxDictionaries.Remove(dict);

        // Remove from enabled services lists
        _settings.MainWindowEnabledServices.Remove(serviceId);
        _settings.MiniWindowEnabledServices.Remove(serviceId);
        _settings.FixedWindowEnabledServices.Remove(serviceId);

        // Remove from test status
        _settings.ServiceTestStatus.Remove(serviceId);

        // Remove from credential fields cache
        _mdxCredentialFields.Remove(serviceId);

        _settings.Save();

        // Unregister from TranslationManager
        TranslationManagerService.Instance.UnregisterMdxDictionary(serviceId);

        // Rebuild UI
        LoadSettings(MinimalThemeService.IsActive);
    }

    /// <summary>
    /// Persists encrypted MDX credential values from the dynamic UI into settings.
    /// Called by SaveSettingsAsync when the user clicks Save.
    /// </summary>
    private void SaveEncryptedMdxCredentials()
    {
        foreach (var dict in _settings.ImportedMdxDictionaries.Where(d => d.IsEncrypted))
        {
            if (_mdxCredentialFields.TryGetValue(dict.ServiceId, out var fields))
            {
                var email = fields.EmailBox.Text?.Trim();
                var regcode = fields.RegcodeBox.Password?.Trim();
                dict.Email = string.IsNullOrEmpty(email) ? null : email;
                dict.Regcode = string.IsNullOrEmpty(regcode) ? null : regcode;
            }
        }
    }

    /// <summary>
    /// Tests encrypted MDX dictionary credentials by attempting to open the dictionary
    /// with the provided email and registration code.
    /// </summary>
    private async Task TestEncryptedMdxAsync(
        SettingsService.ImportedMdxDictionary dict,
        TextBox emailBox,
        PasswordBox regcodeBox,
        Button testButton,
        TextBlock statusIndicator)
    {
        var loc = LocalizationService.Instance;
        var originalContent = testButton.Content;
        testButton.IsEnabled = false;
        testButton.Content = loc.GetString("Testing");

        try
        {
            var email = emailBox.Text?.Trim();
            var regcode = regcodeBox.Password?.Trim();

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(regcode))
            {
                await ShowTestFailureAsync("Please enter both email and registration code.");
                return;
            }

            // Try to open the dictionary with the provided credentials
            var testService = new MdxDictionaryTranslationService(
                dict.ServiceId, dict.DisplayName, dict.FilePath,
                regcode, email);

            if (!testService.IsConfigured)
            {
                throw new TranslationException("Invalid credentials — dictionary could not be decrypted.");
            }

            // Test a lookup to verify the dictionary is readable
            var request = new TranslationRequest
            {
                Text = "test",
                FromLanguage = TranslationLanguage.English,
                ToLanguage = TranslationLanguage.SimplifiedChinese
            };
            await testService.TranslateAsync(request);

            // Success — save credentials and reconfigure the live service
            dict.Regcode = regcode;
            dict.Email = email;
            _settings.ServiceTestStatus[dict.ServiceId] = true;
            _settings.Save();

            statusIndicator.Visibility = Visibility.Visible;

            // Reconfigure the live service in TranslationManager
            TranslationManagerService.Instance.ReconfigureServices();

            var successDialog = new ContentDialog
            {
                Title = loc.GetString("TestSuccessTitle"),
                Content = "Dictionary credentials verified successfully. The dictionary is now ready to use.",
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(successDialog);
        }
        catch (Exception ex)
        {
            _settings.ServiceTestStatus.Remove(dict.ServiceId);
            _settings.Save();
            statusIndicator.Visibility = Visibility.Collapsed;

            await ShowTestFailureAsync($"Credential verification failed: {ex.Message}");
        }
        finally
        {
            testButton.Content = originalContent;
            testButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Populate a service collection from TranslationManager.Services with enabled state and EnabledQuery settings.
    /// </summary>
    private static void PopulateServiceCollection(
        ObservableCollection<ServiceCheckItem> collection,
        List<string> enabledServices,
        Dictionary<string, bool> enabledQuerySettings,
        TranslationManager? sharedManager = null)
    {
        collection.Clear();

        var settings = SettingsService.Instance;
        var internationalEnabled = settings.EnableInternationalServices;

        var handle = sharedManager == null ? TranslationManagerService.Instance.AcquireHandle() : null;
        var manager = sharedManager ?? handle!.Manager;

        try
        {
            // Sort enabled services to the user's saved order; disabled services follow in
            // manager registration order. This preserves the up/down reorder UI choice.
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < enabledServices.Count; i++)
            {
                orderIndex[enabledServices[i]] = i;
            }

            var managerOrder = manager.Services.Keys.ToList();
            var ordered = managerOrder
                .Select((id, idx) => (id, idx))
                .OrderBy(t => orderIndex.TryGetValue(t.id, out var sortIdx) ? sortIdx : int.MaxValue)
                .ThenBy(t => GetSettingsServiceDisplayOrder(t.id, t.idx))
                .ThenBy(t => t.idx)
                .Select(t => t.id);

            foreach (var serviceId in ordered)
            {
                if (!manager.Services.TryGetValue(serviceId, out var service))
                    continue;

                // Default EnabledQuery is true (auto-query); use stored setting if available
                var enabledQuery = enabledQuerySettings.TryGetValue(serviceId, out var stored) ? stored : true;

                var isInternationalOnly = SettingsService.InternationalOnlyServices.Contains(serviceId);
                var isAvailable = internationalEnabled || !isInternationalOnly;

                var item = new ServiceCheckItem
                {
                    ServiceId = serviceId,
                    DisplayName = service.DisplayName,
                    IsChecked = isAvailable && enabledServices.Contains(serviceId),
                    EnabledQuery = enabledQuery,
                    IsAvailable = isAvailable,
                    IsUnconfigured = service.RequiresApiKey && !service.IsConfigured,
                    OriginBadgeText = ServiceOriginHelper.BadgeText(ServiceOriginHelper.Resolve(service, serviceId))
                };

                collection.Add(item);
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static int GetSettingsServiceDisplayOrder(string serviceId, int registrationIndex)
    {
        if (PreferredServiceDisplayOrder.TryGetValue(serviceId, out var sortIndex))
        {
            return sortIndex;
        }

        if (serviceId.StartsWith("mdx::", StringComparison.OrdinalIgnoreCase))
        {
            return 1000 + registrationIndex;
        }

        // Third-party plugins sit after everything Easydict ships, never interleaved with it.
        if (serviceId.StartsWith(BobServiceIds.Prefix, StringComparison.Ordinal))
        {
            return 1500 + registrationIndex;
        }

        return 2000 + registrationIndex;
    }

    private async void OnImportMdxDictionaryClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var mainWindow = App.MainWindow;
            if (mainWindow is null) return;

            var path = await Services.Storage.PickerFactory.PickSingleFileAsync(
                mainWindow,
                Services.Storage.PickerFactory.SettingsIdentifiers.MdxImport,
                new[] { ".mdx" });
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var displayName = Path.GetFileNameWithoutExtension(path);
            var serviceId = BuildMdxServiceId(displayName, path);

            // Discover companion MDD resource files
            var mddFiles = MdxDictionaryTranslationService.DiscoverMddFiles(path);

            var imported = new SettingsService.ImportedMdxDictionary
            {
                ServiceId = serviceId,
                DisplayName = $"📚 {displayName}",
                FilePath = path,
                MddFilePaths = mddFiles
            };

            if (!TranslationManagerService.Instance.TryRegisterMdxDictionary(imported, out var error))
            {
                await ShowSimpleDialogAsync("Import failed", $"Unable to load MDX dictionary: {error}");
                return;
            }

            // Load MDD files into the registered service
            using var handle = TranslationManagerService.Instance.AcquireHandle();
            if (handle.Manager.Services.TryGetValue(serviceId, out var service) &&
                service is MdxDictionaryTranslationService mdxService)
            {
                if (mdxService.IsEncrypted)
                    imported.IsEncrypted = true;

                if (mddFiles.Count > 0)
                    mdxService.LoadMddFiles(mddFiles);
            }

            _settings.ImportedMdxDictionaries.RemoveAll(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
            _settings.ImportedMdxDictionaries.Add(imported);

            // Enable imported service by default in all windows.
            if (!_settings.MainWindowEnabledServices.Contains(serviceId)) _settings.MainWindowEnabledServices.Add(serviceId);
            if (!_settings.MiniWindowEnabledServices.Contains(serviceId)) _settings.MiniWindowEnabledServices.Add(serviceId);
            if (!_settings.FixedWindowEnabledServices.Contains(serviceId)) _settings.FixedWindowEnabledServices.Add(serviceId);

            _settings.Save();
            LoadSettings(MinimalThemeService.IsActive);

            var loc = LocalizationService.Instance;
            if (imported.IsEncrypted)
            {
                await ShowSimpleDialogAsync("Encrypted Dictionary",
                    "This dictionary is encrypted. Please configure your credentials (Email and Registration Code) " +
                    "in the Service Configuration section below.");
            }
            else if (mddFiles.Count > 0)
            {
                var msg = string.Format(loc.GetString("MddFilesDetected") ?? "Imported '{0}' with {1} companion resource file(s).",
                    displayName, mddFiles.Count);
                await ShowSimpleDialogAsync("Import Successful", msg);
            }
            else
            {
                var msg = string.Format(loc.GetString("MddNoFilesDetected") ?? "Imported '{0}'. No companion MDD resource files found.",
                    displayName);
                await ShowSimpleDialogAsync("Import Successful", msg);
            }
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("Import failed", ex.Message);
        }
    }

    internal static string BuildMdxServiceId(string displayName, string filePath)
    {
        var stableHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(filePath))).ToLowerInvariant()[..8];
        var normalizedName = displayName.Trim().ToLowerInvariant().Replace(' ', '-');
        normalizedName = NonServiceIdCharRegex.Replace(normalizedName, "-");
        normalizedName = Regex.Replace(normalizedName, "-+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            normalizedName = "dictionary";
        }
        return $"mdx::{normalizedName}-{stableHash}";
    }

    private async Task ShowSimpleDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        };
        await ShowDialogAsync(dialog);
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        // Default to first item
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string? GetSelectedTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString();
        }
        return null;
    }

    private OcrServiceOptions GetCurrentOcrServiceOptions()
    {
        var engine = GetSelectedOcrEngine();

        return new OcrServiceOptions(
            engine,
            OcrApiKeyBox.Password,
            OcrEndpointBox.Text,
            OcrModelBox.Text,
            OcrSystemPromptBox.Text,
            OcrEnableThinkingToggle.IsOn);
    }

    /// <summary>
    /// Gets the value from an editable ComboBox. Returns the typed text if available,
    /// otherwise returns the selected item's tag.
    /// </summary>
    private static string GetEditableComboValue(ComboBox combo, string defaultValue)
    {
        // For editable ComboBox, prefer Text (user-typed value)
        var text = combo.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            // If text matches a dropdown item's Content, return its Tag (programmatic value).
            // This handles cases where Content differs from Tag (e.g., "glm-4-flash-250414 (GLM)" → "glm-4-flash-250414").
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Content?.ToString() == text && item.Tag != null)
                {
                    return item.Tag.ToString() ?? defaultValue;
                }
            }
            // Custom user-typed value
            return text;
        }
        // Fall back to selected item's tag
        if (combo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag != null)
        {
            return selectedItem.Tag.ToString() ?? defaultValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Sets the value for an editable ComboBox. If the value matches a dropdown item,
    /// selects it. Otherwise, sets the Text property for custom values.
    /// </summary>
    private static void SetEditableComboValue(ComboBox combo, string value)
    {
        // Try to find matching item in dropdown
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        // Custom value - set Text directly
        combo.Text = value;
    }

    private static void ReplaceEditableComboOptions(
        ComboBox combo,
        IReadOnlyList<string> options,
        string selectedValue)
    {
        combo.Items.Clear();
        foreach (var option in options)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = option,
                Tag = option,
            });
        }

        SetEditableComboValue(combo, selectedValue);
    }

    /// <summary>
    /// Repopulate an editable ComboBox from a fetched model catalog: <see cref="ModelCatalogEntry.DisplayLabel"/>
    /// (which reads "id — name  (Free)") is shown, but <see cref="ModelCatalogEntry.Id"/> stays the
    /// item's Tag, which is what settings persist and what <see cref="GetEditableComboValue"/> returns.
    /// </summary>
    private static void ReplaceEditableComboOptions(
        ComboBox combo,
        IReadOnlyList<ModelCatalogEntry> models,
        string selectedValue)
    {
        combo.Items.Clear();
        foreach (var model in models)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = model.DisplayLabel,
                Tag = model.Id,
            });
        }

        SetEditableComboValue(combo, selectedValue);
    }

    /// <summary>
    /// Select the item whose Tag matches <paramref name="value"/> (case-insensitive),
    /// falling back to the item with Tag = <paramref name="defaultTag"/>.
    /// For non-editable combos.
    /// </summary>
    private static void SetTagComboValue(ComboBox combo, string? value, string defaultTag)
    {
        var target = string.IsNullOrEmpty(value) ? defaultTag : value;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        // Fall back to default tag if the saved value is unrecognized.
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), defaultTag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static string GetTagComboValue(ComboBox combo, string defaultTag)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrEmpty(tag))
        {
            return tag;
        }
        return defaultTag;
    }

    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        // Deferred native TextChanged events can arrive after a tab has loaded.
        // Decide from the current editors, rather than a transient dirty flag.
        _hasUnsavedChanges = HasAnyTrackedSettingChanges();
        if (_hasUnsavedChanges)
        {
            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("UnsavedChangesTitle"),
                Content = loc.GetString("UnsavedChangesMessage"),
                PrimaryButtonText = loc.GetString("SaveSettings"),
                SecondaryButtonText = loc.GetString("DontSave"),
                CloseButtonText = loc.GetString("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await ShowDialogAsync(dialog);

            if (result == ContentDialogResult.Primary)
            {
                await ShowNavigationLoadingOverlayAsync();
                // Save and then go back
                var saved = await SaveSettingsAsync();
                if (!saved)
                {
                    HideNavigationLoadingOverlay();
                    return; // Validation failed, stay on page
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ShowNavigationLoadingOverlayAsync();
                // Discard changes — restore SelectedLanguages to pre-edit snapshot
                _settings.SelectedLanguages = _originalSelectedLanguages;
                _hasUnsavedChanges = false;
            }
            else
            {
                // Cancel - stay on settings page
                return;
            }
        }
        else
        {
            await ShowNavigationLoadingOverlayAsync();
        }

        if (Frame is not null)
        {
            var frame = Frame;

            _releaseVisualTreeImmediatelyOnUnload = true;
            bool navigated;
            try
            {
                navigated = frame.Navigate(typeof(MainPage));
            }
            catch (Exception ex)
            {
                _releaseVisualTreeImmediatelyOnUnload = false;
                Debug.WriteLine($"[SettingsPage] Failed to navigate back to MainPage: {ex.Message}");
                HideNavigationLoadingOverlay();
                return;
            }

            if (navigated)
            {
                // Settings owns a large, lazily-inflated tree for service/window controls.
                // Release it only after MainPage navigation succeeds so a navigation failure
                // cannot leave the frame on a blank Settings page.
                QueueTeardownOnUnload(deferVisualTreeRelease: false);
                _releaseVisualTreeImmediatelyOnUnload = false;
                frame.BackStack.Clear();
                frame.ForwardStack.Clear();
            }
            else
            {
                _releaseVisualTreeImmediatelyOnUnload = false;
                HideNavigationLoadingOverlay();
            }
        }
        else
        {
            HideNavigationLoadingOverlay();
        }
    }

    private async Task ShowNavigationLoadingOverlayAsync()
    {
        if (NavigationLoadingOverlay is null || NavigationLoadingRing is null)
        {
            return;
        }

        NavigationLoadingOverlay.Visibility = Visibility.Visible;
        NavigationLoadingRing.IsActive = true;
        NavigationLoadingRing.Visibility = Visibility.Visible;
        BackButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        MainScrollViewer.IsEnabled = false;

        await Task.Delay(50);
    }

    private void HideNavigationLoadingOverlay()
    {
        if (NavigationLoadingOverlay is null || NavigationLoadingRing is null)
        {
            return;
        }

        NavigationLoadingRing.IsActive = false;
        NavigationLoadingOverlay.Visibility = Visibility.Collapsed;
        BackButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
        MainScrollViewer.IsEnabled = true;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var saved = await SaveSettingsAsync();
        if (!saved) return;

        // Show confirmation
        var loc = LocalizationService.Instance;
        var dialog = new ContentDialog
        {
            Title = loc.GetString("SettingsSaved"),
            Content = loc.GetString("SettingsSavedMessage"),
            CloseButtonText = loc.GetString("OK"),
            XamlRoot = this.XamlRoot
        };
        await ShowDialogAsync(dialog);
    }


    /// <summary>
    /// Shared handler for all hotkey AutoSuggestBoxes: offers autocomplete while
    /// typing and validates the text in real time, showing an inline error for
    /// combinations that could not be registered.
    /// </summary>
    private void OnHotkeyBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = HotkeySuggestionProvider.GetSuggestions(sender.Text);
        }
        UpdateHotkeyErrorText(sender);
    }

    private void UpdateHotkeyErrorText(AutoSuggestBox box)
    {
        var errorText = GetHotkeyErrorTextBlock(box);
        if (errorText is null) return;

        var text = box.Text?.Trim() ?? "";
        var isInvalid = text.Length > 0 && !HotkeyParser.IsValidCombination(text);
        if (isInvalid)
        {
            errorText.Text = LocalizationService.Instance.GetString("InvalidHotkeyInlineError");
        }
        errorText.Visibility = isInvalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private TextBlock? GetHotkeyErrorTextBlock(AutoSuggestBox box)
    {
        if (ReferenceEquals(box, ShowHotkeyBox)) return ShowHotkeyErrorText;
        if (ReferenceEquals(box, TranslateHotkeyBox)) return TranslateHotkeyErrorText;
        if (ReferenceEquals(box, ShowMiniHotkeyBox)) return ShowMiniHotkeyErrorText;
        if (ReferenceEquals(box, ShowFixedHotkeyBox)) return ShowFixedHotkeyErrorText;
        if (ReferenceEquals(box, OcrTranslateHotkeyBox)) return OcrTranslateHotkeyErrorText;
        if (ReferenceEquals(box, SilentOcrHotkeyBox)) return SilentOcrHotkeyErrorText;
        return null;
    }

    /// <summary>
    /// Adds a "label: text" line to <paramref name="invalid"/> when an enabled
    /// hotkey's text is not a registrable combination.
    /// </summary>
    private static void CollectInvalidHotkey(bool enabled, string? text, string nameKey, List<string> invalid)
    {
        if (!enabled || HotkeyParser.IsValidCombination(text)) return;

        var label = LocalizationService.Instance.GetString(nameKey);
        invalid.Add(string.IsNullOrWhiteSpace(text) ? label : $"{label}: {text}");
    }

    /// <summary>
    /// True when any enabled hotkey in the saved settings is bound to Win+Space.
    /// </summary>
    private bool HasEnabledWinSpaceHotkey()
    {
        return (_settings.EnableShowWindowHotkey && HotkeyParser.IsWinSpace(_settings.ShowWindowHotkey))
            || (_settings.EnableTranslateSelectionHotkey && HotkeyParser.IsWinSpace(_settings.TranslateSelectionHotkey))
            || (_settings.EnableShowMiniWindowHotkey && HotkeyParser.IsWinSpace(_settings.ShowMiniWindowHotkey))
            || (_settings.EnableShowFixedWindowHotkey && HotkeyParser.IsWinSpace(_settings.ShowFixedWindowHotkey))
            || (_settings.EnableOcrTranslateHotkey && HotkeyParser.IsWinSpace(_settings.OcrTranslateHotkey))
            || (_settings.EnableSilentOcrHotkey && HotkeyParser.IsWinSpace(_settings.SilentOcrHotkey));
    }

    private void OnMouseSelectionTranslateToggled(object sender, RoutedEventArgs e)
    {
        MouseSelectionOptionsPanel.Visibility = MouseSelectionTranslateToggle.IsOn
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHoverWordLookupEnabledChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnHoverWordLookupEnabledChanged(sender, e));
            return;
        }
        if (_isUnloaded || _isTornDown || HoverWordLookupToggle.IsOn == _settings.HoverWordLookupEnabled) return;

        var wasLoading = _isLoading;
        _isLoading = true;
        try { HoverWordLookupToggle.IsOn = _settings.HoverWordLookupEnabled; }
        finally { _isLoading = wasLoading; }
        // A tray change is already persisted; keep any other pending page edits.
        OnSettingChanged(HoverWordLookupToggle, e);
    }

    private void OnHoverWordLookupDelayChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        HoverWordLookupDelayValueText.Text = $"{(int)Math.Round(e.NewValue)} ms";
        OnSettingChanged(sender, e);
    }

    private void OnHoverWordLookupToggled(object sender, RoutedEventArgs e)
    {
        HoverWordLookupPanel.Visibility = HoverWordLookupToggle.IsOn
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private ComboBoxItem? _hoverWordLookupServiceAutoItem;

    /// <summary>
    /// Fill the hover word lookup service combo: "Auto" plus the services enabled in the
    /// Mini/Main windows (user order). A saved id that is registered but no longer enabled is
    /// kept so the selection round-trips.
    /// </summary>
    private void PopulateHoverWordLookupServiceCombo()
    {
        var loc = LocalizationService.Instance;
        HoverWordLookupServiceCombo.Items.Clear();

        _hoverWordLookupServiceAutoItem = new ComboBoxItem
        {
            Content = loc.GetString("HoverWordLookupServiceAuto"),
            Tag = ""
        };
        HoverWordLookupServiceCombo.Items.Add(_hoverWordLookupServiceAutoItem);

        try
        {
            using var handle = TranslationManagerService.Instance.AcquireHandle();
            var services = handle.Manager.Services;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = _settings.MiniWindowEnabledServices
                .Concat(_settings.MainWindowEnabledServices)
                .Append(_settings.HoverWordLookupServiceId ?? "");

            foreach (var serviceId in candidates)
            {
                if (string.IsNullOrWhiteSpace(serviceId) || !seen.Add(serviceId))
                    continue;
                if (!services.TryGetValue(serviceId, out var service))
                    continue;

                HoverWordLookupServiceCombo.Items.Add(new ComboBoxItem
                {
                    Content = service.DisplayName,
                    Tag = serviceId
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] Failed to enumerate services for hover lookup: {ex.Message}");
        }
    }

    private void OnHistoryEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var previousValue = _settings.HistoryEnabled;
        try
        {
            _settings.HistoryEnabled = HistoryEnabledToggle.IsOn;
            _settings.Save();
            HistoryRetentionDaysBox.IsEnabled = HistoryEnabledToggle.IsOn;
        }
        catch (Exception exception)
        {
            _settings.HistoryEnabled = previousValue;
            HistoryEnabledToggle.IsOn = previousValue;
            HistoryRetentionDaysBox.IsEnabled = previousValue;
            HistoryRetentionValidationText.Text = exception.Message;
            HistoryRetentionValidationText.Foreground = ThemeResourceService.GetBrush("SystemFillColorCriticalBrush", this);
            HistoryRetentionValidationText.Visibility = Visibility.Visible;
        }
    }

    private int _historyRetentionGeneration;

    private async void OnHistoryRetentionDaysValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_isLoading || double.IsNaN(e.NewValue))
        {
            return;
        }

        var value = Math.Clamp((int)Math.Round(e.NewValue), 1, 3650);
        var previousValue = _settings.HistoryRetentionDays;
        if (value == previousValue) return;
        var generation = ++_historyRetentionGeneration;
        var persisted = false;
        try
        {
            _settings.HistoryRetentionDays = value;
            _settings.Save();
            persisted = true;
            if (Math.Abs(HistoryRetentionDaysBox.Value - value) > double.Epsilon)
            {
                _isLoading = true;
                try { HistoryRetentionDaysBox.Value = value; }
                finally { _isLoading = false; }
            }
            HistoryRetentionValidationText.Visibility = Visibility.Collapsed;
            if (value < previousValue)
            {
                await SavedItemsService.Instance.PruneExpiredHistoryAsync(_lifetimeCts.Token);
            }
            if (generation != _historyRetentionGeneration) return;
            HistoryRetentionValidationText.Text = LocalizationService.Instance.GetString("HistoryRetentionUpdated");
            HistoryRetentionValidationText.Foreground = ThemeResourceService.GetBrush("SystemFillColorSuccessBrush", this);
            HistoryRetentionValidationText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            if (generation != _historyRetentionGeneration) return;
            if (!persisted)
            {
                _settings.HistoryRetentionDays = previousValue;
                _isLoading = true;
                try { HistoryRetentionDaysBox.Value = previousValue; }
                finally { _isLoading = false; }
            }
            HistoryRetentionValidationText.Text = exception.Message;
            HistoryRetentionValidationText.Foreground = ThemeResourceService.GetBrush("SystemFillColorCriticalBrush", this);
            HistoryRetentionValidationText.Visibility = Visibility.Visible;
        }
    }

    private async void OnClearHistoryClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = LocalizationService.Instance.GetString("HistoryClearTitle"),
            Content = LocalizationService.Instance.GetString("HistoryClearDescription"),
            PrimaryButtonText = LocalizationService.Instance.GetString("HistoryClearLabel"),
            CloseButtonText = LocalizationService.Instance.GetString("Cancel")
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        ClearHistoryButton.IsEnabled = false;
        try
        {
            await SavedItemsService.Instance.ClearHistoryAsync(_lifetimeCts.Token);
            HistoryRetentionValidationText.Text = LocalizationService.Instance.GetString("HistoryCleared");
            HistoryRetentionValidationText.Foreground = ThemeResourceService.GetBrush("SystemFillColorSuccessBrush", this);
            HistoryRetentionValidationText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            HistoryRetentionValidationText.Text = exception.Message;
            HistoryRetentionValidationText.Foreground = ThemeResourceService.GetBrush("SystemFillColorCriticalBrush", this);
            HistoryRetentionValidationText.Visibility = Visibility.Visible;
        }
        finally
        {
            ClearHistoryButton.IsEnabled = true;
        }
    }

    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            ApplyOcrEngineDefaultsIfNeeded(GetSelectedOcrEngine());
        }

        UpdateOcrEngineUI();
        OnSettingChanged(sender, e);
    }

    private void UpdateOcrEngineUI()
    {
        var engine = GetSelectedOcrEngine();
        var isAdvanced = engine is OcrEngineType.Ollama or OcrEngineType.CustomApi;
        AdvancedOcrPanel.Visibility = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
        OcrEndpointBox.PlaceholderText = OcrServiceOptions.GetDefaultEndpoint(engine);
        OcrModelBox.PlaceholderText = OcrServiceOptions.GetDefaultModel(engine);
    }

    private void OnPasswordRevealButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PasswordBox passwordBox)
        {
            return;
        }

        var isVisible = !_visiblePasswordBoxes.TryGetValue(passwordBox, out var wasVisible) || !wasVisible;
        _visiblePasswordBoxes[passwordBox] = isVisible;
        passwordBox.PasswordRevealMode = isVisible ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;

        UpdatePasswordRevealButtonState(button, LocalizationService.Instance);
    }

    private OcrEngineType GetSelectedOcrEngine()
    {
        return Enum.TryParse<OcrEngineType>(GetSelectedTag(OcrEngineCombo), out var parsedEngine)
            ? parsedEngine
            : OcrEngineType.WindowsNative;
    }

    private void ApplyOcrEngineDefaultsIfNeeded(OcrEngineType engine)
    {
        if (OcrServiceOptions.IsKnownDefaultEndpoint(OcrEndpointBox.Text))
        {
            OcrEndpointBox.Text = OcrServiceOptions.GetDefaultEndpoint(engine);
        }

        if (OcrServiceOptions.IsKnownDefaultModel(OcrModelBox.Text))
        {
            OcrModelBox.Text = OcrServiceOptions.GetDefaultModel(engine);
        }
    }

    private async void OnTestOcrConnection(object sender, RoutedEventArgs e)
    {
        OcrTestStatusBox.Text = "Select a region on your screen to test OCR...";
        TestOcrConnectionButton.IsEnabled = false;

        try
        {
            var captureService = new ScreenCaptureService();
            var capture = await captureService.CaptureRegionAsync();

            if (capture == null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    OcrTestStatusBox.Text = "Test cancelled.";
                });
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                OcrTestStatusBox.Text = "Processing image with AI...";
            });

            using (capture)
            {
                using var httpClient = OcrServiceFactory.CreateProxyAwareHttpClient(
                    ProxyEnabledToggle.IsOn,
                    ProxyUriBox.Text,
                    ProxyBypassLocalToggle.IsOn);
                var ocrEngine = OcrServiceFactory.Create(GetCurrentOcrServiceOptions(), httpClient);
                try
                {
                    var result = await ocrEngine.RecognizeAsync(capture, null, CancellationToken.None);

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (string.IsNullOrWhiteSpace(result.Text))
                        {
                            OcrTestStatusBox.Text = "Success: Image processed, but no text was recognized.";
                        }
                        else
                        {
                            OcrTestStatusBox.Text = result.Text;
                        }
                    });
                }
                finally
                {
                    if (ocrEngine is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync();
                    }
                    else
                    {
                        (ocrEngine as IDisposable)?.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                OcrTestStatusBox.Text = $"[Error] {ex.Message}";
            });
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TestOcrConnectionButton.IsEnabled = true;
            });
        }
    }


    /// <summary>
    /// Get list of enabled services from a collection.
    /// </summary>
    private static List<string> GetEnabledServicesFromCollection(ObservableCollection<ServiceCheckItem> collection)
    {
        return collection
            .Where(item => item.IsChecked)
            .Select(item => item.ServiceId)
            .ToList();
    }

    /// <summary>
    /// Ensure at least one service is checked when the collection has no services selected.
    /// Uses region-appropriate default (bing for China, google for international),
    /// falling back to the first available service if the default is unavailable.
    /// Updates both the settings and the collection item.
    /// </summary>
    private static void EnsureDefaultServiceEnabled(ObservableCollection<ServiceCheckItem> collection, List<string> services)
    {
        if (services.Count > 0) return;

        var defaultServiceId = SettingsService.GetRegionDefaultServiceId();

        // If the region default is not available, pick the first available service
        var defaultItem = collection.FirstOrDefault(item => item.ServiceId == defaultServiceId && item.IsAvailable)
                       ?? collection.FirstOrDefault(item => item.IsAvailable);

        if (defaultItem != null)
        {
            services.Add(defaultItem.ServiceId);
            defaultItem.IsChecked = true;
        }
    }

    /// <summary>
    /// Handle Enable International Services toggle change.
    /// Updates IsAvailable on all service items and unchecks unavailable services.
    /// </summary>
    private void OnEnableInternationalServicesToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var enabled = EnableInternationalServicesToggle.IsOn;

        UpdateServiceAvailability(_mainWindowServices, enabled);
        UpdateServiceAvailability(_miniWindowServices, enabled);
        UpdateServiceAvailability(_fixedWindowServices, enabled);

        // Ensure at least one available service is still enabled after unchecking unavailable ones
        var mainServices = GetEnabledServicesFromCollection(_mainWindowServices);
        var miniServices = GetEnabledServicesFromCollection(_miniWindowServices);
        var fixedServices = GetEnabledServicesFromCollection(_fixedWindowServices);
        EnsureDefaultServiceEnabled(_mainWindowServices, mainServices);
        EnsureDefaultServiceEnabled(_miniWindowServices, miniServices);
        EnsureDefaultServiceEnabled(_fixedWindowServices, fixedServices);

        OnSettingChanged(sender, e);
    }

    /// <summary>
    /// Update IsAvailable and uncheck unavailable services in a collection.
    /// </summary>
    private static void UpdateServiceAvailability(ObservableCollection<ServiceCheckItem> collection, bool internationalEnabled)
    {
        foreach (var item in collection)
        {
            var isInternationalOnly = SettingsService.InternationalOnlyServices.Contains(item.ServiceId);
            item.IsAvailable = internationalEnabled || !isInternationalOnly;

            // Uncheck unavailable services
            if (!item.IsAvailable && item.IsChecked)
            {
                item.IsChecked = false;
            }
        }

        // Sort: available items first, unavailable items last (preserve relative order within each group)
        var sorted = collection.OrderBy(item => item.IsAvailable ? 0 : 1).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = collection.IndexOf(sorted[i]);
            if (currentIndex != i)
                collection.Move(currentIndex, i);
        }
    }

    /// <summary>
    /// Handle app theme selection change.
    /// </summary>
    private void OnAppThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        var selectedTag = GetSelectedTag(AppThemeCombo);
        if (string.IsNullOrEmpty(selectedTag)) return;

        System.Diagnostics.Debug.WriteLine($"[SettingsPage] User selected theme: {selectedTag}");

        // Save the theme setting
        _settings.AppTheme = selectedTag;
        _settings.Save();

        // Apply theme immediately
        App.ApplyTheme(selectedTag);

        if (!HasAnyTrackedSettingChanges())
        {
            _hasUnsavedChanges = false;
            SaveButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Handle UI language selection change.
    /// </summary>
    private async void OnUILanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;

        var selectedTag = GetSelectedTag(UILanguageCombo);
        if (string.IsNullOrEmpty(selectedTag)) return;

        try
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] User selected language: {selectedTag}");

            // Set the language (this also saves to settings)
            LocalizationService.Instance.SetLanguage(selectedTag);

            System.Diagnostics.Debug.WriteLine($"[SettingsPage] Language set and saved successfully");

            // Show restart required message
            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("RestartRequired"),
                Content = loc.GetString("RestartRequiredMessage"),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(dialog);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsPage] ERROR: Failed to save language: {ex.Message}");

            // Show error dialog to user
            var loc = LocalizationService.Instance;
            var errorDialog = new ContentDialog
            {
                Title = loc.GetString("StatusError"),
                Content = $"Failed to save language setting: {ex.Message}\n\nPlease check if the application has write permissions.",
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(errorDialog);
        }
    }

    /// <summary>
    /// Get EnabledQuery dictionary from a service collection.
    /// Only includes services that are checked (enabled).
    /// </summary>
    private static Dictionary<string, bool> GetEnabledQueryFromCollection(ObservableCollection<ServiceCheckItem> collection)
    {
        var dict = new Dictionary<string, bool>();
        foreach (var item in collection)
        {
            if (item.IsChecked)
            {
                dict[item.ServiceId] = item.EnabledQuery;
            }
        }
        return dict;
    }

    #region Service Test Handlers

    /// <summary>
    /// Creates a fresh, isolated service instance for testing.
    /// This ensures the test uses current UI values without modifying the shared service.
    /// </summary>
    private static ITranslationService? CreateFreshService(string serviceId, HttpClient httpClient)
    {
        return serviceId switch
        {
            "deepl" => new DeepLService(httpClient),
            "openai" => new OpenAIService(httpClient),
            "ollama" => new OllamaService(httpClient),
            "deepseek" => new DeepSeekService(httpClient),
            "groq" => new GroqService(httpClient),
            "zhipu" => new ZhipuService(httpClient),
            "kimi" => new KimiService(httpClient),
            "github" => new GitHubModelsService(httpClient),
            "gemini" => new GeminiService(httpClient),
            "custom-openai" => new CustomOpenAIService(httpClient),
            "openrouter" => new OpenRouterService(httpClient),
            "orcarouter" => new OrcaRouterService(httpClient),
            "doubao" => new DoubaoService(httpClient),
            "caiyun" => new CaiyunService(httpClient),
            "niutrans" => new NiuTransService(httpClient),
            "volcano" => new VolcanoService(httpClient),
            "claude-code" => new ClaudeCodeService(httpClient),
            "codex" => new CodexCliService(httpClient),
            _ => null
        };
    }

    /// <summary>
    /// Test a translation service with current UI configuration.
    /// </summary>
    /// <param name="serviceId">The service ID to test.</param>
    /// <param name="configureAction">Action to configure the service with current UI values.</param>
    /// <param name="testButton">The test button to disable during testing.</param>
    /// <param name="statusIndicator">Optional TextBlock to show success indicator.</param>
    private async Task TestServiceAsync(string serviceId, Action<ITranslationService> configureAction, Button testButton, TextBlock? statusIndicator = null, Action<ITranslationService>? onSuccess = null)
    {
        var loc = LocalizationService.Instance;
        var originalContent = testButton.Content;

        testButton.IsEnabled = false;
        testButton.Content = loc.GetString("Testing");

        try
        {
            // Create an isolated service instance so the test uses UI values
            // and doesn't modify the shared service in TranslationManager.
            using var handle = TranslationManagerService.Instance.AcquireHandle();
            var service = CreateFreshService(serviceId, handle.Manager.SharedHttpClient);
            if (service == null)
            {
                throw new TranslationException($"Service '{serviceId}' not found");
            }

            // Configure the fresh instance with current UI values
            configureAction(service);

            // Check if configured
            if (!service.IsConfigured)
            {
                await ShowTestFailureAsync(loc.GetString("TestNotConfigured"));
                return;
            }

            // Create a simple test request
            var request = new TranslationRequest
            {
                Text = "hello",
                FromLanguage = TranslationLanguage.English,
                ToLanguage = TranslationLanguage.SimplifiedChinese
            };

            // Run the test translation
            var result = await service.TranslateAsync(request);

            // Let callers inspect post-translate state (e.g. detected API format).
            onSuccess?.Invoke(service);

            // Show success indicator on expander header
            if (statusIndicator != null)
            {
                statusIndicator.Visibility = Visibility.Visible;
            }

            // Save test success status
            _settings.ServiceTestStatus[serviceId] = true;
            _settings.Save();

            // Show success
            var successDialog = new ContentDialog
            {
                Title = loc.GetString("TestSuccessTitle"),
                Content = string.Format(loc.GetString("TestSuccessMessage"), result.TimingMs),
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(successDialog);
        }
        catch (TranslationException ex)
        {
            // Clear test passed status and hide indicator
            _settings.ServiceTestStatus.Remove(serviceId);
            _settings.Save();
            if (statusIndicator != null)
            {
                statusIndicator.Visibility = Visibility.Collapsed;
            }

            await ShowTestFailureAsync(string.Format(
                loc.GetString("TestFailedMessage"),
                Controls.ServiceResultStatusTextProvider.GetErrorText(ex)));
        }
        catch (Exception ex)
        {
            // Clear test passed status and hide indicator
            _settings.ServiceTestStatus.Remove(serviceId);
            _settings.Save();
            if (statusIndicator != null)
            {
                statusIndicator.Visibility = Visibility.Collapsed;
            }

            await ShowTestFailureAsync(string.Format(loc.GetString("TestFailedMessage"), ex.Message));
        }
        finally
        {
            testButton.Content = originalContent;
            testButton.IsEnabled = true;
        }
    }











    /// <summary>
    /// Test Custom OpenAI configuration.
    /// </summary>
    private async void OnTestCustomOpenAI(object sender, RoutedEventArgs e)
    {
        await TestServiceAsync("custom-openai", service =>
        {
            if (service is CustomOpenAIService customOpenai)
            {
                var endpoint = CustomOpenAIEndpointBox.Text?.Trim() ?? "";
                var apiKey = CustomOpenAIKeyBox.Password;
                var model = CustomOpenAIModelBox.Text?.Trim();
                customOpenai.Configure(
                    endpoint,
                    string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
                    string.IsNullOrWhiteSpace(model) ? "gpt-3.5-turbo" : model);
            }
        }, TestCustomOpenAIButton, CustomOpenAIStatusText);
    }







    private async void OnClaudeCodeExpanding(Expander sender, ExpanderExpandingEventArgs e)
    {
        if (_hasRefreshedClaudeCodeModels) return;

        _hasRefreshedClaudeCodeModels = true;
        await RefreshClaudeCodeModelsAsync();
    }

    private async void OnRefreshClaudeCodeModels(object sender, RoutedEventArgs e)
    {
        await RefreshClaudeCodeModelsAsync();
    }


    private async void OnCodexExpanding(Expander sender, ExpanderExpandingEventArgs e)
    {
        if (_hasRefreshedCodexModels) return;

        _hasRefreshedCodexModels = true;
        await RefreshCodexModelsAsync();
    }

    private async void OnRefreshCodexModels(object sender, RoutedEventArgs e)
    {
        await RefreshCodexModelsAsync();
    }














    private static async Task<IReadOnlyList<ModelCatalogEntry>> FetchOpenRouterModelsAsync()
    {
        using var handle = TranslationManagerService.Instance.AcquireHandle();
        var service = new OpenRouterService(handle.Manager.SharedHttpClient);
        return await service.FetchModelsAsync();
    }

    private async void OnOrcaRouterExpanding(Expander sender, ExpanderExpandingEventArgs e)
    {
        if (_hasRefreshedOrcaRouterModels) return;

        _hasRefreshedOrcaRouterModels = true;
    }

    private async void OnRefreshOrcaRouterModels(object sender, RoutedEventArgs e)
    {
    }

    private async Task<IReadOnlyList<ModelCatalogEntry>> FetchOrcaRouterModelsAsync()
    {
        using var handle = TranslationManagerService.Instance.AcquireHandle();
        var service = new OrcaRouterService(handle.Manager.SharedHttpClient);
        return await service.FetchModelsAsync();
    }

    /// <summary>
    /// Populates an OpenAI-compatible service's model dropdown from the on-disk catalog cache,
    /// refetching from the network when the cache is missing/stale (or when
    /// <paramref name="forceRefresh"/> is set, e.g. from an explicit Refresh button click).
    /// A failed lazy (non-forced) fetch falls back to a stale cache, then to the dropdown's
    /// existing seed items, and stays silent — opening Settings offline must never pop an error
    /// dialog. A failed forced refresh shows the same "test failed" dialog used elsewhere on
    /// this page when <paramref name="showErrorDialog"/> is set.
    /// </summary>
    private async Task RefreshCatalogModelsAsync(
        string serviceId,
        ComboBox combo,
        Button refreshButton,
        string defaultModel,
        bool forceRefresh,
        bool showErrorDialog,
        Func<Task<IReadOnlyList<ModelCatalogEntry>>> fetchAsync)
    {
        var selectedModel = GetEditableComboValue(combo, defaultModel);

        if (!forceRefresh)
        {
            var fresh = await _modelCatalogCache.TryGetFreshAsync(serviceId);
            if (fresh is { Count: > 0 })
            {
                ReplaceEditableComboOptions(combo, fresh, selectedModel);
                return;
            }
        }

        refreshButton.IsEnabled = false;
        try
        {
            var models = await fetchAsync();
            if (models.Count > 0)
            {
                ReplaceEditableComboOptions(combo, models, selectedModel);
                await _modelCatalogCache.SaveAsync(serviceId, models);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to refresh {serviceId} models: {ex.Message}");

            var stale = await _modelCatalogCache.TryGetAnyAsync(serviceId);
            if (stale is { Count: > 0 })
            {
                ReplaceEditableComboOptions(combo, stale, selectedModel);
            }

            if (showErrorDialog)
            {
                await ShowAgentCliErrorAsync(ex);
            }
        }
        finally
        {
            if (!_isUnloaded)
            {
                refreshButton.IsEnabled = true;
            }
        }
    }

    private async Task RefreshAgentCliModelsAsync(
        ComboBox combo,
        Button refreshButton,
        string defaultModel,
        Func<Task<IReadOnlyList<string>>> discoverAsync)
    {
        var selectedModel = GetEditableComboValue(combo, defaultModel);
        refreshButton.IsEnabled = false;
        try
        {
            var models = await discoverAsync();
            ReplaceEditableComboOptions(combo, models, selectedModel);
        }
        catch (Exception ex)
        {
            await ShowAgentCliErrorAsync(ex);
        }
        finally
        {
            if (!_isUnloaded)
            {
                refreshButton.IsEnabled = true;
            }
        }
    }

    private async Task ShowAgentCliErrorAsync(Exception exception)
    {
        if (_isUnloaded) return;

        var loc = LocalizationService.Instance;
        var errorText = GetAgentCliErrorText(
            exception,
            Controls.ServiceResultStatusTextProvider.GetErrorText);
        await ShowTestFailureAsync(string.Format(loc.GetString("TestFailedMessage"), errorText));
    }

    internal static string GetAgentCliErrorText(
        Exception exception,
        Func<TranslationException, string> getTranslationErrorText)
    {
        return exception is TranslationException translationError
            ? getTranslationErrorText(translationError)
            : exception.Message;
    }




    #region Layout Detection
    private void UpdateOnnxModelStatus()
    {
#if DEBUG
        MemoryDiagnostics.LogSnapshot("SettingsPage.UpdateOnnxModelStatus begin");
#endif
        using var downloadService = new LayoutModelDownloadService();
        var loc = LocalizationService.Instance;

        if (downloadService.IsReady)
        {
            _settings.OnnxModelDownloaded = true;
        }
        else
        {
            _settings.OnnxModelDownloaded = false;
        }

#if DEBUG
        MemoryDiagnostics.LogSnapshot("SettingsPage.UpdateOnnxModelStatus complete");
        LogObjectState("UpdateOnnxModelStatus complete");
#endif
    }

    private async void OnDownloadOnnxModelClick(object sender, RoutedEventArgs e)
    {

        try
        {
            using var downloadService = new LayoutModelDownloadService();
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (p.Percentage >= 0)
                    {
                    }
                    else
                    {
                    }

                    var stageName = p.Stage == "runtime" ? "ONNX Runtime" : "Model";
                    var mb = p.BytesDownloaded / (1024.0 * 1024.0);
                    var totalMb = p.TotalBytes > 0 ? p.TotalBytes / (1024.0 * 1024.0) : 0;
                });
            });

            await downloadService.EnsureAvailableAsync(progress);

            _settings.OnnxModelDownloaded = true;
            _settings.Save();
            UpdateOnnxModelStatus();
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            var dialog = new ContentDialog
            {
                Title = loc.GetString("LayoutDetection_DownloadFailed"),
                Content = ex.Message,
                CloseButtonText = loc.GetString("OK"),
                XamlRoot = this.XamlRoot
            };
            await ShowDialogAsync(dialog);
        }
        finally
        {
        }
    }

    private void OnDeleteOnnxModelClick(object sender, RoutedEventArgs e)
    {
        using var downloadService = new LayoutModelDownloadService();
        downloadService.DeleteAll();
        _settings.OnnxModelDownloaded = false;
        _settings.Save();
        UpdateOnnxModelStatus();
    }

    #endregion

    #region Formula Detection

    private void OnFormulaPatternChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        OnSettingChanged(sender, new RoutedEventArgs());
    }

    #endregion

    #region Translation Cache

    private void OnTranslationCacheToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        OnSettingChanged(sender, new RoutedEventArgs());
    }

    private async void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        try
        {
            using var cacheService = new TranslationCacheService();
            await cacheService.ClearAsync();
            TranslationManagerService.Instance.ClearTranslationCache();
            CacheStatusText.Text = "Cache cleared.";
        }
        catch (Exception ex)
        {
            CacheStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }

    private static async Task UpdateCacheStatusAsync(
        WeakReference<SettingsPage> pageReference,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || !TryGetLivePage(pageReference, out var page))
        {
            return;
        }

#if DEBUG
        page.UpdateDeferredIoState("cache-running");
        MemoryDiagnostics.LogSnapshot("SettingsPage.UpdateCacheStatusAsync begin");
#endif
        try
        {
            var count = await GetCacheEntryCountAsync();
            if (cancellationToken.IsCancellationRequested)
            {
#if DEBUG
                if (pageReference.TryGetTarget(out var canceledPage))
                {
                    canceledPage.UpdateDeferredIoState("cache-canceled");
                }
#endif
                return;
            }

            dispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested || !TryGetLivePage(pageReference, out var activePage))
                {
#if DEBUG
                    if (pageReference.TryGetTarget(out var canceledPage))
                    {
                        canceledPage.UpdateDeferredIoState("cache-canceled");
                    }
#endif
                    return;
                }

                if (activePage.CacheStatusText is not null)
                {
                    activePage.CacheStatusText.Text = $"{count} cached entries";
                }

#if DEBUG
                activePage.UpdateDeferredIoState("cache-complete");
                MemoryDiagnostics.LogSnapshot("SettingsPage.UpdateCacheStatusAsync complete");
                activePage.LogObjectState("UpdateCacheStatusAsync complete");
#endif
            });
        }
        catch (OperationCanceledException)
        {
#if DEBUG
            if (pageReference.TryGetTarget(out var canceledPage))
            {
                canceledPage.UpdateDeferredIoState("cache-canceled");
            }
#endif
            // Ignore when teardown has canceled background work.
        }
        catch
        {
#if DEBUG
            if (pageReference.TryGetTarget(out var faultedPage))
            {
                faultedPage.UpdateDeferredIoState("cache-faulted");
            }
#endif
            // Ignore if cache DB doesn't exist yet
        }
    }

    #endregion

    #region Custom Prompt

    private void OnCustomPromptChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        OnSettingChanged(sender, new RoutedEventArgs());
    }

    #endregion

    #region CJK Font

    private async void OnCjkFontDownloadClick(object sender, RoutedEventArgs e)
    {

        try
        {
            using var fontService = new FontDownloadService();
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                });
            });

            // Download fonts for all CJK languages
            var languages = new[]
            {
                Easydict.TranslationService.Models.Language.SimplifiedChinese,
                Easydict.TranslationService.Models.Language.TraditionalChinese,
                Easydict.TranslationService.Models.Language.Japanese,
                Easydict.TranslationService.Models.Language.Korean,
            };

            foreach (var lang in languages)
            {
                await fontService.EnsureFontAsync(lang, progress);
            }

        }
        catch (Exception ex)
        {
        }
        finally
        {
        }
    }

    private void OnCjkFontDeleteClick(object sender, RoutedEventArgs e)
    {
        using var fontService = new FontDownloadService();
        fontService.DeleteAllFonts();
    }

    #endregion

    private async Task ShowTestFailureAsync(string message)
    {
        var loc = LocalizationService.Instance;
        var copyStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        content.Children.Add(copyStatus);

        var dialog = new ContentDialog
        {
            Title = loc.GetString("TestFailedTitle"),
            Content = content,
            PrimaryButtonText = loc.GetString("Copy"),
            CloseButtonText = loc.GetString("OK"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try
            {
                ClipboardService.SetText(message);
                dialog.PrimaryButtonText = loc.GetString("FluentCopied");
                copyStatus.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SettingsPage] Failed to copy test error: {ex.Message}");
                dialog.PrimaryButtonText = loc.GetString("Copy");
                copyStatus.Text = loc.GetString("FluentCopyFailed");
                copyStatus.Visibility = Visibility.Visible;
            }
        };
        await ShowDialogAsync(dialog);
    }

    /// <summary>
    /// Shows a ContentDialog, hiding any currently-open dialog first.
    /// WinUI 3 allows only one ContentDialog open at a time per XamlRoot.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_isUnloaded || _lifetimeCts.IsCancellationRequested)
        {
            return ContentDialogResult.None;
        }

        try { _currentDialog?.Hide(); } catch (COMException) { }
        PrepareDialogForDisplay(dialog);
        _currentDialog = dialog;

        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            if (_currentDialog == dialog)
            {
                _currentDialog = null;
            }
        }
    }

    private void PrepareDialogForDisplay(ContentDialog dialog)
    {
        dialog.XamlRoot ??= XamlRoot;
        dialog.RequestedTheme = ResolveDialogRequestedTheme();
    }

    private ElementTheme ResolveDialogRequestedTheme()
    {
        if (ThemeResourceService.IsHighContrastActive() && !MinimalThemeService.IsActive)
        {
            return ElementTheme.Default;
        }

        return ActualTheme switch
        {
            ElementTheme.Dark => ElementTheme.Dark,
            ElementTheme.Light => ElementTheme.Light,
            _ => MinimalThemeService.ToElementTheme(_settings.AppTheme)
        };
    }

    #endregion

    #region Available Languages Checkbox

    /// <summary>
    /// Build the language checkbox items from AllLanguages, sorted by group
    /// with the user's FirstLanguage group shown first.
    /// </summary>
    private void PopulateLanguageCheckboxGrid()
    {
        UnregisterLanguageCheckboxHandlers();

#if DEBUG
        MemoryDiagnostics.LogSnapshot("SettingsPage.PopulateLanguageCheckboxGrid begin");
#endif

        var loc = LocalizationService.Instance;
        var selectedSet = new HashSet<string>(_settings.SelectedLanguages, StringComparer.OrdinalIgnoreCase);

        // Determine which group the user's FirstLanguage belongs to
        var firstLangGroup = LanguageComboHelper.AllLanguages
            .FirstOrDefault(e => string.Equals(e.Tag, _settings.FirstLanguage, StringComparison.OrdinalIgnoreCase))
            .GroupOrder;

        _languageItems = LanguageComboHelper.AllLanguages
            .OrderBy(e => e.GroupOrder == firstLangGroup ? 0 : 1) // User's group first
            .ThenBy(e => e.GroupOrder) // Then by group order
            .Select(entry =>
            {
                var emoji = entry.Language.GetFlagEmoji();
                var name = loc.GetString(entry.LocalizationKey);
                var isEnglish = entry.Tag == "en";

                var item = new LanguageCheckboxItem
                {
                    Language = entry.Language,
                    Tag = entry.Tag,
                    DisplayText = $"{emoji} {name}",
                    IsEnabled = !isEnglish, // English is always selected and disabled
                    IsSelected = isEnglish || selectedSet.Contains(entry.Tag)
                };

                item.PropertyChanged += OnLanguageCheckboxChanged;
                return item;
            })
            .ToList();

        // Set up the ItemTemplate programmatically since LanguageCheckboxItem is a private inner class
        LanguageCheckboxGrid.ItemTemplate = CreateLanguageCheckboxTemplate();
        LanguageCheckboxGrid.ItemsSource = _languageItems;

#if DEBUG
        MemoryDiagnostics.LogSnapshot("SettingsPage.PopulateLanguageCheckboxGrid end");
#endif
    }

    private void UnregisterLanguageCheckboxHandlers()
    {
        foreach (var item in _languageItems)
        {
            item.PropertyChanged -= OnLanguageCheckboxChanged;
        }
    }

    /// <summary>
    /// Creates a DataTemplate for language checkbox items using XamlReader.
    /// Uses Binding (not x:Bind) since the model type is private.
    /// Cached to avoid repeated XamlReader.Load() calls.
    /// </summary>
    private static Microsoft.UI.Xaml.DataTemplate? _cachedLanguageCheckboxTemplate;
    private static Microsoft.UI.Xaml.DataTemplate CreateLanguageCheckboxTemplate()
    {
        if (_cachedLanguageCheckboxTemplate != null)
            return _cachedLanguageCheckboxTemplate;

        var xaml = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <CheckBox Content="{Binding DisplayText}"
                          IsChecked="{Binding IsSelected, Mode=TwoWay}"
                          IsEnabled="{Binding IsEnabled}"
                          MinWidth="160" Padding="4,0" />
            </DataTemplate>
            """;
        _cachedLanguageCheckboxTemplate = (Microsoft.UI.Xaml.DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        return _cachedLanguageCheckboxTemplate;
    }

    /// <summary>
    /// Handle checkbox changes in the Available Languages grid.
    /// Validates minimum selection and updates dependent combos.
    /// </summary>
    private void OnLanguageCheckboxChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading || e.PropertyName != nameof(LanguageCheckboxItem.IsSelected)) return;
        if (sender is not LanguageCheckboxItem changedItem) return;

        // Count currently selected languages
        var selectedCount = _languageItems.Count(item => item.IsSelected);

        // Enforce minimum of 2 selected languages
        if (selectedCount < 2 && !changedItem.IsSelected)
        {
            // Revert the uncheck
            _isLoading = true;
            changedItem.IsSelected = true;
            _isLoading = false;
            return;
        }

        // Build new selected languages list
        var newSelectedLanguages = _languageItems
            .Where(item => item.IsSelected)
            .Select(item => item.Tag)
            .ToList();

        // Check if First/Second language was unchecked — reset if so
        var firstLang = GetSelectedTag(FirstLanguageCombo) ?? _settings.FirstLanguage;
        var secondLang = GetSelectedTag(SecondLanguageCombo) ?? _settings.SecondLanguage;

        if (!newSelectedLanguages.Contains(firstLang, StringComparer.OrdinalIgnoreCase))
        {
            // Reset First Language to the first available selected language (prefer "zh")
            firstLang = newSelectedLanguages.Contains("zh") ? "zh" : newSelectedLanguages[0];
        }

        if (!newSelectedLanguages.Contains(secondLang, StringComparer.OrdinalIgnoreCase))
        {
            // Reset Second Language to a still-selected language that differs from First
            secondLang = newSelectedLanguages.FirstOrDefault(t =>
                !string.Equals(t, firstLang, StringComparison.OrdinalIgnoreCase)) ?? newSelectedLanguages[0];
        }

        // Update settings temporarily for combo helpers to pick up
        _settings.SelectedLanguages = newSelectedLanguages;

        // Always rebuild First/Second Language combos since available languages changed
        _isLoading = true;
        try
        {
            var loc = LocalizationService.Instance;
            PopulateSettingsLanguageCombo(FirstLanguageCombo, loc);
            PopulateSettingsLanguageCombo(SecondLanguageCombo, loc);
            SelectComboByTag(FirstLanguageCombo, firstLang);
            SelectComboByTag(SecondLanguageCombo, secondLang);
        }
        finally
        {
            _isLoading = false;
        }

        OnSettingChanged(null!, null!);
    }

    /// <summary>
    /// Populate a settings First/Second language combo with active languages (flag emoji + name).
    /// Unlike the window combos, these don't have Auto Detect.
    /// </summary>
    private static void PopulateSettingsLanguageCombo(ComboBox combo, LocalizationService loc)
    {
        combo.Items.Clear();
        foreach (var entry in LanguageComboHelper.SelectableLanguages)
        {
            var emoji = entry.Language.GetFlagEmoji();
            var name = loc.GetString(entry.LocalizationKey);
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{emoji} {name}",
                Tag = entry.Tag
            });
        }
    }

    /// <summary>
    /// Model class for language checkbox items in the Available Languages grid.
    /// </summary>
    private sealed class LanguageCheckboxItem : INotifyPropertyChanged
    {
        public TranslationLanguage Language { get; init; }
        public string Tag { get; init; } = string.Empty;
        public string DisplayText { get; init; } = string.Empty;
        public bool IsEnabled { get; init; } = true;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region TTS Voice Selection
    private async Task LoadTtsVoicesAsync()
    {
        IReadOnlyList<TextToSpeechService.TtsVoiceEntry> voices;
        var enumerationSucceeded = true;
        try
        {
            voices = await Task.Run(() => TextToSpeechService.Instance.GetAllVoices());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to enumerate TTS voices: {ex}");
            voices = Array.Empty<TextToSpeechService.TtsVoiceEntry>();
            enumerationSucceeded = false;
        }

        if (_isUnloaded || _isTornDown)
        {
            return;
        }

        PopulateTtsVoiceCombo(voices, enumerationSucceeded);
    }


    private void PopulateTtsVoiceCombo(
        IReadOnlyList<TextToSpeechService.TtsVoiceEntry> voices,
        bool enumerationSucceeded)
    {
        var wasSuppressed = _suppressTtsVoiceSelectionChanged;
        _suppressTtsVoiceSelectionChanged = true;
        try
        {
        TtsVoiceCombo.Items.Clear();

        var loc = LocalizationService.Instance;
        var autoItem = new ComboBoxItem
        {
            Content = loc.GetString("TtsVoiceAuto"),
            Tag = string.Empty
        };
        TtsVoiceCombo.Items.Add(autoItem);

        var sortedVoices = voices.OrderByDescending(v => v.IsNeural)
                                 .ThenBy(v => v.DisplayName)
                                 .ToList();

        foreach (var voice in sortedVoices)
        {
            var suffix = voice.IsSapi5 ? " [SAPI]" : "";
            var item = new ComboBoxItem
            {
                Content = $"{voice.DisplayName} - {voice.Language}{suffix}",
                Tag = voice.Id
            };
            TtsVoiceCombo.Items.Add(item);
        }

        var selectedVoiceId = _pendingTtsVoiceId;
        if (string.IsNullOrEmpty(selectedVoiceId))
        {
            TtsVoiceCombo.SelectedIndex = 0;
        }
        else
        {
            var selectedItem = TtsVoiceCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == selectedVoiceId);
            
            if (selectedItem != null)
            {
                TtsVoiceCombo.SelectedItem = selectedItem;
            }
            else
            {
                TtsVoiceCombo.SelectedIndex = 0;
                if (enumerationSucceeded)
                {
                    _pendingTtsVoiceId = string.Empty;
                    OnSettingChanged(TtsVoiceCombo, EventArgs.Empty);
                }
            }
        }
        }
        finally
        {
            _suppressTtsVoiceSelectionChanged = wasSuppressed;
        }
    }

    private void OnTtsVoiceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTtsVoiceSelectionChanged)
        {
            return;
        }

        if (TtsVoiceCombo.SelectedItem is ComboBoxItem item && item.Tag is string voiceId)
        {
            _pendingTtsVoiceId = voiceId;
            OnSettingChanged(sender, e);
        }
    }

    private async void OnTtsVoicePreviewClicked(object sender, RoutedEventArgs e)
    {
        if (_isUnloaded || _isTornDown)
        {
            return;
        }

        var tts = TextToSpeechService.Instance;
        if (TtsVoicePreviewIcon.Glyph == "\uE71A")
        {
            tts.Stop();
            return;
        }

        var lifetimeToken = _lifetimeCts.Token;
        TtsVoicePreviewIcon.Glyph = "\uE71A";
        try
        {
            await tts.SpeakPreviewAsync(
                TtsVoicePreviewText,
                TranslationLanguage.English,
                _pendingTtsVoiceId,
                TtsSpeedSlider.Value,
                lifetimeToken);
        }
        finally
        {
            if (!_isUnloaded && !_isTornDown)
            {
                TtsVoicePreviewIcon.Glyph = "\uE768";
            }
        }
    }

    private async void OnTtsVoiceRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (!TtsVoiceRefreshButton.IsEnabled) return;

        TtsVoiceRefreshButton.IsEnabled = false;
        TtsVoiceCombo.IsEnabled = false;
        TtsVoiceRefreshProgress.Visibility = Visibility.Visible;

        try
        {
            var voices = await Task.Run(() => TextToSpeechService.Instance.RefreshVoices());
            if (_isUnloaded || _isTornDown)
            {
                return;
            }

            PopulateTtsVoiceCombo(voices, enumerationSucceeded: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] Failed to refresh TTS voices: {ex}");
        }
        finally
        {
            if (!_isUnloaded && !_isTornDown)
            {
                TtsVoiceRefreshButton.IsEnabled = true;
                TtsVoiceCombo.IsEnabled = true;
                TtsVoiceRefreshProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    #endregion

    #endregion
}

/// <summary>
/// Converts a boolean value to Visibility.
/// True = Visible, False = Collapsed.
/// </summary>
public class BoolToVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// Converts a boolean value to opacity.
/// True = 1.0 (fully opaque), False = 0.4 (grayed out).
/// </summary>
public class BoolToOpacityConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? 1.0 : 0.4;
        }
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is double opacity)
        {
            return opacity > 0.5;
        }
        return true;
    }
}

/// <summary>
/// Converts a boolean (IsAvailable) to margin for compact layout.
/// True = normal spacing, False = reduced spacing for unavailable items.
/// </summary>
public class BoolToCompactMarginConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? new Thickness(0, 4, 0, 0) : new Thickness(0, 1, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
/// <summary>
/// Converts a boolean (IsAvailable) to font size for compact layout.
/// True = normal size (14), False = smaller size (12) for unavailable items.
/// </summary>
public class BoolToCompactFontSizeConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 14.0 : 12.0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a boolean (IsUnconfigured) to FontStyle.
/// True = Italic (needs configuration), False = Normal.
/// </summary>
public class BoolToItalicFontStyleConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
