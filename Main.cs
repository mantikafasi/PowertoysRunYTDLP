using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.Toolkit.Uwp.Notifications;
using Wox.Plugin;
using Wox.Plugin.Logger;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using NotificationData = Windows.UI.Notifications.NotificationData;
using ToastNotification = Windows.UI.Notifications.ToastNotification;

namespace Community.PowerToys.Run.Plugin.YTDLP;

internal class ContextData {
    public string format;
    public string url;
}

/// <summary>
///     Main class of this plugin that implement all used interfaces.
/// </summary>
public class Main : IPlugin, IContextMenu, IDisposable, IDelayedExecutionPlugin, ISettingProvider {
    private CancellationTokenSource _cts = new();
    private readonly YoutubeDL _ytdl = new() {
        OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\VideoDownloads"
    };
    private string? _currentFileName = "";
    private string _currentUrl = "";


    private Task<RunResult<VideoData>>? _fetchTask;

    private bool _isWaiting;
    public static string PluginID => "323D66BA5A384DA3A443F5302B10CC3D";
    private PluginInitContext Context { get; set; }
    private string IconPath { get; set; }
    private bool Disposed { get; set; }
    public string Name => "YTDLP";
    public string Description => "PowerToys Run plugin for downloading videos from websites with YTDLP";

    
    /// <summary>
    ///     Initialize the plugin with the given <see cref="PluginInitContext" />.
    /// </summary>
    /// <param name="context">The <see cref="PluginInitContext" /> for this plugin.</param>
    public void Init(PluginInitContext context) {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Context.API.ThemeChanged += OnThemeChanged;
        UpdateIconPath(Context.API.GetCurrentTheme());
        
        // check if yt-dlp is in path 
        var values = Environment.GetEnvironmentVariable("PATH");
    
        bool ytDlpFound = false;
        
        foreach (var path in values!.Split(Path.PathSeparator)) {
            if (File.Exists(Path.Combine(path, "yt-dlp.exe"))) {
                _ytdl.YoutubeDLPath = Path.Combine(path, "yt-dlp.exe");
                break;
            }
            
            if (File.Exists(Path.Combine(path, "ffmpeg.exe"))) {
                _ytdl.FFmpegPath = Path.Combine(path, "ffmpeg.exe");
                break;
            }
        }
        
        if (!ytDlpFound) {
            Log.Info("yt-dlp not found in PATH, Downloading", GetType());
            var task = Utils.DownloadBinaries();

            Task.Run(async () => {
                await task;

                if (task.IsCompleted) {
                    new ToastContentBuilder().AddText("Binaries downloaded").Show();
                }
                
                if (task.IsFaulted) {
                    new ToastContentBuilder().AddText("Error downloading binaries").AddText(task.Exception?.Message).Show();
                }
            });
        }
    }

    /// <summary>
    ///     Return a list context menu entries for a given <see cref="Result" /> (shown at the right side of the result).
    /// </summary>
    /// <param name="selectedResult">The <see cref="Result" /> for the list with context menu entries.</param>
    /// <returns>A list context menu entries.</returns>
    public List<ContextMenuResult> LoadContextMenus(Result selectedResult) {
        if (selectedResult.ContextData is string search)
            return [
                new ContextMenuResult {
                    PluginName = Name,
                    Title = "Copy to clipboard (Ctrl+C)",
                    FontFamily = "Segoe MDL2 Assets",
                    Glyph = "\xE8C8", // Copy
                    AcceleratorKey = Key.C,
                    AcceleratorModifiers = ModifierKeys.Control,
                    Action = _ => {
                        Clipboard.SetDataObject(search);
                        return true;
                    }
                }
            ];

        return [];
    }
    
    /// <summary>
    ///     Return a filtered list, based on the given query.
    /// </summary>
    /// <param name="query">The query to filter the list.</param>
    /// <returns>A filtered list, can be empty when nothing was found.</returns>
    public List<Result> Query(Query query) {
        if (string.IsNullOrEmpty(query.Search)) return [GetResult(query.Search, "Enter a URL", "Please enter a URL")];

        var url = query.Search.Contains(' ') ? query.Search.Split(" ")[0] : query.Search;

        if (_currentUrl != url && _fetchTask != null) {
            _cts.CancelAsync();
            _cts = new CancellationTokenSource();
            _fetchTask = null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uriResult) &&
            (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)) {
            if (query.Search.Contains(' ')) {
                Log.Info("Setting filename to " + query.Search.Split(" ", 2)[1], GetType());

                _currentFileName = query.Search.Split(" ", 2)[1];
            }

            if (_fetchTask == null) {
                _fetchTask = Task.Run(() => _ytdl.RunVideoDataFetch(url, _cts.Token,
                    overrideOptions: new OptionSet { FormatSort = "quality, hasvid, hasaudio, fps" }));
                _currentUrl = url;
            }

            return [
                new Result {
                    QueryTextDisplay = query.Search,
                    IcoPath = IconPath,
                    Title = "Best Video+Audio",
                    Action = _ => {
                        DownloadVideo(
                            string.IsNullOrEmpty(_currentFileName) ? DefaultFilenameFormat : _currentFileName,
                            _currentUrl, "bestvideo+bestaudio/best");
                        return true;
                    }
                },
                GetResult(query.Search, "Loading Qualities...", "")
            ];
        }

        return [GetResult(query.Search, "Invalid URL", "Please enter a valid URL")];
    }

    public List<Result> Query(Query query, bool delayedExecution) {
        if (_fetchTask != null) {
            if (!_fetchTask.IsCanceled && !_fetchTask.IsCompleted && !_isWaiting) {
                _isWaiting = true;
                _fetchTask.Wait();
                _isWaiting = false;
            }
        }
        else {
            return [];
        }

        try {
            if (!_fetchTask.Result.Success)
                return [
                    GetResult(query.Search, "An error occured while fetching URL", "Please check the URL and try again")
                ];

            var results = new List<Result>();

            var formatsData = _fetchTask.Result.Data.Formats.ToList();
            formatsData.Add(new FormatData {
                Format = "Best Video+Audio",
                FormatId = "bestvideo+bestaudio/best",
                Url = _fetchTask.Result.Data.Url
            });

            formatsData.Reverse();

            var i = 0;
            foreach (var res in formatsData) {
                results.Add(new Result {
                    QueryTextDisplay = query.Search,
                    Score = formatsData.Count - i,
                    IcoPath = IconPath,
                    Title = "" + _fetchTask.Result.Data.Title,
                    SubTitle = "Quality:" + res.Format,
                    ToolTipData = new ToolTipData(_fetchTask.Result.Data.Title, res.Format),
                    Action = _ => {
                        if (!_fetchTask.IsCanceled) {
                            _cts.CancelAsync();
                            _cts = new CancellationTokenSource();
                        }
                        
                        DownloadVideo(
                            string.IsNullOrEmpty(_currentFileName) ? DefaultFilenameFormat : _currentFileName,
                            _currentUrl!, res.FormatId);
                        return true;
                    },
                    ContextData = new ContextData {
                        format = res.Format,
                        url = _fetchTask.Result.Data.Url
                    }
                });
                i++;
            }

            return results;
        }
        catch (Exception e) {
            Log.Exception("error", e, GetType());

            if (e is AggregateException) return [];
            return [
                GetResult(query.Search, "An error occured while fetching URL", e.ToString())
            ];
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    public Control CreateSettingPanel() {
        return new Control();
    }

    public void UpdateSettings(PowerLauncherPluginSettings settings)
    {
        if (settings.AdditionalOptions != null)
        {
            foreach (var option in settings.AdditionalOptions)
            {
                if (option.Key == nameof(_ytdl.YoutubeDLPath))
                {
                    _ytdl.YoutubeDLPath = option.TextValue;
                }
                else if (option.Key == nameof(_ytdl.OutputFolder))
                {
                    _ytdl.OutputFolder = option.TextValue;
                }
                else if (option.Key == nameof(DefaultFilenameFormat)) {
                    DefaultFilenameFormat = option.TextValue;
                }
            }
        }
    }
    
    public string DefaultFilenameFormat = "%(title)s.%(ext)s";

    public IEnumerable<PluginAdditionalOption> AdditionalOptions => [
        new() {
            Key = nameof(_ytdl.YoutubeDLPath),
            DisplayLabel = "Path to yt-dlp executable",
            DisplayDescription = "Leave empty to use the default yt-dlp path",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _ytdl.YoutubeDLPath
        },
        new() {
            Key = nameof(_ytdl.OutputFolder),
            DisplayLabel = "Output folder",
            DisplayDescription = "Folder to save downloaded videos",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _ytdl.OutputFolder
        },
        new () {
            Key = nameof(DefaultFilenameFormat),
            DisplayLabel = "Default filename format",
            DisplayDescription = "yt-dlp filename format string",
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = DefaultFilenameFormat
        }
    ];

    public Result GetResult(string query, string title, string subtitle) {
        return new Result {
            QueryTextDisplay = query,
            IcoPath = IconPath,
            Title = title,
            SubTitle = subtitle
        };
    }

    private async Task DownloadVideo(string? filename, string url, string format) {
        _currentUrl = "";
        _currentFileName = "";

        var progressToast = new ToastContentBuilder()
            .AddText("Downloading Video")
            .AddVisualChild(new AdaptiveProgressBar {
                Title = filename == DefaultFilenameFormat ? "" : filename,
                Value = new BindableProgressBarValue("progressValue"),
                Status = "Downloading..."
            }).GetToastContent();

        var toast = new ToastNotification(progressToast.GetXml());

        toast.Data = new NotificationData();

        toast.Data.SequenceNumber = 1;
        toast.Tag = "yt-dlp-download";
        toast.Data.Values["progressValue"] = "0";
        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);

        var progress = new Progress<DownloadProgress>(p => {
            if (p.State == DownloadState.Success) {
                ToastNotificationManagerCompat.CreateToastNotifier().Hide(toast);
                return;
            }

            toast.Data.Values["progressValue"] = p.Progress.ToString(CultureInfo.InvariantCulture);
            toast.Data.SequenceNumber += 1;

            ToastNotificationManagerCompat.CreateToastNotifier().Update(toast.Data, "yt-dlp-download");
        });

        var video = await _ytdl.RunVideoDownload(url, format, progress: progress, overrideOptions: new OptionSet {
            Output = Path.Combine(_ytdl.OutputFolder, filename + ".%(ext)s")
        }, output: new Progress<string>(s => { Log.Info(s, GetType()); }));

        if (video.Success) {
            Log.Info("Download complete: " + video.Data, GetType());

            ToastNotificationManagerCompat.OnActivated += toastArgs => {
                Log.Info(toastArgs.Argument, GetType());
                var args = ToastArguments.Parse(toastArgs.Argument);
                Process.Start("explorer.exe", "/select, \"" + video.Data + "\"");
            };

            new ToastContentBuilder()
                .AddToastActivationInfo("app", ToastActivationType.Foreground)
                .AddText("Download complete")
                .AddText("Click to open file location")
                .AddArgument("path", video.Data)
                .Show();
        }
        else {
            Log.Info("error downloading video: " + string.Join("\n", video.ErrorOutput), GetType());

            new ToastContentBuilder()
                .AddToastActivationInfo("app", ToastActivationType.Foreground)
                .AddText("Download failed")
                .AddText(string.Join("\n", video.ErrorOutput))
                .AddArgument("error", video.ErrorOutput.ToString())
                .Show();
        }
    }

    /// <summary>
    ///     Wrapper method for <see cref="Dispose()" /> that dispose additional objects and events form the plugin itself.
    /// </summary>
    /// <param name="disposing">Indicate that the plugin is disposed.</param>
    protected virtual void Dispose(bool disposing) {
        if (Disposed || !disposing) return;
        if (Context?.API != null) Context.API.ThemeChanged -= OnThemeChanged;
        Disposed = true;
    }

    private void UpdateIconPath(Theme theme) {
        IconPath = theme == Theme.Light || theme == Theme.HighContrastWhite
            ? "Images/ytdlp.light.png"
            : "Images/ytdlp.dark.png";
    }

    private void OnThemeChanged(Theme currentTheme, Theme newTheme) {
        UpdateIconPath(newTheme);
    }
}