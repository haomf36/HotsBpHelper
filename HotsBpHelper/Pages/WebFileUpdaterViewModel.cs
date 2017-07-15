﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HotsBpHelper.Api;
using HotsBpHelper.Api.Model;
using HotsBpHelper.Models;
using HotsBpHelper.Utils;
using RestSharp.Extensions;
using Stylet;

namespace HotsBpHelper.Pages
{
    public class WebFileUpdaterViewModel : ViewModelBase
    {
        private readonly IRestApi _restApi;

        private Form1 form1 = new Form1();
        private bool isBallowShow = false;

        //private int percent,count=0, lastBalloon = 0;

        public BindableCollection<FileUpdateInfo> FileUpdateInfos { get; set; } = new BindableCollection<FileUpdateInfo>();

        public WebFileUpdaterViewModel(IRestApi restApi)
        {
            _restApi = restApi;
        }

        protected override async void OnViewLoaded()
        {
            base.OnViewLoaded();
            await GetFileList();
            await DownloadNeededFiles();
            CheckFiles();
        }

        private void CheckFiles()
        {
            try
            {
                if (FileUpdateInfos.Any(fui => fui.FileStatus == L("UpdateFailed")))
                {
                    ErrorView _errorView = new ErrorView(L("FileUpdateFail"), L("FilesNotReady"), "http://www.bphots.com/articles/QA/");
                    _errorView.ShowDialog();
                    //ShowMessageBox(L("FilesNotReady"),  MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    RequestClose(false);
                }
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ErrorView _errorView = new ErrorView(L("FilesNotReady"), e.Message, "http://www.bphots.com/articles/QA/");
                RequestClose(false);
                return;
            }
            RequestClose(true);
        }

        private async Task GetFileList()
        {
            List<RemoteFileInfo> remoteFileInfos;
            try
            {
                remoteFileInfos = await _restApi.GetRemoteFileListAsync();
                Logger.Trace("Remote files:\r\n{0}", string.Join("\r\n", remoteFileInfos.Select(rfi => rfi.Name)));
            }
            catch (Exception e)
            {
                Logger.Error(e);
                ErrorView _errorView = new ErrorView(L("FilesNotReady"), e.Message, "http://www.bphots.com/articles/QA/");
                //ShowMessageBox(L("FilesNotReady"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
                RequestClose(false);
                return;
            }
            FileUpdateInfos.AddRange(remoteFileInfos.Select(fi => new FileUpdateInfo
            {
                FileName = fi.Name,
                Url = fi.Url,
                RemoteMD5 = fi.MD5,
                LocalFilePath = Path.Combine(App.AppPath, Const.LOCAL_WEB_FILE_DIR, fi.Name.TrimStart('/')),
                FileStatus = L("Updating"),
            }));
        }

        private async Task DownloadNeededFiles()
        {
            await Task.Run(() =>
            {
                
                foreach (var fileUpdateInfo in FileUpdateInfos)
                {
                    if (NeedUpdate(fileUpdateInfo))
                    {
                        try
                        {
                            form1.xuMing();
                            if (!isBallowShow)
                            {
                                form1.ShowBallowNotify();
                                isBallowShow = true;
                            }
                            Logger.Trace("Downloading file: {0}", fileUpdateInfo.FileName);
                            byte[] content = _restApi.DownloadFile(fileUpdateInfo.Url);
                            content.SaveAs(fileUpdateInfo.LocalFilePath);
                            Logger.Trace("Downloaded. Bytes count: {0}", content.Length);
                            if (NeedUpdate(fileUpdateInfo)) fileUpdateInfo.FileStatus = L("UpdateFailed");
                            else fileUpdateInfo.FileStatus = L("UpToDate");
                            Logger.Trace("File status: {0}", fileUpdateInfo.FileStatus);
                            FileUpdateInfos.Refresh();
                        }
                        catch (Exception e)
                        {
                            fileUpdateInfo.FileStatus = L("UpdateFailed");
                            Logger.Error(e, "Downloading error.");
                        }
                    }
                    else
                    {
                        fileUpdateInfo.FileStatus = L("UpToDate");
                    }
                }
            });
        }

        private bool NeedUpdate(FileUpdateInfo fileUpdateInfo)
        {
            if (!File.Exists(fileUpdateInfo.LocalFilePath))
            {
                // ReSharper disable once AssignNullToNotNullAttribute
                Directory.CreateDirectory(Path.GetDirectoryName(fileUpdateInfo.LocalFilePath));
                return true;
            }
            string localMd5 = Md5Util.CaculateFileMd5(fileUpdateInfo.LocalFilePath).ToLower();
            string remoteMd5 = fileUpdateInfo.RemoteMD5.Trim().ToLower();
            return localMd5 != remoteMd5;
        }
    }
}