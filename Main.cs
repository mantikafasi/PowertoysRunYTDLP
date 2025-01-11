using ManagedCommon;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Windows.Storage;
using Windows.System;
using Windows.UI.Notifications;
using ABI.Windows.UI.Notifications;
using Microsoft.Toolkit.Uwp.Notifications;
using Wox.Plugin;
using Wox.Plugin.Logger;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using NotificationData = Windows.UI.Notifications.NotificationData;
using ToastNotification = Windows.UI.Notifications.ToastNotification;
using Microsoft.PowerToys.Settings.UI.Library;

namespace Community.PowerToys.Run.Plugin.YTDLP
{
     
    class ContextData
    {
        public string url;
        public string format;
    }
    /// <summary>
    /// Main class of this plugin that implement all used interfaces.
    /// </summary>
    public class Main : IPlugin, IContextMenu, IDisposable
    {
        YoutubeDL ytdl = new();
        
        /// <summary>
        /// ID of the plugin.
        /// </summary>
        public static string PluginID => "323D66BA5A384DA3A443F5302B10CC3D";

        /// <summary>
        /// Name of the plugin.
        /// </summary>
        public string Name => "YTDLP";

        /// <summary>
        /// Description of the plugin.
        /// </summary>
        public string Description => "YTDLP Description";

        private PluginInitContext Context { get; set; }

        private string IconPath { get; set; }

        private bool Disposed { get; set; }

        public Result GetResult(string title, string description)
        {
            return new Result()
            {
                QueryTextDisplay = title,
                IcoPath = IconPath,
                Title = title,
            };
        }
        
        /// <summary>
        /// Return a filtered list, based on the given query.
        /// </summary>
        /// <param name="query">The query to filter the list.</param>
        /// <returns>A filtered list, can be empty when nothing was found.</returns>
        public List<Result> Query(Query query)
        {
            if (String.IsNullOrEmpty(query.Search))
            {
                return [GetResult("No search query", "Please enter a search query")];   
            };
            
            try
            {
                var search = query.Search;
                var formats = Task.Run(() => ytdl.RunVideoDataFetch(search, overrideOptions: new OptionSet() {FormatSort = "quality, hasvid, hasaudio, fps"}));
                formats.Wait();
            
                
                if (!formats.Result.Success)
                {
                    return
                    [
                        GetResult("An error occured while fetching URL", "Please check the URL and try again")
                    ];
                }

                var results = new List<Result>();

                var formatsData = formats.Result.Data.Formats.ToList();
                formatsData.Add(new FormatData()
                {
                    Format = "Best Video+Audio",
                    FormatId = "bestvideo+bestaudio/best",
                    Url = formats.Result.Data.Url
                });

                formatsData.Reverse();

                int i = 0;
                foreach (var res in formatsData)
                {
                    
                    results.Add(new Result()
                    {
                        Score = formatsData.Count - i,
                        QueryTextDisplay = search,
                        IcoPath = IconPath,
                        Title = "" + formats.Result.Data.Title,
                        SubTitle = "Quality:" + res.Format,
                        ToolTipData = new ToolTipData(formats.Result.Data.Title, res.Format),
                        Action = _ =>
                        {
                            var progressToast = new ToastContentBuilder()
                                .AddText("Downloading Video")
                                .AddVisualChild(new AdaptiveProgressBar()
                                {
                                    Title = formats.Result.Data.Title,
                                    Value = new BindableProgressBarValue("progressValue"),
                                    Status = "Downloading..."
                                        
                                }).GetToastContent();
                                
                            var toast = new ToastNotification(progressToast.GetXml());
                                
                            toast.Data = new NotificationData();

                            toast.Data.SequenceNumber = 1;
                            toast.Tag = "yt-dlp-download";
                            toast.Data.Values["progressValue"] = "0";
                            ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
                            
                            var progress = new Progress<DownloadProgress>((p) =>
                            {
                                if (p.State == DownloadState.Success)
                                {
                                    
                                    ToastNotificationManagerCompat.CreateToastNotifier().Hide(toast);
                                    
                                    return;
                                }
                                // Log.Info("Progress: " + p.Progress.ToString(), GetType());
                                toast.Data.Values["progressValue"] = p.Progress.ToString(CultureInfo.InvariantCulture);
                                toast.Data.SequenceNumber += 1;

                                ToastNotificationManagerCompat.CreateToastNotifier().Update(toast.Data, "yt-dlp-download");

                            });

                            
                            Task.Run(async () =>
                            {
                                                                
  
                                
                                var downloadTask = ytdl.RunVideoDownload(search, res.FormatId, progress:progress);

                                
                       
                                var video = await downloadTask;
                                
                                if (video.Success)
                                {
                                    Log.Info("Download complete: " + video.Data, GetType());
                                    
                                    ToastNotificationManagerCompat.OnActivated += toastArgs =>
                                    {
                                        Log.Info(toastArgs.Argument, GetType());
                                        ToastArguments args = ToastArguments.Parse(toastArgs.Argument);
                                        System.Diagnostics.Process.Start("explorer.exe", "/select, \"" + video.Data + "\"");
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
                            
                            return true;
                        },
                        ContextData = new ContextData()
                        {
                            format =  res.Format,
                            url = formats.Result.Data.Url
                        },
                    });   
                    i++;
                }

                return results;

            } catch (Exception e)
            {
                Log.Exception("error", e, GetType());
                return
                [
                    GetResult("An error occured while fetching URL", e.ToString())
                ];
            }
        }

        /// <summary>
        /// Initialize the plugin with the given <see cref="PluginInitContext"/>.
        /// </summary>
        /// <param name="context">The <see cref="PluginInitContext"/> for this plugin.</param>
        public void Init(PluginInitContext context)
        {
            // TODO dehardcode this
            ytdl.YoutubeDLPath = @"C:\tools\yt-dlp.exe";
            ytdl.OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
            
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Context.API.ThemeChanged += OnThemeChanged;
            UpdateIconPath(Context.API.GetCurrentTheme());
        }

        /// <summary>
        /// Return a list context menu entries for a given <see cref="Result"/> (shown at the right side of the result).
        /// </summary>
        /// <param name="selectedResult">The <see cref="Result"/> for the list with context menu entries.</param>
        /// <returns>A list context menu entries.</returns>
        public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
        {
            if (selectedResult.ContextData is string search)
            {
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
                        },
                    }
                ];
            }

            return [];
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Wrapper method for <see cref="Dispose()"/> that dispose additional objects and events form the plugin itself.
        /// </summary>
        /// <param name="disposing">Indicate that the plugin is disposed.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (Disposed || !disposing)
            {
                return;
            }

            if (Context?.API != null)
            {
                Context.API.ThemeChanged -= OnThemeChanged;
            }

            Disposed = true;
        }

        private void UpdateIconPath(Theme theme) => IconPath = theme == Theme.Light || theme == Theme.HighContrastWhite ? "Images/ytdlp.light.png" : "Images/ytdlp.dark.png";

        private void OnThemeChanged(Theme currentTheme, Theme newTheme) => UpdateIconPath(newTheme);
    }
}
