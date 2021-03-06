﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using Chromium;
using DotNetHelper;
using GlobalHotKey;
using HotsBpHelper.Api;
using HotsBpHelper.Api.Model;
using HotsBpHelper.Api.Security;
using HotsBpHelper.Configuration;
using HotsBpHelper.Factories;
using HotsBpHelper.Services;
using HotsBpHelper.Settings;
using HotsBpHelper.Uploader;
using HotsBpHelper.UserControls;
using HotsBpHelper.Utils;
using HotsBpHelper.WPF;
using ImageProcessor.Ocr;
using LobbyFileParser;
using NAppUpdate.Framework;
using NAppUpdate.Framework.Sources;
using NAppUpdate.Framework.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Stylet;
using Application = System.Windows.Application;
using MessageBox = System.Windows.Forms.MessageBox;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace HotsBpHelper.Pages
{
    public class ShellViewModel : ViewModelBase
    {
        private readonly HotKeyManager _hotKeyManager;

        private readonly IImageUtil _imageUtil;
        private NotifyTaskCompletion<double> _notifyGetTimeStampTaskCompleted;
        private readonly ISecurityProvider _securityProvider;

        private readonly IToastService _toastService;
        private bool _autoDetect;

        private bool _autoShowHideHelper;
        private bool _autoShowMMR;

        private BpViewModel _bpViewModel;
        private bool _initializeReset;

        private bool _isLoaded;

        private MMRViewModel _mmrViewModel;
        private string _percentageInfo;
        private readonly ViewModelFactory _viewModelFactory;
        private readonly IRestApi _restApi;
        private NotifyTaskCompletion<bool> _notifyUpdateTaskCompleted;
        private bool _canOcr;

        public ShellViewModel(ViewModelFactory viewModelFactory, IImageUtil imageUtil, IToastService toastService,
            IRestApi restApi, ISecurityProvider securityProvider, IEventAggregator eventAggregator)
        {
            _viewModelFactory = viewModelFactory;
            _eventAggregator = eventAggregator;
            _imageUtil = imageUtil;
            _toastService = toastService;
            _securityProvider = securityProvider;
            _restApi = restApi;

            _hotKeyManager = new HotKeyManager();
            CefInitializer.InitializeCef();

            PercentageInfo = L("Loading");
            
            using (var mutex = new Mutex(false, "Global\\" + Const.HOTSBPHELPER_PROCESS_NAME))
            {
                if (!mutex.WaitOne(0, false))
                {
                    Application.Current.Shutdown();
                    return;
                }
            }

            if (Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1)
            {
                Exit();
            }
        }

        public bool AutoShowHideHelper
        {
            get { return _autoShowHideHelper; }
            set
            {
                if (_bpViewModel == null)
                    return;

                if (_bpViewModel.AutoShowHideHelper == value)
                    return;

                if (value)
                {
                    var hotsConfig = new HotsVariableConfigParser();
                    if (!hotsConfig.CheckIfWindowlessMax())
                    {
                        TopMostMessageBox.Show(L("WindowlessWarning"), @"Warning");
                        AutoDetect = false;
                        return;
                    }
                }

                _bpViewModel.AutoShowHideHelper = value;
                if (SetAndNotify(ref _autoShowHideHelper, value))
                {
                    if (_initializeReset)
                    {
                        ResetHelper();
                        BpViewModelOnRemindBpMode(null, EventArgs.Empty);
                    }
                }

                NotifyOfPropertyChange(() => AutoShowHideHelperInputGestureText);
                NotifyOfPropertyChange(() => ManualShowHideHelperInputGuestrueText);
                NotifyOfPropertyChange(() => CanManualShowHelper);
                NotifyOfPropertyChange(() => ShowHideHelperTip);
            }
        }

        public bool AutoDetect
        {
            get { return _autoDetect; }
            set
            {
                if (_bpViewModel == null)
                    return;

                if (_bpViewModel.IsAutoMode == value)
                    return;

                if (value)
                {
                    var hotsConfig = new HotsVariableConfigParser();
                    if (!hotsConfig.CheckIfWindowlessMax())
                    {
                        TopMostMessageBox.Show(L("WindowlessWarning"), @"Warning");
                        AutoShowHideHelper = false;
                        return;
                    }

                    if (App.AppSetting.Position.Height < Const.BestExpericenResolutionHeight && TopMostMessageBox.Show(L("ResolutionQuestion"), @"Warning",
                            MessageBoxButtons.YesNo) == DialogResult.No)
                            return;

                    if (!OcrEngine.IsTessDataAvailable(App.OcrLanguage))
                    {
                        IsLoaded = false;
                        if (TopMostMessageBox.Show(L("TessdataQuestion"), @"Warning",
                            MessageBoxButtons.YesNo) == DialogResult.Yes)
                        {
                            var tessdataWebUpdateVm = _viewModelFactory.CreateViewModel<WebFileUpdaterViewModel>();
                            var languageParams = OcrEngine.GetDirectory(App.OcrLanguage);
                            tessdataWebUpdateVm.ShellViewModel = this;
                            tessdataWebUpdateVm.UpdateCompleted += OnTessdataFileReinitializeCompleted;
                            tessdataWebUpdateVm.SetPaths(languageParams[0], languageParams[1]);
                            WindowManager.ShowDialog(tessdataWebUpdateVm);
                        }
                        else
                        {
                            IsLoaded = true;
                            return;
                        }
                    }
                }

                SetAndNotify(ref _autoDetect, value);

                _bpViewModel.IsAutoMode = value;
            }
        }

        private void OnTessdataFileReinitializeCompleted(object sender, EventArgs e)
        {
            _bpViewModel.ReInitializeOcr();
            IsLoaded = true;
        }

        public bool AutoShowMmr
        {
            get { return _autoShowMMR; }
            set
            {
                if (_autoShowMMR == value)
                    return;
                
                SetAndNotify(ref _autoShowMMR, value);
            }
        }

        public string PercentageInfo
        {
            get { return _percentageInfo; }
            set { SetAndNotify(ref _percentageInfo, value); }
        }

        public bool CanOcr { get { return _canOcr; } set { SetAndNotify(ref _canOcr, value); } }

        public bool IsLoaded
        {
            get { return _isLoaded; }
            set
            {
                SetAndNotify(ref _isLoaded, value);
                NotifyOfPropertyChange(() => CanManualShowHelper);

                NotifyOfPropertyChange(() => ServiceNotRunning);
                NotifyOfPropertyChange(() => IsServiceRunning);
            }
        }

        private bool _loadingFailed;

        private void OnTimeStampCompleted(object sender, EventArgs e)
        {
            try
            {
                if (_notifyGetTimeStampTaskCompleted != null && !_notifyGetTimeStampTaskCompleted.IsSuccessfullyCompleted)
                {
                    Logger.Error("NotifyGetTimeStampTask failed.");
                    _loadingFailed = true;
                }

                ReceiveBroadcast();
            }
            catch
            {
                // ignored
            }

            if (!App.Debug)
            {
                // ²»ÊÇµ÷ÊÔÄ£Äâ,Ôò¼ì²é¸üÐÂ
                _notifyUpdateTaskCompleted = new NotifyTaskCompletion<bool>(UpdateAsync());
                _notifyUpdateTaskCompleted.TaskStopped += OnFeedUpdateCompleted;
                if (_notifyUpdateTaskCompleted.IsCompleted)
                    OnFeedUpdateCompleted(this, EventArgs.Empty);
            }
            else
                OnFeedUpdateCompleted(this, EventArgs.Empty);
        }

        protected override void OnViewLoaded()
        {
            _notifyGetTimeStampTaskCompleted = new NotifyTaskCompletion<double>(InitializeApiAsync());
            _notifyGetTimeStampTaskCompleted.TaskStopped += OnTimeStampCompleted;
            if (_notifyGetTimeStampTaskCompleted.IsCompleted)
                OnTimeStampCompleted(this, EventArgs.Empty);
            
            InitSettings();
            
            base.OnViewLoaded();

            _toastService.ShowInformation(L("Loading"));
        }
        
        private async Task<double> InitializeApiAsync()
        {
            var timeStamp = await _restApi.GetTimestamp();
            
            _securityProvider.SetServerTimestamp(timeStamp);

            App.AdviceHeroInfos = await _restApi.GetHeroListV2();
            App.AdviceMapInfos = await _restApi.GetMapListV2();
            var lobbyHeroList = await _restApi.GetLobbyHeroList(App.Language);
            var lobbyMapList = await _restApi.GetLobbyMapList(App.Language);
            App.LobbyHeroes = lobbyHeroList.Where(h => !h.IsNew).Select(h => h.Name).ToList();
            App.LobbyMaps = lobbyMapList.Select(v => v.Name).ToList();

            if (!string.IsNullOrEmpty(App.CustomConfigurationSettings.LanguageForGameClient))
            {
                App.OcrHeroInfos = HeroInfoV2.ToHeroInfoList(App.AdviceHeroInfos, App.CustomConfigurationSettings.LanguageForGameClient);

                foreach (var heroInfo in App.OcrHeroInfos)
                {
                    OcrEngine.CandidateHeroes.Add(heroInfo.Name);
                }

                App.OcrMapInfos = MapInfoV2.ToMapInfoList(App.AdviceMapInfos, App.CustomConfigurationSettings.LanguageForGameClient);

                foreach (var mapInfo in App.OcrMapInfos)
                {
                    OcrEngine.CandidateMaps.Add(mapInfo.Name);
                }

            }

            Logger.Trace("Api initialized");
            return timeStamp;
        }

        private void ReceiveBroadcast()
        {
            //���չ��棬���ԶԻ������ʽ��ʾ
            var broadcastList = _restApi.GetBroadcastInfo("0", App.Language);
            if (broadcastList != null)
            {
                Execute.OnUIThreadSync(() =>
                {
                    try
                    {
                        foreach (var broadcast in broadcastList)
                        {
                            if (broadcast.Type == 0)
                            {
                                var b = new BroadcastWindow(broadcast.Msg, broadcast.Url);
                                b.Show();
                            }
                        }
                        foreach (var broadcast in broadcastList)
                        {
                            if (broadcast.Type == 1)
                            {
                                var e = new ErrorView(L("Reminder"), broadcast.Msg, broadcast.Url);
                                e.ShowDialog();
                            }
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                });
            }
            
            Logger.Trace("Broadcast loaded");
        }

        private void OnFeedUpdateCompleted(object sender, EventArgs e)
        {
            if (_notifyUpdateTaskCompleted != null && !_notifyUpdateTaskCompleted.IsSuccessfullyCompleted)
            {
                Logger.Trace("Upldate failed");
                DisplayFatalMessage(L("UpdateFailed"));
                return;
            }

            if (_loadingFailed)
            {
                DisplayFatalMessage(L("ApiFailed"));
                return;
            }

            Logger.Trace("Update completed");
            if (!App.Debug)
                Task.Run(() => FileUtil.CleanUpImageTestFiles());

            Execute.OnUIThread(() =>
            {
                if (!App.Debug)
                {
                    var webUpdateVm = _viewModelFactory.CreateViewModel<WebFileUpdaterViewModel>();
                    webUpdateVm.ShellViewModel = this;
                    webUpdateVm.UpdateCompleted += OnWebFileUpdateCompleted;
                    if (WindowManager.ShowDialog(webUpdateVm) != true)
                        Exit();
                }
                else
                    OnWebFileUpdateCompleted(this, EventArgs.Empty);
            });
        }

        private void DisplayFatalMessage(string message)
        {
            Execute.OnUIThread(() =>
            {
                var errorView = new ErrorView(L("ErrorTitle"), message);
                errorView.ShowDialog();
                errorView.Pause();
                Exit();
            });
        }
        
        private void OnTessdataFileUpdateCompleted(object sender, EventArgs e)
        {
            Logger.Trace("Tessdata initialzed");

            _mmrViewModel = _viewModelFactory.CreateViewModel<MMRViewModel>();
            _mmrViewModel.HideBrowser();
            WindowManager.ShowWindow(_mmrViewModel);
            _mmrViewModel.Hide();

            _bpViewModel = _viewModelFactory.CreateViewModel<BpViewModel>();
            WindowManager.ShowWindow(_bpViewModel);
            _bpViewModel.Hide();

            ManagerViewModel.TabChanged += OnManagerTabChanged;
            
            RegisterHotKey();
            IsLoaded = true;

            CanOcr = true;

            if (App.OcrLanguage == OcrLanguage.Unavailable && App.CustomConfigurationSettings.AutoDetectHeroAndMap)
            {
                TopMostMessageBox.Show(L("LanguageUnavailable"), @"Warning");
                CanOcr = false;
            }
            else if (!_bpViewModel.OcrAvailable && App.CustomConfigurationSettings.AutoDetectHeroAndMap)
                _toastService.ShowWarning(L("OcrUnavailable"));

            if (App.AppSetting.Position.Height < Const.IncompatibleResolutionHeight && App.CustomConfigurationSettings.AutoDetectHeroAndMap)
            {
                CanOcr = false;
                _toastService.ShowWarning(L("IncompatibleResolution"));
            }

            bool isWindowlessMax = true;
            var hotsConfig = new HotsVariableConfigParser();
            if (!hotsConfig.CheckIfWindowlessMax())
            {
                TopMostMessageBox.Show(L("WindowlessWarning"), @"Warning");
                isWindowlessMax = false;
            }

            BpServiceConfigParser.PopulateConfigurationSettings();
            if (!App.HasServiceAsked)
            {
                if (ServiceNotRunning)
                {
                    if (TopMostMessageBox.Show(L(@"ServiceQuestion"),
                        @"Question",
                        MessageBoxButtons.YesNo) == DialogResult.Yes)
                        SetAutoStart();
                }
                else
                {
                    if (TopMostMessageBox.Show(L(@"ServiceRestartQuestion"),
                       @"Question",
                       MessageBoxButtons.YesNo) == DialogResult.Yes)
                        ServiceRestart();
                }

            }

            App.HasServiceAsked = true;
            BpServiceConfigParser.WriteConfig();
            AutoDetect = App.CustomConfigurationSettings.AutoDetectHeroAndMap && CanOcr && _bpViewModel.OcrAvailable && App.AppSetting.Position.Height > Const.BestExpericenResolutionHeight && isWindowlessMax;
            AutoShowHideHelper = App.CustomConfigurationSettings.AutoShowHideHelper && isWindowlessMax;
            AutoShowMmr = App.CustomConfigurationSettings.AutoShowMMR && isWindowlessMax; 

            _bpViewModel.RemindDetectMode += BpViewModelOnRemindDetectMode;
            _bpViewModel.RemindBpStart += BpViewModelOnRemindGameStart;
            _bpViewModel.TurnOffAutoDetectMode += BpViewModelOnTurnOffAutoDetectMode;

            _toastService.CloseMessages(L("Loading"));
            _toastService.ShowInformation(L("Started") + Environment.NewLine + L("StartedTips"));

            _initializeReset = true;

            Logger.Trace("BpHelper successfully loaded");
            Upload();
            Task.Run(CheckFocusAsync).ConfigureAwait(false);
            Task.Run(MonitorInGameAsync).ConfigureAwait(false);
            Task.Run(MonitorLobbyFile).ConfigureAwait(false);

            WebCallbackListener.StartServiceRequested += WebCallbackListenerOnStartServiceRequested;
            WebCallbackListener.StopServiceRequested += WebCallbackListenerOnStopServiceRequested;
        }

        private void WebCallbackListenerOnStopServiceRequested(object sender, EventArgs eventArgs)
        {
            StopService();
        }

        private void InformServiceStatus()
        {
            _eventAggregator.PublishOnUIThread(new InvokeScriptMessage
            {
                ScriptName = "setIsServiceRunning",
                Args = new[] {JsonConvert.SerializeObject(IsServiceRunning)}
            }, "ManagerChannel");
        }

        private void WebCallbackListenerOnStartServiceRequested(object sender, EventArgs eventArgs)
        {
            SetAutoStart();
        }

        private void OnManagerTabChanged(object sender, EventArgs e)
        {
            NotifyOfPropertyChange(() => CanShowSettings);
            NotifyOfPropertyChange(() => CanShowReplays);
            NotifyOfPropertyChange(() => CanShowAbout);
        }

        private void Upload()
        {
            FilePath path = Path.GetFullPath(@".\Replay\replayRecords.xml");
            if (!path.GetDirPath().Exists)
                Directory.CreateDirectory(path.GetDirPath());

            _uploadManager = new Manager(new ReplayStorage(path), _restApi);
            _uploadManager.StatusChanged += OnUploadStateChanged;
            _uploadManager.Start();
        }

        private Manager _uploadManager;

        private void OnUploadStateChanged(object sender, EventArgs e)
        {
            NotifyOfPropertyChange(() => UploadStatusDescription);
        }

        public string UploadStatusDescription
        {
            get
            {
                if (_uploadManager == null)
                    return L("Loading Uploader...");
                
                return _uploadManager.Status;
            }
        }

        public string SwitchUploadDescription => Manager.ManualSuspend ? L("Resume Uploading") : L("Suspend Uploading");

        public string AutoShowHideHelperInputGestureText => AutoShowHideHelper ? "Ctrl+Shift+B" : string.Empty;

        public string ManualShowHideHelperInputGuestrueText => AutoShowHideHelper ? string.Empty : "Ctrl+Shift+B";

        public bool CanManualShowHelper => !AutoShowHideHelper && !OcrUtil.InGame && IsLoaded;

        public void SwitchUpload()
        {
            Manager.ManualSuspend = !Manager.ManualSuspend;
            NotifyOfPropertyChange(() => SwitchUploadDescription);
            NotifyOfPropertyChange(() => UploadStatusDescription);
        }

        private void OnWebFileUpdateCompleted(object sender, EventArgs e)
        {
            Logger.Trace("Webfiles initialzed");

            if (App.OcrLanguage == OcrLanguage.Unavailable || !App.CustomConfigurationSettings.AutoDetectHeroAndMap)
            {
                OnTessdataFileUpdateCompleted(this, EventArgs.Empty);
                return;
            }

            var tessdataWebUpdateVm = _viewModelFactory.CreateViewModel<WebFileUpdaterViewModel>();
            tessdataWebUpdateVm.ShellViewModel = this;
            tessdataWebUpdateVm.UpdateCompleted += OnTessdataFileUpdateCompleted;

            var languageParams = OcrEngine.GetDirectory(App.OcrLanguage);
            if (!OcrEngine.IsTessDataAvailable(App.OcrLanguage))
            {
                if (TopMostMessageBox.Show(L("TessdataQuestion"), @"Warning",
                        MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    tessdataWebUpdateVm.SetPaths(languageParams[0], languageParams[1]);
                    WindowManager.ShowDialog(tessdataWebUpdateVm);
                }
                else
                    OnTessdataFileUpdateCompleted(this, EventArgs.Empty);
            }
            else
            {
                tessdataWebUpdateVm.SetPaths(languageParams[0], languageParams[1]);
                WindowManager.ShowDialog(tessdataWebUpdateVm);
            }
        }

        private void BpViewModelOnTurnOffAutoDetectMode(object sender, EventArgs e)
        {
            Execute.OnUIThread(() => AutoDetect = false);
        }

        private void BpViewModelOnRemindDetectMode(object sender, EventArgs eventArgs)
        {
            var onText = L("OcrModeOnTitle") + Environment.NewLine + L("OcrModeOnToolTip");
            var offText = L("OcrModeOffTitle") + Environment.NewLine + L("OcrModeOffToolTip");
            if (AutoDetect)
            {
                _toastService.CloseMessages(offText);
                _toastService.ShowSuccess(onText);
            }
            else
            {
                _toastService.CloseMessages(onText);
                _toastService.ShowInformation(offText);
            }
        }

        private void BpViewModelOnRemindBpMode(object sender, EventArgs eventArgs)
        {
            var onText = L("MatchDetectOnTitle") + Environment.NewLine + L("StartedTips");
            var offText = L("MatchDetectOffTitle") + Environment.NewLine + L("AutoBpScreenModeOnToolTip");
            if (_autoShowHideHelper)
            {
                _toastService.CloseMessages(offText);
                _toastService.ShowSuccess(onText);
            }
            else
            {
                _toastService.CloseMessages(onText);
                _toastService.ShowInformation(offText);
            }
        }

        private void BpViewModelOnRemindGameStart(object sender, EventArgs eventArgs)
        {
            if (_autoDetect)
            {
                _toastService.ShowSuccess(L("OcrModeToolTipTitle") + Environment.NewLine + L("OcrModeOnToolTip"));
            }
            else
            {
                _toastService.ShowSuccess(L("OcrModeToolTipTitle") + Environment.NewLine + L("OcrModeOffToolTip"));
            }
        }

        private async Task MonitorLobbyFile()
        {
            var lobbyLastModified = DateTime.MinValue;
            while (true)
            {
                if (AutoShowMmr)
                {
                    if (File.Exists(Const.BattleLobbyPath) && File.GetLastWriteTime(Const.BattleLobbyPath) != lobbyLastModified)
                    {
                        lobbyLastModified = File.GetLastWriteTime(Const.BattleLobbyPath);
                        Execute.OnUIThread(() =>
                        {
                            ((MMRView)_mmrViewModel.View).Browser.InitializeBrowser(_mmrViewModel.LocalFileUri);
                        });
                    }
                }
                
                await Task.Delay(1000);
            }
        }

        public void ShowMmr()
        {
            ((MMRView) _mmrViewModel?.View)?.Browser?.InitializeBrowser(_mmrViewModel.LocalFileUri);
        }

        private async Task MonitorInGameAsync()
        {
            var lobbyLastModified = DateTime.MinValue;
            while (true)
            {
                if (File.Exists(Const.BattleLobbyPath) && File.GetLastWriteTime(Const.BattleLobbyPath) != lobbyLastModified)
                {
                    _uploadManager?.Monitor?.Start();

                    lobbyLastModified = File.GetLastWriteTime(Const.BattleLobbyPath);
                    Execute.OnUIThread(() =>
                    {
                        _toastService.DisposeManager();
                        _bpViewModel.Reset(); 
                        ((BpView)_bpViewModel.View).Browser.DisposeBrowser();
                        NotifyOfPropertyChange(() => ShowHideHelperTip);
                    });
                }

                if (File.Exists(Const.BattleLobbyPath))
                {
                    if (_bpViewModel.ProcessingThreads.All(t => !t.Value) && _bpViewModel.OcrUtil.IsInitialized)
                    {
                        _bpViewModel.OcrUtil?.Dispose();
                    }

                    if (!OcrUtil.InGame)
                    {
                        OcrUtil.InGame = true;
                        Execute.OnUIThread(() => NotifyOfPropertyChange(() => CanManualShowHelper));
                        Execute.OnUIThread(() => NotifyOfPropertyChange(() => ShowHideHelperTip));
                        Execute.OnUIThread(() => NotifyOfPropertyChange(() => IsStatsVisible));
                    }

                    if (!Manager.IngameSuspend)
                        Manager.IngameSuspend = true;
                }

                if (!File.Exists(Const.BattleLobbyPath) && OcrUtil.InGame)
                {
                    Execute.OnUIThread(() => _toastService.ReinitializeToast());
                    if (!_bpViewModel.OcrUtil.IsInitialized)
                    {
                        _bpViewModel.OcrUtil.Initialize();
                        ((BpView)_bpViewModel.View).Browser.InitializeBrowser(_bpViewModel.LocalFileUri);
                    }
                    
                    OcrUtil.InGame = false;
                    Execute.OnUIThread(() => NotifyOfPropertyChange(() => CanManualShowHelper));
                    Execute.OnUIThread(() => NotifyOfPropertyChange(() => ShowHideHelperTip));
                    Execute.OnUIThread(() => NotifyOfPropertyChange(() => IsStatsVisible));
                    Manager.IngameSuspend = false;
                }

                await Task.Delay(1000);
            }
        }

        private void RegisterHotKey()
        {
            try
            {
                _hotKeyManager.Register(Key.B, ModifierKeys.Control | ModifierKeys.Shift);
                _hotKeyManager.Register(Key.C, ModifierKeys.Control | ModifierKeys.Shift);
                _hotKeyManager.Register(Key.M, ModifierKeys.Control | ModifierKeys.Shift);
                _hotKeyManager.Register(Key.N, ModifierKeys.Control | ModifierKeys.Shift);
                _hotKeyManager.Register(Key.R, ModifierKeys.Control | ModifierKeys.Shift);
                _hotKeyManager.Register(Key.D, ModifierKeys.Control | ModifierKeys.Shift);
                _hotKeyManager.KeyPressed += HotKeyManagerPressed;
            }
            catch (Exception e)
            {
                Logger.Error(e);
                /*
                Pages.ErrorView _errorView = new Pages.ErrorView(L("RegisterHotKeyFailed"),e.Message);
                _errorView.isShutDown = false;
                _errorView.ShowDialog();
                */
                ShowMessageBox(L("RegisterHotKeyFailed"), MessageBoxButton.OK, MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
        }

        private async Task CheckFocusAsync()
        {
            var lastStatus = 0;
            bool hotsClosed = true;
            while (true)
            {
                await Task.Delay(1000);
                if (_bpViewModel == null)
                    continue;

                var hwnd = Win32.GetForegroundWindow();
                var pid = Win32.GetWindowProcessID(hwnd);
                var process = Process.GetProcessById(pid);
                var hotsProcess = Process.GetProcessesByName(Const.HEROES_PROCESS_NAME).Union(Process.GetProcessesByName(Const.HEROES_PROCESS_NAME + "_x64"));
                if (!hotsProcess.Any())
                {
                    if (!OcrUtil.NotInFocus && lastStatus != 2)
                    {
                        OcrUtil.NotInFocus = true;
                        lastStatus = 2;
                    }

                    if (!hotsClosed && _bpViewModel.BpScreenLoaded)
                    {
                        Execute.OnUIThread(() =>
                        {
                            _bpViewModel.Reset();
                        });
                        hotsClosed = true;
                    }
                    continue;
                }

                hotsClosed = false;

                var inHotsGame = process.ProcessName.StartsWith(Const.HEROES_PROCESS_NAME);
                var inHotsHelper = process.ProcessName.StartsWith(Const.HOTSBPHELPER_PROCESS_NAME);
                if (inHotsGame)
                {
                    if (OcrUtil.NotInFocus && lastStatus != 1)
                    {
                        if (_bpViewModel.BpScreenLoaded)
                            Execute.OnUIThread(() => _bpViewModel.Show());
                        OcrUtil.NotInFocus = false;
                        lastStatus = 1;
                    }
                }
                if (!inHotsHelper && !inHotsGame)
                {
                    if (!OcrUtil.NotInFocus && lastStatus != 2)
                    {
                        if (_bpViewModel == null)
                            continue;

                        await Task.Delay(1000);
                        var hwnd2 = Win32.GetForegroundWindow();
                        var pid2 = Win32.GetWindowProcessID(hwnd2);
                        var process2 = Process.GetProcessById(pid2);
                        var inHotsGame2 = process2.ProcessName.StartsWith(Const.HEROES_PROCESS_NAME);
                        var inHotsHelper2 = process2.ProcessName.StartsWith(Const.HOTSBPHELPER_PROCESS_NAME);
                        if (!inHotsHelper2 && !inHotsGame2 && _bpViewModel.BpScreenLoaded)
                            Execute.OnUIThread(() => _bpViewModel.Hide());

                        OcrUtil.NotInFocus = true;
                        lastStatus = 2;
                    }
                }
            }
        }

        private void HotKeyManagerPressed(object sender, KeyPressedEventArgs e)
        {
            if (!IsLoaded)
                return;
            
            if (e.HotKey.Key == Key.B)
            {
                ManuallyShowHideHelper();
            }
            if (e.HotKey.Key == Key.M)
            {
                if (IsStatsVisible)
                    ShowMmr();
            }
            if (e.HotKey.Key == Key.R)
            {
                if (CanOcr)
                    AutoDetect = !_bpViewModel.IsAutoMode;
            }
            if (e.HotKey.Key == Key.N)
            {
                ResetHelper();
            }
            if (e.HotKey.Key == Key.D)
            {
                if (_bpViewModel == null)
                    return; 

                _bpViewModel.ShowDevTool = !_bpViewModel.ShowDevTool;
            }
            else if (e.HotKey.Key == Key.C)
            {
                var captureName = Path.Combine(App.AppPath, "Screenshots",
                    DateTime.Now.ToString("yyyyMMdd_hhmmss") + ".bmp");
                _imageUtil.CaptureScreen().Save(captureName);
            }
        }

        public void SwitchVisibility()
        {
            if (_bpViewModel == null)
                return;

            if (_bpViewModel.View.Visibility == Visibility.Visible)
            {
                _bpViewModel.Hide();
            }
            else
            {
                _bpViewModel.Show();
            }
        }

        public void ResetHelper()
        {
            _bpViewModel?.Reset();
        }

        public void ManuallyShowHideHelper()
        {
            var wasAuto = AutoShowHideHelper;
            AutoShowHideHelper = false;

            if (!wasAuto && !OcrUtil.InGame)
                ToggleVisible(true);

            Execute.OnUIThread(() => NotifyOfPropertyChange(() => ShowHideHelperTip));
        }

        private void ToggleVisible(bool clear)
        {
            if (!_isLoaded)
            {
                return;
            }
            Execute.OnUIThread(() =>
            {
                if (clear)
                {
                    _bpViewModel.CancelAllActiveScan();
                    _bpViewModel.Reset(false);
                }
                _bpViewModel.ToggleVisible();
            });
        }

        private async Task<bool> UpdateAsync()
        {
            await Task.Run(() => Update());
            return true;
        }

        private void Update()
        {
            var updManager = UpdateManager.Instance;
            try
            {
                updManager.ReinstateIfRestarted();
                var source = Const.UPDATE_FEED_XML;
                if (App.ForceUpdate)
                    source += @"&is_debug=1";

                updManager.UpdateSource = new SimpleWebSource(source);
                try
                {
                    updManager.CheckForUpdates();
                }
                catch (InvalidOperationException e)
                {
                    Logger.Error(e);
                    if (e.Message.Contains(@"Already checked for updates"))
                        return;

                    Execute.OnUIThread(() => {
                        var errorView = new ErrorView(L("FileUpdateFail"), e.Message, "https://www.bphots.com/articles/errors/1");
                        errorView.ShowDialog();
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Checking updates exception.");
                    throw;
                }
                Logger.Trace("Need updates files: {0}", updManager.UpdatesAvailable);
                if (updManager.UpdatesAvailable == 0) return;

                Execute.OnUIThread(() => _toastService.ShowInformation(L("UpdateFullText") + Environment.NewLine + L("HotsBpHelper")));

                try
                {
                    updManager.PrepareUpdates();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Preparing updates exception.");
                    throw;
                }

                try
                {
                    foreach (var updateTask in updManager.Tasks)
                    {
                        Logger.Trace(((FileUpdateTask) updateTask).LocalPath);
                    }
                    updManager.ApplyUpdates(true);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Applying updates exception.");
                    ShowMessageBox(string.Format(L("UpdatesFailed"), ex.Message), MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    throw;
                }
            }
            finally
            {
                updManager.CleanUp();
            }
        }

        private void ExpandHeroPropertiesForLatin()
        {
            App.AppSetting.Position.MapPosition = new MapPosition
            {
                Location = new Point((int) (App.AppSetting.Position.Width/2 - 0.25*App.AppSetting.Position.Height), 0),
                Width = (int) (0.5*App.AppSetting.Position.Height),
                Height = (int) (0.03563*App.AppSetting.Position.Height)
            };
        }

        private void InitSettings()
        {
            try
            {
                App.AppSetting = Its.Configuration.Settings.Get<AppSetting>();
                var screenSize = ScreenUtil.GetScreenResolution();
                App.AppSetting.Position = CaculatePosition(screenSize.Width, screenSize.Height);
                
                if (App.OcrLanguage == OcrLanguage.English)
                    ExpandHeroPropertiesForLatin();
            }
            catch (Exception e)
            {
                var _errorView = new ErrorView(e.Message);
                _errorView.ShowDialog();
                _errorView.Pause();
            }
        }

        /// <summary>
        ///     ���ݷֱ��ʶ�̬�������λ�úͳߴ�
        /// </summary>
        private Position CaculatePosition(int width, int height)
        {
            var bpHelperSize = new Size(425, 150);
            var dpiPoint = ScreenUtil.GetSystemDpi();
            double ratio = (double) dpiPoint.X / 96;
            if (App.Debug)
                File.WriteAllText(@".\DPI.txt", dpiPoint.X + @"," + dpiPoint.Y);

            bpHelperSize.Height = (int)(bpHelperSize.Height * ratio);
            bpHelperSize.Width = (int)(bpHelperSize.Width * ratio);
            var bpHelperPosition = new Point(
                (int)(0.31 * height), 0.852 * height + bpHelperSize.Height > height ? height - bpHelperSize.Height - (int)(0.01 * height) : (int)(0.852 * height));
            
            var heroWidth = (int) (0.1074074074074074 * height);
            var heroHeight = (int) (0.087962962962963 * height);
            var position = new Position
            {
                Width = width,
                Height = height,
                BpHelperSize = bpHelperSize,
                BpHelperPosition = bpHelperPosition,
                MapSelectorPosition = new Point((int) (0.5*width), (int) (0.146*height)),
                HeroWidth = heroWidth,
                HeroHeight = heroHeight,
                Left = new SidePosition
                {
                    Ban1 = new Point((int) (0.45*height), (int) (0.016*height)),
                    Ban2 =
                        new Point((int) (0.45*height),
                            (int) (0.016*height) + (int) (0.023*height) + (int) (0.015*height)),
                    Pick1 = new Point((int) (0.195*height), (int) (0.132*height)),
                    Dx = (int) (0.0905*height),
                    Dy = (int) (0.1565*height),
                    HeroPathPoints =
                        new[]
                        {
                            new Point(1, 1), new Point(1, (int) (0.025*height)),
                            new Point(heroWidth, heroHeight),
                            new Point(heroWidth, heroHeight - (int) (0.025*height))
                        },
                    HeroName1 = new Point(RoundUp(0.0138888888888889 * height), RoundUp(0.1398148148148148 * height))
                },
                Right = new SidePosition
                {
                    Ban1 = new Point((int) (width - 0.46*height), (int) (0.016*height)),
                    Ban2 =
                        new Point((int) (width - 0.46*height),
                            (int) (0.016*height) + (int) (0.023*height) + (int) (0.015*height)),
                    Pick1 = new Point((int) (width - 0.205*height), (int) (0.132*height)),
                    Dx = (int) (-0.0905*height),
                    Dy = (int) (0.1565*height),
                    HeroPathPoints =
                        new[]
                        {
                            new Point(heroWidth, 1), new Point(heroWidth, 1 + (int) (0.025*height)),
                            new Point(1, heroHeight),
                            new Point(1, heroHeight - (int) (0.025*height))
                        },
                    HeroName1 = new Point(RoundUp(width - 0.0138888888888889 * height), RoundUp(0.1398148148148148 * height))
                },
                MapPosition = new MapPosition
                {
                    Location = new Point((int) (width * 0.5 - 0.18 * height), 0),
                    Width = (int) (0.36*height),
                    Height = (int) (0.03563*height)
                },
                OverlapPoints = new OverlapPoints()
                {
                    AppearanceFramePoint = new Point(RoundUp(0.0666666666666667 * height), RoundUp(0.4231481481481481 * height)),
                    FrameRightBorderPoint = new Point(RoundUp(width - 0.012037037037037 * height), RoundUp(0.8685185185185185 * height)),
                    SkillFramePoint = new Point(RoundUp(0.0740740740740741 * height), RoundUp(0.7712962962962963 * height)),
                    TalentFramePoint = new Point(RoundUp(0.1435185185185185 * height), RoundUp(0.2953703703703704 * height)),
                    FullChatHorizontalPoint = new Point(RoundUp(width - 0.0185185185185185 * height), RoundUp(0.2296296296296296 * height)),
                    PartialChatlHorizontalPoint = new Point(RoundUp(width - 0.0185185185185185 * height), RoundUp(0.5851851851851852 * height))
                },
                LoadingPoints = new LoaddingPoints()
                {
                    LeftFirstPoint = new Point(RoundUp(0.1048611111111111 * height), RoundUp(0.2236111111111111 * height)),
                    RightFirstPoint = new Point(RoundUp(width - 0.2770833333333333 * height), RoundUp(0.2236111111111111 * height)),
                    Width = RoundUp(0.1694444444444444 * height),
                    Height = RoundUp(0.0243055555555556 * height),
                    Dy = RoundUp(0.1229166666666667 * height)
                },
                MmrWidth = (int)(600 * ratio),
                MmrHeight = (int)(400 * ratio),

                BanPositions = new List<Rectangle>()
                {
                    new Rectangle(RoundUp(0.2824074074074074 * height), RoundUp(0.0287037037037037 * height), RoundUp(0.05 * height), RoundUp(0.05 * height)),
                    new Rectangle(RoundUp(0.375 * height), RoundUp(0.0287037037037037 * height), RoundUp(0.05 * height), RoundUp(0.05 * height)),
                    new Rectangle(width - RoundUp(0.425 * height), RoundUp(0.0287037037037037 * height), RoundUp(0.05 * height), RoundUp(0.05 * height)),
                    new Rectangle(width - RoundUp(0.3324074074074074 * height), RoundUp(0.0287037037037037 * height), RoundUp(0.05 * height), RoundUp(0.05 * height)),
                }
            };
            
            return position;
        }

        private static int RoundUp(double num)
        {
            return (int) (num + 0.5);
        }

        public void Exit()
        {
            try
            {
                // stupid workaround for deploy build
                _toastService?.ReinitializeToast();
                Application.Current.Shutdown();
            }
            catch (Exception)
            {
                try
                {
                    CfxRuntime.Shutdown();
                }
                catch
                {
                    // ignored
                }
                Environment.Exit(0);
            }

        }

        protected override void OnClose()
        {
            try
            {
                BpHelperConfigParser.WriteConfig(App.NextConfigurationSettings);
                _hotKeyManager?.Dispose();
                _bpViewModel?.OcrUtil?.Dispose();
                AutoShowHideHelper = false;
                CfxRuntime.Shutdown();
            }
            catch (Exception)
            {
                // ignored
            }
            base.OnClose();
        }
        
        public void ShowSettings()
        {
            if (_managerVm == null)
            {
                _managerVm = _viewModelFactory.CreateViewModel<ManagerViewModel>();
                _managerVm.UploadManager = _uploadManager;
            }
            
            InitializeManagerView();

            _managerVm.ShowSettings();

            NotifyOfPropertyChange(() => CanShowSettings);
            NotifyOfPropertyChange(() => CanShowReplays);
            NotifyOfPropertyChange(() => CanShowAbout);
        }

        public void ShowReplays()
        {
            if (_managerVm == null)
            {
                _managerVm = _viewModelFactory.CreateViewModel<ManagerViewModel>();
                _managerVm.UploadManager = _uploadManager;
            }
            
            InitializeManagerView();

            _managerVm.ShowReplays();

            NotifyOfPropertyChange(() => CanShowSettings);
            NotifyOfPropertyChange(() => CanShowReplays);
            NotifyOfPropertyChange(() => CanShowAbout);
        }

        public void SetAutoStart()
        {
            try
            {
                if (ServiceNotRunning)
                {
                    var process = Process.Start(new ProcessStartInfo()
                    {
                        FileName = @"cmd.exe",
                        UseShellExecute = true,
                        Arguments =
                            "/C sc delete \"" + Const.ServiceName + "\"&sc create \"" + Const.ServiceName + "\" binPath= \"" +
                            App.AppPath +
                            "\\Service\\BpHelperMonitor.exe\"&sc config \"" + Const.ServiceName + "\" start=auto&sc description \"" + Const.ServiceName + "\" \"Launch HotsBpHelper on game start.\"&sc start \"" + Const.ServiceName + "\"",
                        Verb = "runas",
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });

                    process.WaitForExit();

                    _restApi.Analysis("action", "switchOnService", App.Language).ConfigureAwait(false);
                    Thread.Sleep(500);
                }
            }
            catch (Exception)
            {
            }


            if (IsServiceRunning)
                _toastService.ShowSuccess(L(@"ServiceOn"));
            else 
                _toastService.ShowWarning(L(@"ServiceOnFail"));
            
            InformServiceStatus();
            NotifyOfPropertyChange(() => ServiceNotRunning);
            NotifyOfPropertyChange(() => IsServiceRunning);
        }

        public void ServiceRestart()
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo()
                {
                    FileName = @"cmd.exe",
                    UseShellExecute = true,
                    Arguments =
                        "/C sc stop \"" + Const.ServiceName + "\"&sc delete \"" + Const.ServiceName + "\"&sc create \"" + Const.ServiceName + "\" binPath= \"" +
                        App.AppPath +
                        "\\Service\\BpHelperMonitor.exe\"&sc config \"" + Const.ServiceName + "\" start=auto&sc description \"" + Const.ServiceName + "\" \"Launch HotsBpHelper on game start.\"&sc start \"" + Const.ServiceName + "\"",
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                process.WaitForExit();

                _restApi.Analysis("action", "switchOnService", App.Language).ConfigureAwait(false);
                Thread.Sleep(500);
            }
            catch (Exception)
            {
            }

            if (IsServiceRunning)
                _toastService.ShowSuccess(L(@"ServiceOn"));
            else
                _toastService.ShowWarning(L(@"ServiceOnFail"));

            NotifyOfPropertyChange(() => ServiceNotRunning);
            NotifyOfPropertyChange(() => IsServiceRunning);
        }

        public void StopService()
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo()
                {
                    FileName = @"cmd.exe",
                    UseShellExecute = true,
                    Arguments = "/C sc stop \"" + Const.ServiceName + "\"&sc delete \"" + Const.ServiceName + "\"",
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                
                process.WaitForExit();

                _restApi.Analysis("action", "switchOffService", App.Language).ConfigureAwait(false);
                Thread.Sleep(500);
            }
            catch (Exception)
            {
                
            }

            if (ServiceNotRunning)
                _toastService.ShowSuccess(L(@"ServiceOff"));
            else
                _toastService.ShowWarning(L(@"ServiceOffFail"));
            
            InformServiceStatus();
            NotifyOfPropertyChange(() => ServiceNotRunning);
            NotifyOfPropertyChange(() => IsServiceRunning);
        }

        public bool IsServiceRunning
        {
            get { return GetIsServiceRuning() && IsLoaded; }
        }

        public static bool GetIsServiceRuning()
        {
                try
                {
                    using (ServiceController sc = new ServiceController(Const.ServiceName))
                    {
                        return sc.Status == ServiceControllerStatus.Running;
                    }
                }
                catch (Exception)
                {
                    return false;
                }
        }

        public bool ServiceNotRunning => !IsServiceRunning && IsLoaded;

        public bool IsStatsVisible
        {
            get { return File.Exists(Const.BattleLobbyPath) && OcrUtil.InGame && IsLoaded; }
        }

        public void ShowAbout()
        {
            if (_managerVm == null)
            {
                _managerVm = _viewModelFactory.CreateViewModel<ManagerViewModel>();
                _managerVm.UploadManager = _uploadManager;
            }
            
            InitializeManagerView();

            _managerVm.ShowAbout();

            NotifyOfPropertyChange(() => CanShowSettings);
            NotifyOfPropertyChange(() => CanShowReplays);
            NotifyOfPropertyChange(() => CanShowAbout);
        }

        private void InitializeManagerView()
        {
            var managerView = (ManagerView)_managerVm.View;

            if (managerView == null)
            {
                WindowManager.ShowWindow(_managerVm);
                managerView = (ManagerView)_managerVm.View;
                managerView.RegisterTitleHandler();
                managerView.HideRequested += OnManagerClose;
            }
            else if (managerView.Browser.Browser == null || managerView.Browser.Browser.IsDisposed)
            {
                managerView.Browser.InitializeBrowser(_managerVm.LocalFileUri);
                managerView.RegisterTitleHandler();
                managerView.ShowWindow();
            }
            else
                managerView.ShowWindow();

            _managerVm.IsClosed = false;
        }

        private void OnManagerClose(object sender, EventArgs e)
        {
            _managerVm.PopulatedTabs.Clear();
            _managerVm.IsClosed = true;
            NotifyOfPropertyChange(() => CanShowSettings);
            NotifyOfPropertyChange(() => CanShowReplays);
            NotifyOfPropertyChange(() => CanShowAbout);
        }

        public bool CanShowSettings => _managerVm == null || _managerVm.IsClosed || _managerVm.SettingsTab != SettingsTab.Configure;

        public bool CanShowAbout => _managerVm == null || _managerVm.IsClosed || _managerVm.SettingsTab != SettingsTab.About;

        public bool CanShowReplays => _managerVm == null || _managerVm.IsClosed || _managerVm.SettingsTab != SettingsTab.Replay;

        public string ShowHideHelperTip
            => _bpViewModel != null && _bpViewModel.BpScreenLoaded ? L("HideHelper") : L("ShowHelper");

        private ManagerViewModel _managerVm;
        private IEventAggregator _eventAggregator;
    }
}