using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ManagedCommon;
using Microsoft.Toolkit.Uwp.Notifications;
using Wox.Plugin;
using Wox.Plugin.Logger;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using NotificationData = Windows.UI.Notifications.NotificationData;
using ToastNotification = Windows.UI.Notifications.ToastNotification;

namespace Community.PowerToys.Run.Plugin.YTDLP;

internal class ContextData
{
    public string format;
    public string url;
}

/// <summary>
///     Main class of this plugin that implement all used interfaces.
/// </summary>
public class Main : IPlugin, IContextMenu, IDisposable, IDelayedExecutionPlugin
{
    private readonly YoutubeDL ytdl = new();

    /// <summary>
    ///     ID of the plugin.
    /// </summary>
    public static string PluginID => "323D66BA5A384DA3A443F5302B10CC3D";

    private PluginInitContext Context { get; set; }

    private string IconPath { get; set; }

    private bool Disposed { get; set; }

    /// <summary>
    ///     Return a list context menu entries for a given <see cref="Result" /> (shown at the right side of the result).
    /// </summary>
    /// <param name="selectedResult">The <see cref="Result" /> for the list with context menu entries.</param>
    /// <returns>A list context menu entries.</returns>
    public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is string search)
            return
            [
                new ContextMenuResult
                {
                    PluginName = Name,
                    Title = "Copy to clipboard (Ctrl+C)",
                    FontFamily = "Segoe MDL2 Assets",
                    Glyph = "\xE8C8", // Copy
                    AcceleratorKey = Key.C,
                    AcceleratorModifiers = ModifierKeys.Control,
                    Action = _ =>
                    {
                        Clipboard.SetDataObject(search);
                        return true;
                    }
                }
            ];

        return [];
    }

    public List<Result> Query(Query query, bool delayedExecution)
    {
        if (string.IsNullOrEmpty(query.Search)) return [];

        try
        {
            var search = query.Search;
            var task = Task.Run(() => ytdl.RunVideoDataFetch(search,
                overrideOptions: new OptionSet { FormatSort = "quality, hasvid, hasaudio, fps" }));
            task.Wait();


            if (!task.Result.Success)
                return
                [
                    GetResult("An error occured while fetching URL", "Please check the URL and try again")
                ];

            var results = new List<Result>();

            var formatsData = task.Result.Data.Formats.ToList();
            formatsData.Add(new FormatData
            {
                Format = "Best Video+Audio",
                FormatId = "bestvideo+bestaudio/best",
                Url = task.Result.Data.Url
            });

            formatsData.Reverse();

            var i = 0;
            foreach (var res in formatsData)
            {
                results.Add(new Result
                {
                    Score = formatsData.Count - i,
                    QueryTextDisplay = search,
                    IcoPath = IconPath,
                    Title = "" + task.Result.Data.Title,
                    SubTitle = "Quality:" + res.Format,
                    ToolTipData = new ToolTipData(task.Result.Data.Title, res.Format),
                    Action = _ =>
                    {
                        DownloadVideo(task.Result.Data.Title, task.Result.Data.Url, res.FormatId);
                        return true;
                    },
                    ContextData = new ContextData
                    {
                        format = res.Format,
                        url = task.Result.Data.Url
                    }
                });
                i++;
            }

            return results;
        }
        catch (Exception e)
        {
            Log.Exception("error", e, GetType());
            return
            [
                GetResult("An error occured while fetching URL", e.ToString())
            ];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Name of the plugin.
    /// </summary>
    public string Name => "YTDLP";

    /// <summary>
    ///     Description of the plugin.
    /// </summary>
    public string Description => "PowerToys Run plugin for downloading videos from websites with YTDLP";


    /// <summary>
    ///     Return a filtered list, based on the given query.
    /// </summary>
    /// <param name="query">The query to filter the list.</param>
    /// <returns>A filtered list, can be empty when nothing was found.</returns>
    public List<Result> Query(Query query)
    {
        if (string.IsNullOrEmpty(query.Search))
            return
            [
                GetResult("No search query", "Please enter a search query")
            ];

        return
        [
            new Result
            {
                QueryTextDisplay = "Best Video+Audio",
                IcoPath = IconPath,
                Title = "Best Video+Audio",
                Action = _ =>
                {
                    DownloadVideo("", query.Search, "bestvideo+bestaudio/best");
                    return true;
                }
            },
            GetResult("Loading Qualities...", "")
        ];
    }

    /// <summary>
    ///     Initialize the plugin with the given <see cref="PluginInitContext" />.
    /// </summary>
    /// <param name="context">The <see cref="PluginInitContext" /> for this plugin.</param>
    public void Init(PluginInitContext context)
    {
        // TODO dehardcode this
        ytdl.YoutubeDLPath = @"C:\tools\yt-dlp.exe";
        ytdl.OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";

        Context = context ?? throw new ArgumentNullException(nameof(context));
        Context.API.ThemeChanged += OnThemeChanged;
        UpdateIconPath(Context.API.GetCurrentTheme());
    }

    public Result GetResult(string title, string subtitle)
    {
        return new Result
        {
            QueryTextDisplay = title,
            IcoPath = IconPath,
            Title = title,
            SubTitle = subtitle
        };
    }

    public void DownloadVideo(string filename, string url, string format)
    {
        var progressToast = new ToastContentBuilder()
            .AddText("Downloading Video")
            .AddVisualChild(new AdaptiveProgressBar
            {
                Title = filename,
                Value = new BindableProgressBarValue("progressValue"),
                Status = "Downloading..."
            }).GetToastContent();

        var toast = new ToastNotification(progressToast.GetXml());

        toast.Data = new NotificationData();

        toast.Data.SequenceNumber = 1;
        toast.Tag = "yt-dlp-download";
        toast.Data.Values["progressValue"] = "0";
        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.State == DownloadState.Success)
            {
                ToastNotificationManagerCompat.CreateToastNotifier().Hide(toast);
                return;
            }

            toast.Data.Values["progressValue"] = p.Progress.ToString(CultureInfo.InvariantCulture);
            toast.Data.SequenceNumber += 1;

            ToastNotificationManagerCompat.CreateToastNotifier().Update(toast.Data, "yt-dlp-download");
        });


        Task.Run(async () =>
        {
            var video = await ytdl.RunVideoDownload(url, format, progress: progress);

            if (video.Success)
            {
                Log.Info("Download complete: " + video.Data, GetType());

                ToastNotificationManagerCompat.OnActivated += toastArgs =>
                {
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
            else
            {
                Log.Info("error downloading video: " + string.Join("\n", video.ErrorOutput), GetType());

                new ToastContentBuilder()
                    .AddToastActivationInfo("app", ToastActivationType.Foreground)
                    .AddText("Download failed")
                    .AddArgument("error", video.ErrorOutput.ToString())
                    .Show();
            }
        });
    }

    /// <summary>
    ///     Wrapper method for <see cref="Dispose()" /> that dispose additional objects and events form the plugin itself.
    /// </summary>
    /// <param name="disposing">Indicate that the plugin is disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (Disposed || !disposing) return;
        if (Context?.API != null) Context.API.ThemeChanged -= OnThemeChanged;
        Disposed = true;
    }

    private void UpdateIconPath(Theme theme)
    {
        IconPath = theme == Theme.Light || theme == Theme.HighContrastWhite
            ? "Images/ytdlp.light.png"
            : "Images/ytdlp.dark.png";
    }

    private void OnThemeChanged(Theme currentTheme, Theme newTheme)
    {
        UpdateIconPath(newTheme);
    }
}