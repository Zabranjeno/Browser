namespace Cheprkach;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Forms; // For NotifyIcon and ToolTipIcon only

public class Browser : Window
{
    private System.Windows.Controls.TabControl? tabControl;
    private System.Windows.Controls.TextBox? urlBar;
    private System.Windows.Controls.Label? downloadLabel;
    private System.Windows.Controls.ProgressBar? downloadProgress;
    private DispatcherTimer? downloadTimer;
    private List<string> bookmarks = new();
    private List<(string Url, string Title, DateTime Time)> history = new();
    private List<string> pinnedTabs = new();
    private Dictionary<string, List<string>> tabGroups = new();
    private string homePage = "https://www.google.com";
    private readonly string bookmarksFile = "bookmarks.json";
    private readonly string sessionFile = "session.json";
    private readonly string syncFile = "cloud_sync.json";
    private DownloadManager? downloadManager;
    private List<(string Name, string Script, string[] Permissions)> extensions = new();
    private static readonly List<Browser> windows = new();
    private bool isDarkTheme = false;
    private readonly List<string> adBlockList = new() { "ads.", "doubleclick.net", "adserver." };

public Browser()
{
    windows.Add(this);
    WindowStyle = WindowStyle.SingleBorderWindow;
    Title = "Cheprkach";
    Width = 1200;
    Height = 800;
    Icon = new System.Windows.Media.Imaging.BitmapImage(
        new Uri("pack://application:,,,/Resources/Autorun.ico"));
    Closing += (s, e) => { SaveSession(); windows.Remove(this); SyncData(); };

        try
        {
            LoadBookmarks();
            downloadManager = new DownloadManager();
            InitializeUI();
            Loaded += (s, e) => Dispatcher.Invoke(() =>
            {
                RestoreSession();
                urlBar?.Focus();
                ApplyTheme();
            });
        }
        catch (Exception ex)
        {
            LogError("Initialization failed", ex);
            System.Windows.MessageBox.Show("Failed to initialize browser. Please restart.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InitializeUI()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var mainLayout = new Grid();
                mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
                mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                Content = mainLayout;

                CreateNavigationBar(mainLayout);
                CreateTabControl(mainLayout);
                CreateDownloadStatus(mainLayout);
            }
            catch (Exception ex)
            {
                LogError("Failed to initialize UI, possibly due to missing resources", ex);
                throw;
            }
        });
    }

    private void CreateNavigationBar(Grid layout)
    {
        Dispatcher.Invoke(() =>
        {
            var navBar = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Height = 40,
                Margin = new Thickness(5),
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240))
            };
            AutomationProperties.SetName(navBar, "Navigation Bar");

            var buttons = new[]
            {
                ("\u25C0", "Back", (Action<object?>)(_ => GetCurrentWebView()?.GoBack())),
                ("\u25B6", "Forward", (Action<object?>)(_ => GetCurrentWebView()?.GoForward())),
                ("\u27F3", "Reload", (Action<object?>)(_ => GetCurrentWebView()?.Reload())),
                ("\u2302", "Home", (Action<object?>)(_ => GetCurrentWebView()!.Source = new Uri(homePage))),
                ("\u2605", "Bookmark", (Action<object?>)(_ => AddBookmark())),
                ("\u2630", "Bookmarks Menu", (Action<object?>)(_ => ShowBookmarksMenu(_, new RoutedEventArgs()))),
                ("\u231A", "History", (Action<object?>)(_ => ShowHistoryMenu(_, new RoutedEventArgs()))),
                ("\u2699", "Settings", (Action<object?>)(_ => ShowSettings())),
                ("JS", "Execute JavaScript", (Action<object?>)(_ => ExecuteJavaScript())),
                ("\u2193", "Downloads", (Action<object?>)(_ => downloadManager!.Show())),
                ("\u2692", "Extensions", (Action<object?>)(_ => ShowExtensionsMenu(_, new RoutedEventArgs()))),
                ("\u25A3", "Tab Groups", (Action<object?>)(_ => ShowTabGroupsMenu(_, new RoutedEventArgs()))),
                ("\u229E", "New Window", (Action<object?>)(_ => new Browser().Show())),
                ("\u263C", "Toggle Theme", (Action<object?>)(_ => ToggleTheme()))
            };

            foreach (var (content, name, action) in buttons)
            {
                var button = new System.Windows.Controls.Button
                {
                    Content = content,
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(5),
                    Style = (Style)FindResource("NavButtonStyle")
                };
                AutomationProperties.SetName(button, name);
                button.Click += (s, e) => action(null);
                navBar.Children.Add(button);
            }

            urlBar = new System.Windows.Controls.TextBox
            {
                Margin = new Thickness(5),
                Height = 40,
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("UrlBarStyle")
            };
            AutomationProperties.SetName(urlBar, "URL Bar");
            urlBar.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    LoadUrl();
                }
            };

            navBar.Children.Add(urlBar);
            Grid.SetRow(navBar, 0);
            layout.Children.Add(navBar);

            InputBindings.Add(new KeyBinding(new RelayCommand(_ => GetCurrentWebView()?.GoBack()), Key.Left, ModifierKeys.Alt));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => GetCurrentWebView()?.GoForward()), Key.Right, ModifierKeys.Alt));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => GetCurrentWebView()?.Reload()), Key.R, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => AddNewTab(homePage, false)), Key.T, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => new Browser().Show()), Key.N, ModifierKeys.Control));
        });
    }

    private void CreateTabControl(Grid layout)
    {
        Dispatcher.Invoke(() =>
        {
            tabControl = new System.Windows.Controls.TabControl
            {
                Margin = new Thickness(5),
                Style = (Style)FindResource("TabControlStyle")
            };
            AutomationProperties.SetName(tabControl, "Tabs");
            tabControl.SelectionChanged += (s, e) => UpdateUrlBar();
            Grid.SetRow(tabControl, 1);
            layout.Children.Add(tabControl);

            var newTabButton = new System.Windows.Controls.Button
            {
                Content = "+",
                Width = 40,
                Height = 40,
                Margin = new Thickness(5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("NavButtonStyle")
            };
            AutomationProperties.SetName(newTabButton, "New Tab");
            newTabButton.Click += (s, e) => AddNewTab(homePage, false);

            var incognitoTabButton = new System.Windows.Controls.Button
            {
                Content = "\u26A5",
                Width = 40,
                Height = 40,
                Margin = new Thickness(5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("NavButtonStyle")
            };
            AutomationProperties.SetName(incognitoTabButton, "New Incognito Tab");
            incognitoTabButton.Click += (s, e) => AddNewTab(homePage, true);

            var buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            buttonPanel.Children.Add(newTabButton);
            buttonPanel.Children.Add(incognitoTabButton);
            Grid.SetRow(buttonPanel, 0);
            layout.Children.Add(buttonPanel);
        });
    }

    private void CreateDownloadStatus(Grid layout)
    {
        Dispatcher.Invoke(() =>
        {
            downloadLabel = new System.Windows.Controls.Label
            {
                Visibility = Visibility.Hidden,
                Margin = new Thickness(5),
                Content = "No active downloads"
            };
            AutomationProperties.SetName(downloadLabel, "Download Status");

            downloadProgress = new System.Windows.Controls.ProgressBar
            {
                Visibility = Visibility.Hidden,
                Margin = new Thickness(5),
                Height = 20
            };
            AutomationProperties.SetName(downloadProgress, "Download Progress");

            var downloadPanel = new StackPanel();
            downloadPanel.Children.Add(downloadLabel);
            downloadPanel.Children.Add(downloadProgress);
            Grid.SetRow(downloadPanel, 2);
            layout.Children.Add(downloadPanel);
        });
    }

    private void AddNewTab(string url, bool isIncognito, bool isPinned = false, string? groupName = null)
    {
        if (!IsValidUrl(url))
        {
            LogError("Invalid URL for new tab", new ArgumentException(url));
            return;
        }

        Dispatcher.Invoke(() =>
        {
            var webView = new WebView2();
            webView.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.StartsWith("https", StringComparison.OrdinalIgnoreCase) && !isIncognito)
                {
                    Dispatcher.Invoke(() =>
                        System.Windows.MessageBox.Show("Warning: This site is not secure (HTTP). Proceed with caution.", "Security Warning", MessageBoxButton.OK, MessageBoxImage.Warning));
                }
                if (!isIncognito && adBlockList.Any(ad => e.Uri.Contains(ad)))
                {
                    e.Cancel = true;
                }
            };
            webView.NavigationCompleted += async (s, e) =>
            {
                Dispatcher.Invoke(() => UpdateUrlBar());
                if (!isIncognito && webView.Source != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        history.Add((webView.Source.ToString(), webView.CoreWebView2.DocumentTitle, DateTime.Now));
                        SyncData();
                    });
                    foreach (var ext in extensions)
                    {
                        if (ext.Permissions.Contains("script-execution"))
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync(SanitizeScript(ext.Script));
                        }
                    }
                }
            };
            webView.CoreWebView2InitializationCompleted += async (s, e) =>
            {
                webView.CoreWebView2.DownloadStarting += async (sender, args) =>
                {
                    args.Cancel = true;
                    var savePath = await Dispatcher.InvokeAsync(() => ShowSaveFileDialog(args.ResultFilePath));
                    if (!string.IsNullOrEmpty(savePath))
                    {
                        downloadManager!.AddDownload(args.DownloadOperation, savePath);
                    }
                };
                webView.CoreWebView2.ContextMenuRequested += (sender, args) =>
                {
                    var menuList = args.MenuItems;
                    var devToolsItem = webView.CoreWebView2.Environment.CreateContextMenuItem("Developer Tools", null, CoreWebView2ContextMenuItemKind.Command);
                    devToolsItem.CustomItemSelected += (s2, e2) => webView.CoreWebView2.OpenDevToolsWindow();
                    var pinItem = webView.CoreWebView2.Environment.CreateContextMenuItem(isPinned ? "Unpin Tab" : "Pin Tab", null, CoreWebView2ContextMenuItemKind.Command);
                    pinItem.CustomItemSelected += (s2, e2) => TogglePinTab(webView.Source.ToString());
                    menuList.Add(devToolsItem);
                    menuList.Add(pinItem);
                };
                if (isIncognito)
                {
                    webView.CoreWebView2.NewWindowRequested += (s2, e2) => e2.Handled = true;
                    await webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
                }
            };

            if (isIncognito)
            {
                var envOptions = new CoreWebView2EnvironmentOptions();
                var env = CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), $"Cheprkach.Incognito_{Guid.NewGuid()}"), envOptions).GetAwaiter().GetResult();
                webView.EnsureCoreWebView2Async(env).GetAwaiter().GetResult();
            }
            webView.Source = new Uri(url);

            var tabItem = new TabItem
            {
                Header = isIncognito ? "Incognito Tab" : (isPinned ? "Pinned Tab" : "New Tab"),
                Content = webView,
                ToolTip = url,
                Style = (Style)FindResource("TabItemStyle")
            };

            var closeButton = new System.Windows.Controls.Button
            {
                Content = "x",
                Width = 20,
                Height = 20,
                Margin = new Thickness(5, 0, 0, 0),
                Visibility = isPinned ? Visibility.Hidden : Visibility.Visible,
                Style = (Style)FindResource("CloseButtonStyle")
            };
            closeButton.Click += (s, e) =>
            {
                if (tabControl!.Items.Count > 1)
                {
                    tabControl.Items.Remove(tabItem);
                }
            };

            var tabHeader = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            tabHeader.Children.Add(new System.Windows.Controls.Label { Content = isIncognito ? "Incognito" : (isPinned ? "Pinned" : "New Tab") });
            tabHeader.Children.Add(closeButton);
            tabItem.Header = tabHeader;

            tabControl!.Items.Add(tabItem);
            tabControl.SelectedItem = tabItem;

            if (isPinned)
            {
                pinnedTabs.Add(url);
            }
            if (!string.IsNullOrEmpty(groupName))
            {
                if (!tabGroups.ContainsKey(groupName))
                {
                    tabGroups[groupName] = new List<string>();
                }
                tabGroups[groupName].Add(url);
            }

            if (tabControl.SelectedItem == tabItem)
            {
                webView.EnsureCoreWebView2Async().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Dispatcher.Invoke(() => LogError("WebView2 initialization failed", t.Exception!));
                    }
                });
            }
            else
            {
                tabItem.GotFocus += async (s, e) => await webView.EnsureCoreWebView2Async();
            }
        });
    }

    private WebView2? GetCurrentWebView()
    {
        return Dispatcher.Invoke(() =>
        {
            if (tabControl?.SelectedItem is TabItem tabItem)
            {
                return tabItem.Content as WebView2;
            }
            return null;
        });
    }

    private void LoadUrl()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var url = urlBar?.Text.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    return;
                }

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    if (url.Contains("."))
                    {
                        url = "https://" + url;
                    }
                    else
                    {
                        url = $"https://www.google.com/search?q={Uri.EscapeDataString(url)}";
                    }
                }

                if (!IsValidUrl(url))
                {
                    throw new UriFormatException("Invalid URL format.");
                }

                var webView = GetCurrentWebView();
                if (webView != null)
                {
                    webView.Source = new Uri(url);
                }
            }
            catch (Exception ex)
            {
                LogError("Error loading URL", ex);
                System.Windows.MessageBox.Show($"Error loading URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private void UpdateUrlBar()
    {
        Dispatcher.Invoke(() =>
        {
            var webView = GetCurrentWebView();
            if (webView?.Source != null && tabControl?.SelectedItem is TabItem tabItem)
            {
                urlBar!.Text = webView.Source.ToString();
                var headerPanel = tabItem.Header as StackPanel;
                var title = webView.CoreWebView2?.DocumentTitle ?? "New Tab";
                headerPanel!.Children[0] = new System.Windows.Controls.Label { Content = title.Length > 20 ? title.Substring(0, 20) + "..." : title };
            }
        });
    }

    private void AddBookmark()
    {
        Dispatcher.Invoke(() =>
        {
            var currentUrl = GetCurrentWebView()?.Source?.ToString();
            if (!string.IsNullOrEmpty(currentUrl) && !bookmarks.Contains(currentUrl))
            {
                bookmarks.Add(currentUrl);
                SaveBookmarks();
                SyncData();
                System.Windows.MessageBox.Show("Bookmark added!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
    }

    private void ShowBookmarksMenu(object? sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var contextMenu = new ContextMenu();
            foreach (var bookmark in bookmarks)
            {
                var menuItem = new MenuItem { Header = bookmark };
                menuItem.Click += (s, ev) => GetCurrentWebView()!.Source = new Uri(bookmark);
                contextMenu.Items.Add(menuItem);
            }

            if (bookmarks.Count == 0)
            {
                contextMenu.Items.Add(new MenuItem { Header = "No bookmarks", IsEnabled = false });
            }

            contextMenu.IsOpen = true;
        });
    }

    private void ShowHistoryMenu(object? sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var contextMenu = new ContextMenu();
            foreach (var entry in history.OrderByDescending(h => h.Time).Take(20))
            {
                var menuItem = new MenuItem { Header = $"{entry.Title} - {entry.Time:MM/dd/yyyy HH:mm}" };
                menuItem.Click += (s, ev) => GetCurrentWebView()!.Source = new Uri(entry.Url);
                contextMenu.Items.Add(menuItem);
            }

            if (history.Count == 0)
            {
                contextMenu.Items.Add(new MenuItem { Header = "No history", IsEnabled = false });
            }

            contextMenu.IsOpen = true;
        });
    }

    private void ShowTabGroupsMenu(object? sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var contextMenu = new ContextMenu();
            var addToGroupItem = new MenuItem { Header = "Add to New Group" };
            addToGroupItem.Click += (s, ev) =>
            {
                var groupName = Microsoft.VisualBasic.Interaction.InputBox("Enter group name:", "New Tab Group", "");
                if (!string.IsNullOrEmpty(groupName))
                {
                    var currentUrl = GetCurrentWebView()?.Source?.ToString();
                    if (!string.IsNullOrEmpty(currentUrl))
                    {
                        if (!tabGroups.ContainsKey(groupName))
                        {
                            tabGroups[groupName] = new List<string>();
                        }
                        tabGroups[groupName].Add(currentUrl);
                    }
                }
            };
            contextMenu.Items.Add(addToGroupItem);

            foreach (var group in tabGroups)
            {
                var groupItem = new MenuItem { Header = group.Key };
                foreach (var url in group.Value)
                {
                    var urlItem = new MenuItem { Header = url };
                    urlItem.Click += (s, ev) => GetCurrentWebView()!.Source = new Uri(url);
                    groupItem.Items.Add(urlItem);
                }
                contextMenu.Items.Add(groupItem);
            }

            contextMenu.IsOpen = true;
        });
    }

    private void TogglePinTab(string url)
    {
        Dispatcher.Invoke(() =>
        {
            if (pinnedTabs.Contains(url))
            {
                pinnedTabs.Remove(url);
            }
            else
            {
                pinnedTabs.Add(url);
            }
            foreach (TabItem tabItem in tabControl!.Items)
            {
                var webView = tabItem.Content as WebView2;
                if (webView?.Source.ToString() == url)
                {
                    var headerPanel = tabItem.Header as StackPanel;
                    headerPanel!.Children[0] = new System.Windows.Controls.Label { Content = pinnedTabs.Contains(url) ? "Pinned" : webView.CoreWebView2.DocumentTitle };
                    var closeButton = headerPanel.Children[1] as System.Windows.Controls.Button;
                    closeButton!.Visibility = pinnedTabs.Contains(url) ? Visibility.Hidden : Visibility.Visible;
                    break;
                }
            }
        });
    }

    private void ShowExtensionsMenu(object? sender, RoutedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var contextMenu = new ContextMenu();
            var addExtensionItem = new MenuItem { Header = "Add Extension" };
            addExtensionItem.Click += (s, ev) =>
            {
                var name = Microsoft.VisualBasic.Interaction.InputBox("Enter extension name:", "Add Extension", "");
                var script = Microsoft.VisualBasic.Interaction.InputBox("Enter JavaScript for extension:", "Add Extension", "");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(script))
                {
                    var permissions = Microsoft.VisualBasic.Interaction.InputBox("Enter permissions (comma-separated, e.g., script-execution):", "Add Extension", "script-execution").Split(',').Select(p => p.Trim()).ToArray();
                    extensions.Add((name, SanitizeScript(script), permissions));
                    System.Windows.MessageBox.Show("Extension added!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            contextMenu.Items.Add(addExtensionItem);

            foreach (var ext in extensions)
            {
                var extItem = new MenuItem { Header = ext.Name };
                extItem.Click += async (s, ev) =>
                {
                    if (ext.Permissions.Contains("script-execution"))
                    {
                        await GetCurrentWebView()?.CoreWebView2.ExecuteScriptAsync(ext.Script);
                    }
                };
                contextMenu.Items.Add(extItem);
            }

            contextMenu.IsOpen = true;
        });
    }

    private void ExecuteJavaScript()
    {
        Dispatcher.Invoke(() =>
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox("Enter JavaScript to execute:", "Execute JavaScript", "");
            if (!string.IsNullOrEmpty(input))
            {
                try
                {
                    GetCurrentWebView()?.CoreWebView2.ExecuteScriptAsync(SanitizeScript(input));
                }
                catch (Exception ex)
                {
                    LogError("Error executing JavaScript", ex);
                    System.Windows.MessageBox.Show($"Error executing JavaScript: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        });
    }

    private void ShowSettings()
    {
        Dispatcher.Invoke(() =>
        {
            var settingsWindow = new Window
            {
                Title = "Settings",
                Width = 400,
                Height = 500,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            try
            {
                settingsWindow.Style = (Style)FindResource("WindowStyle");
            }
            catch (Exception ex)
            {
                LogError("Failed to apply WindowStyle to Settings window", ex);
            }

            var layout = new StackPanel { Margin = new Thickness(10) };

            var homePageLabel = new System.Windows.Controls.Label { Content = "Homepage URL:" };
            var homePageTextBox = new System.Windows.Controls.TextBox { Text = homePage, Margin = new Thickness(0, 0, 0, 10) };
            try
            {
                homePageTextBox.Style = (Style)FindResource("TextBoxStyle");
            }
            catch (Exception ex)
            {
                LogError("Failed to apply TextBoxStyle to homePageTextBox", ex);
            }

            var clearCacheButton = new System.Windows.Controls.Button { Content = "Clear Cache", Margin = new Thickness(0, 0, 0, 10) };
            try
            {
                clearCacheButton.Style = (Style)FindResource("ButtonStyle");
            }
            catch (Exception ex)
            {
                LogError("Failed to apply ButtonStyle to clearCacheButton", ex);
            }
            clearCacheButton.Click += async (s, e) =>
            {
                await GetCurrentWebView()?.CoreWebView2.Profile.ClearBrowsingDataAsync();
                System.Windows.MessageBox.Show("Cache cleared!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            };

            var memoryLimitLabel = new System.Windows.Controls.Label { Content = "Max Tabs (0 for unlimited):" };
            var memoryLimitTextBox = new System.Windows.Controls.TextBox { Text = "0", Margin = new Thickness(0, 0, 0, 10) };
            try
            {
                memoryLimitTextBox.Style = (Style)FindResource("TextBoxStyle");
            }
            catch (Exception ex)
            {
                LogError("Failed to apply TextBoxStyle to memoryLimitTextBox", ex);
            }

            var syncButton = new System.Windows.Controls.Button { Content = "Sync Now", Margin = new Thickness(0, 0, 0, 10) };
            try
            {
                syncButton.Style = (Style)FindResource("ButtonStyle");
            }
            catch (Exception ex)
            {
                LogError("Failed to apply ButtonStyle to syncButton", ex);
            }
            syncButton.Click += (s, e) => SyncData();

            var saveButton = new System.Windows.Controls.Button { Content = "Save", Margin = new Thickness(0, 0, 0, 10) };
            try
            {
                saveButton.Style = (Style)FindResource("ButtonStyle");
            }
            catch (Exception ex)
            {
                LogError("Failed to apply ButtonStyle to saveButton", ex);
            }
            saveButton.Click += (s, e) =>
            {
                try
                {
                    homePage = new Uri(homePageTextBox.Text).ToString();
                    if (int.TryParse(memoryLimitTextBox.Text, out int limit) && limit > 0)
                    {
                        EnforceTabLimit(limit);
                    }
                    System.Windows.MessageBox.Show("Settings saved!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    settingsWindow.Close();
                }
                catch
                {
                    System.Windows.MessageBox.Show("Invalid homepage URL or settings.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };

            layout.Children.Add(homePageLabel);
            layout.Children.Add(homePageTextBox);
            layout.Children.Add(clearCacheButton);
            layout.Children.Add(memoryLimitLabel);
            layout.Children.Add(memoryLimitTextBox);
            layout.Children.Add(syncButton);
            layout.Children.Add(saveButton);
            settingsWindow.Content = layout;
            settingsWindow.ShowDialog();
        });
    }

    private void ApplyTheme()
    {
        Dispatcher.Invoke(() =>
        {
            Background = isDarkTheme ? new SolidColorBrush(Color.FromRgb(30, 30, 30)) : Brushes.White;
            var foreground = isDarkTheme ? Brushes.White : Brushes.Black;
#pragma warning disable CS8602 // urlBar is initialized in CreateNavigationBar
            urlBar.Background = Background;
            urlBar.Foreground = foreground;
#pragma warning restore CS8602
        });
    }

    private void ToggleTheme()
    {
        Dispatcher.Invoke(() =>
        {
            isDarkTheme = !isDarkTheme;
            ApplyTheme();
        });
    }

    private void SyncData()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var syncData = new
                {
                    Bookmarks = bookmarks,
                    History = history,
                    HomePage = homePage
                };
                var json = JsonSerializer.Serialize(syncData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(syncFile, json);

                if (File.Exists(syncFile))
                {
                    var syncJson = File.ReadAllText(syncFile);
                    var syncedData = JsonSerializer.Deserialize<JsonElement>(syncJson);
                    if (syncedData.ValueKind != JsonValueKind.Null && syncedData.ValueKind != JsonValueKind.Undefined)
                    {
                        if (syncedData.TryGetProperty("Bookmarks", out var bookmarksElement) && bookmarksElement.ValueKind != JsonValueKind.Null)
                        {
                            try
                            {
                                var deserializedBookmarks = JsonSerializer.Deserialize<List<string>>(bookmarksElement.GetRawText());
                                bookmarks = deserializedBookmarks ?? bookmarks;
                            }
                            catch
                            {
                                LogError("Failed to deserialize bookmarks", new JsonException());
                            }
                        }
                        if (syncedData.TryGetProperty("History", out var historyElement) && historyElement.ValueKind != JsonValueKind.Null)
                        {
                            try
                            {
                                var deserializedHistory = JsonSerializer.Deserialize<List<(string Url, string Title, DateTime Time)>>(historyElement.GetRawText());
                                history = deserializedHistory ?? history;
                            }
                            catch
                            {
                                LogError("Failed to deserialize history", new JsonException());
                            }
                        }
                        if (syncedData.TryGetProperty("HomePage", out var homePageElement) && homePageElement.ValueKind != JsonValueKind.Null)
                        {
                            homePage = homePageElement.GetString() ?? homePage;
                        }
#pragma warning disable CS8602 // windows is initialized and UpdateUrlBar is safe
                        foreach (var window in windows)
                        {
                            window.UpdateUrlBar();
                        }
#pragma warning restore CS8602
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Error syncing data", ex);
                System.Windows.MessageBox.Show($"Error syncing data: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private void EnforceTabLimit(int limit)
    {
        Dispatcher.Invoke(() =>
        {
            while (tabControl?.Items.Count > limit)
            {
                var tabToRemove = tabControl.Items.Cast<TabItem>().FirstOrDefault(t => !pinnedTabs.Contains((t.Content as WebView2)!.Source.ToString()));
                if (tabToRemove != null)
                {
                    tabControl.Items.Remove(tabToRemove);
                }
                else
                {
                    break;
                }
            }
        });
    }

    private void LoadBookmarks()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (File.Exists(bookmarksFile))
                {
                    var json = File.ReadAllText(bookmarksFile);
                    bookmarks = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
            }
            catch (Exception ex)
            {
                LogError("Error loading bookmarks", ex);
                bookmarks = new List<string>();
            }
        });
    }

    private void SaveBookmarks()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                var json = JsonSerializer.Serialize(bookmarks, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(bookmarksFile, json);
            }
            catch (Exception ex)
            {
                LogError("Error saving bookmarks", ex);
                System.Windows.MessageBox.Show($"Error saving bookmarks: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private void SaveSession()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (tabControl == null || tabControl.Items.Count == 0)
                {
                    LogError("No tabs to save in session", new InvalidOperationException("Tab control is not initialized or empty"));
                    return;
                }

                var session = new List<(string Url, bool IsIncognito, bool IsPinned, string? GroupName)>();
                foreach (TabItem tabItem in tabControl.Items)
                {
                    var webView = tabItem.Content as WebView2;
                    if (webView?.Source == null)
                    {
                        LogError("Skipping tab with uninitialized WebView", new InvalidOperationException("WebView or Source is null"));
                        continue;
                    }
                    var url = webView.Source.ToString();
                    var isPinned = pinnedTabs.Contains(url);
                    var groupName = tabGroups.FirstOrDefault(g => g.Value.Contains(url)).Key;
                    session.Add((url, false, isPinned, groupName));
                }
                var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sessionFile, json);
            }
            catch (Exception ex)
            {
                LogError("Error saving session", ex);
                System.Windows.MessageBox.Show($"Error saving session: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private void RestoreSession()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (File.Exists(sessionFile))
                {
                    var json = File.ReadAllText(sessionFile);
                    var session = JsonSerializer.Deserialize<List<(string Url, bool IsIncognito, bool IsPinned, string? GroupName)>>(json);
                    if (session != null)
                    {
                        foreach (var tab in session)
                        {
                            AddNewTab(tab.Url, tab.IsIncognito, tab.IsPinned, tab.GroupName);
                        }
                    }
                }
                else
                {
                    AddNewTab(homePage, false);
                }
            }
            catch (Exception ex)
            {
                LogError("Error restoring session", ex);
                AddNewTab(homePage, false);
            }
        });
    }

    private void HandleDownload(CoreWebView2DownloadOperation download, string savePath)
    {
        Dispatcher.Invoke(() =>
        {
#pragma warning disable CS8602 // downloadLabel and downloadProgress are initialized in CreateDownloadStatus
            if (downloadLabel == null || downloadProgress == null)
            {
                LogError("Download UI elements not initialized", new InvalidOperationException());
                return;
            }

            downloadLabel.Content = $"Downloading: {Path.GetFileName(savePath)}";
            downloadLabel.Visibility = Visibility.Visible;
            downloadProgress.Visibility = Visibility.Visible;
            downloadProgress.Value = 0;
#pragma warning restore CS8602

            MonitorDownload(download, savePath);

            using var notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Text = "Download Started",
                Visible = true
            };
            notifyIcon.ShowBalloonTip(3000, "Download", $"Downloading {Path.GetFileName(savePath)}", System.Windows.Forms.ToolTipIcon.Info);
            download.StateChanged += (s, e) =>
            {
                if (download.State == CoreWebView2DownloadState.Completed)
                {
                    notifyIcon.ShowBalloonTip(3000, "Download Complete", $"{Path.GetFileName(savePath)} downloaded.", System.Windows.Forms.ToolTipIcon.Info);
                }
            };
        });
    }

    private string? ShowSaveFileDialog(string? suggestedFileName)
    {
        return Dispatcher.Invoke(() =>
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = suggestedFileName ?? "download",
                Filter = "All files (*.*)|*.*"
            };
            return saveFileDialog.ShowDialog() == true ? saveFileDialog.FileName : null;
        });
    }

    private void MonitorDownload(CoreWebView2DownloadOperation download, string savePath)
    {
        Dispatcher.Invoke(() =>
        {
            downloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            downloadTimer.Tick += (s, e) =>
            {
                var bytesReceived = download.BytesReceived;
                var totalBytes = download.TotalBytesToReceive;
                if (totalBytes.HasValue)
                {
#pragma warning disable CS8602 // downloadProgress is checked in HandleDownload
                    downloadProgress.Value = (double)bytesReceived / totalBytes.Value * 100;
#pragma warning restore CS8602
                }

                if (download.State == CoreWebView2DownloadState.Completed)
                {
#pragma warning disable CS8602 // downloadLabel and downloadProgress are checked in HandleDownload
                    downloadLabel.Content = "Download completed!";
                    downloadProgress.Value = 100;
#pragma warning restore CS8602
                    downloadTimer.Stop();
                    DispatcherTimer cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    cleanupTimer.Tick += (s2, e2) =>
                    {
#pragma warning disable CS8602 // downloadLabel and downloadProgress are checked in HandleDownload
                        downloadLabel.Visibility = Visibility.Hidden;
                        downloadProgress.Visibility = Visibility.Hidden;
#pragma warning restore CS8602
                        cleanupTimer.Stop();
                    };
                    cleanupTimer.Start();
                }
                else if (download.State == CoreWebView2DownloadState.Interrupted)
                {
#pragma warning disable CS8602 // downloadLabel is checked in HandleDownload
                    downloadLabel.Content = "Download failed!";
#pragma warning restore CS8602
                    downloadTimer.Stop();
                }
            };
            downloadTimer.Start();
        });
    }

    private bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uriResult) &&
               (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
    }

    private string SanitizeScript(string script)
    {
        var dangerousPatterns = new[] { "eval\\(", "document.cookie", "localStorage", "sessionStorage" };
        foreach (var pattern in dangerousPatterns)
        {
            script = Regex.Replace(script, pattern, "/* blocked */");
        }
        return script;
    }

    private static void LogError(string message, Exception ex)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            File.AppendAllText("error.log", $"{DateTime.Now}: {message} - {ex.Message}\n");
        });
    }

    private class DownloadManager : Window
    {
        private readonly List<(CoreWebView2DownloadOperation Download, string SavePath)> downloads = new();
        private System.Windows.Controls.ListBox? downloadList;

        public DownloadManager()
        {
            Dispatcher.Invoke(() =>
            {
                Title = "Download Manager";
                Width = 600;
                Height = 400;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;

                var layout = new StackPanel { Margin = new Thickness(10) };
                downloadList = new System.Windows.Controls.ListBox();
                layout.Children.Add(downloadList);
                Content = layout;
            });
        }

        public void AddDownload(CoreWebView2DownloadOperation download, string savePath)
        {
            Dispatcher.Invoke(() =>
            {
                downloads.Add((download, savePath));
                UpdateDownloadList();
                MonitorDownload(download, savePath);
            });
        }

        private void UpdateDownloadList()
        {
            Dispatcher.Invoke(() =>
            {
                if (downloadList == null)
                {
                    LogError("Download list is not initialized", new InvalidOperationException());
                    return;
                }
                downloadList.Items.Clear();
                foreach (var (download, savePath) in downloads)
                {
                    var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                    var label = new System.Windows.Controls.Label { Content = Path.GetFileName(savePath) };
                    var pauseResumeButton = new System.Windows.Controls.Button { Content = download.State == CoreWebView2DownloadState.InProgress ? "Pause" : "Resume", Width = 60 };
                    try
                    {
                        pauseResumeButton.Style = (Style)FindResource("ButtonStyle");
                    }
                    catch (Exception ex)
                    {
                        LogError("Failed to apply ButtonStyle to pauseResumeButton", ex);
                    }
                    pauseResumeButton.Click += (s, e) =>
                    {
                        if (download.State == CoreWebView2DownloadState.InProgress)
                        {
                            download.Pause();
                            pauseResumeButton.Content = "Resume";
                        }
                        else
                        {
                            download.Resume();
                            pauseResumeButton.Content = "Pause";
                        }
                    };
                    var cancelButton = new System.Windows.Controls.Button { Content = "Cancel", Width = 60 };
                    try
                    {
                        cancelButton.Style = (Style)FindResource("ButtonStyle");
                    }
                    catch (Exception ex)
                    {
                        LogError("Failed to apply ButtonStyle to cancelButton", ex);
                    }
                    cancelButton.Click += (s, e) => download.Cancel();

                    panel.Children.Add(label);
                    panel.Children.Add(pauseResumeButton);
                    panel.Children.Add(cancelButton);
                    downloadList.Items.Add(panel);
                }
            });
        }

        private void MonitorDownload(CoreWebView2DownloadOperation download, string savePath)
        {
            Dispatcher.Invoke(() =>
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (s, e) =>
                {
                    UpdateDownloadList();
                    if (download.State == CoreWebView2DownloadState.Completed || download.State == CoreWebView2DownloadState.Interrupted)
                    {
                        timer.Stop();
                    }
                };
                timer.Start();
            });
        }

        public new void Show()
        {
            Dispatcher.Invoke(() =>
            {
                if (!IsVisible)
                {
                    base.Show();
                }
                else
                {
                    Activate();
                }
            });
        }
    }

    private class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    }

    [STAThread]
    public static void Main()
    {
        try
        {
            var app = new System.Windows.Application();
            app.Run(new Browser());
        }
        catch (Exception ex)
        {
            LogError("Application startup failed", ex);
            System.Windows.MessageBox.Show("Failed to start the browser. Please check the error log.", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}