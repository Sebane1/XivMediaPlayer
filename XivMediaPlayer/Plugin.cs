using Dalamud.Game.Config;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using XivMediaPlayer.GameObjects;
using XivMediaPlayer.Windows;
using MediaPlayerCore;
using MediaPlayerCore.Compositing;
using MediaPlayerCore.Twitch;
using MediaPlayerCore.YtDlp;
using XivMediaPlayer.Compositing;
using XivMediaPlayer.Localization;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Concurrent;

namespace XivMediaPlayer
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => Translate("XIV Media Player");

        // Static PluginService properties (following Dalamud SamplePlugin template)
        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;
        [PluginService] internal static IKeyState KeyState { get; private set; } = null!;

        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly ICommandManager _commandManager;
        private readonly IChatGui _chat;
        private readonly IClientState _clientState;
        private readonly IFramework _framework;
        private readonly IGameConfig _gameConfig;
        private readonly IPluginLog _pluginLog;
        private readonly ITextureProvider _textureProvider;
        private readonly IGameGui _gameGui;
        private readonly IObjectTable _objectTable;
        private readonly IPartyList _partyList;
        private readonly IGameInteropProvider _gameInterop;

        private readonly Configuration _config;
        private readonly WindowSystem _windowSystem;
        private readonly VideoWindow _videoWindow;
        private readonly SettingsWindow _settingsWindow;
        private readonly ScreenSettingsWindow _screenSettingsWindow;
        internal ScreenSettingsWindow ScreenSettingsWindow => _screenSettingsWindow;
        private WorldVideoRenderer _worldRenderer;
        internal WorldVideoRenderer WorldRenderer => _worldRenderer;
        internal string CurrentStreamer => _currentStreamer;
        internal Networking.ControllerService? ControllerService => _controllerService;
        private DepthPreviewWindow _depthPreviewWindow;

        private Networking.EmulationClient? _emulationClient;
        private Networking.ControllerService? _controllerService;

        private string _currentMediaOwnerId = string.Empty;
        private bool _isLocalDj = false;
        private DepthBufferCapture _depthCapture;
        private UILayerCapture _uiCapture;
        private Compositing.TitleTextureManager _titleTextureManager;
        private Compositing.HistoryMenuTextureManager _historyMenuTextureManager;
        private bool _isHistoryMenuOpen = false;
        private Compositing.QueueMenuTextureManager _queueMenuTextureManager;
        private bool _isQueueMenuOpen = false;

        private MediaManager _mediaManager;
        public MediaManager MediaManager => _mediaManager;
        private YtDlpManager _ytDlpManager;
        private Task _ytDlpInitTask;

        private string _lastLocationKey = "";
        private MediaGameObject? _playerObject;
        private IMediaGameObject? _lastStreamObject;
        private MediaGameObject? _tvAudioObject;
        private IMediaGameObject CurrentAudioSource => (_worldRenderer?.Transform.Enabled == true) ? _tvAudioObject! : _playerObject!;
        private Queue<string> _mediaQueue = new Queue<string>();
        private Stack<string> _mediaHistory = new Stack<string>();
        private float _preMuteVolume = 0.5f;
        private bool _isMuted = false;
        private bool _wasDragging3DSeek = false;
        private Random _shuffleRandom = new Random();
        private MediaCameraObject _playerCamera;
        private unsafe Camera* _camera;

        private string[] _streamURLs;
        private string _lastStreamURL;
        private double? _currentMediaDurationMs;
        private string _currentStreamer = "";
        private string _currentMediaTitle = "";
        private string _potentialStream;
        private bool _streamWasPlaying;
        private bool _disposed;
        private bool _bgmWasMutedByUs;
        private bool _wasHousingMenuOpen = false;
        private readonly ConcurrentQueue<Action> _frameworkActions = new();
        private DateTime? _deferredBgmRestoreTime = null;
        private bool _killRestartQueued;
        private int _translationRevision;
        private bool _translationServerErrorNotified;

        private System.Numerics.Matrix4x4? _prevViewProjMatrix = null;
        private System.Numerics.Vector3? _prevCameraPos = null;
        private System.Numerics.Vector3? _prevCameraForward = null;
        private System.Numerics.Vector3? _prevCameraRight = null;
        private System.Numerics.Vector3? _prevCameraUp = null;
        private bool _refreshQueued;

        public Networking.ServerClient ServerClient { get; private set; }
        public Configuration Config => _config;
        public YtDlpManager YtDlpManager => _ytDlpManager;
        internal int TranslationRevision => _translationRevision;

        public string Translate(string text) => Localization.Translation.Get(text);

        public void PrintChat(string text) => _chat.Print(Translate(text));

        public void PrintChatFormat(string format, params object[] args) => _chat.Print(string.Format(Translate(format), args));

        public void PrintErrorChat(string text) => _chat.PrintError(Translate(text));

        public void PrintErrorChatFormat(string format, params object[] args) => _chat.PrintError(string.Format(Translate(format), args));

        public void PrintChatWithBody(string body) => _chat.Print(Translate("[Media Player] ") + Translate(body));

        public void PrintErrorChatWithBody(string body) => _chat.PrintError(Translate("[Media Player] ") + Translate(body));

        public string FormatDmcaClipboardText(string url, string domain) =>
            string.Format(Translate("Content URL: {0}\n\nPlease contact {1} to report this content."), url, domain);

        public void ApplyUiLanguageFromConfig()
        {
            int lang = Math.Clamp(_config.UiLanguage, 0, Translator.LanguageStringsDisplay.Length - 1);
            _config.UiLanguage = lang;
            Translator.UiLanguage = (LanguageEnum)lang;
            Translator.ServerUrl = _config.TranslationServerUrl;
            Translator.ClearLastError();
            _translationServerErrorNotified = false;
            Interlocked.Increment(ref _translationRevision);
            _titleTextureManager?.InvalidateLoadingCache();
            UpdateMediaCommandHelp();

            if (lang != (int)LanguageEnum.English)
            {
                _ = Task.Run(async () => await Translator.ProbeServerAsync());
            }
        }

        private string GetMediaCommandHelpText()
        {
            return Translate("Media Player commands.\n") +
                Translate(" /media — Open settings\n") +
                Translate(" /media twitch <url> — Tune into a Twitch stream\n") +
                Translate(" /media rtmp <url> — Tune into an RTMP stream\n") +
                Translate(" /media play <url> — Play a media URL\n") +
                Translate(" /media stop — Stop current stream\n") +
                Translate(" /media video — Toggle video window\n") +
                Translate(" /media emulate <ip> <session> — Connect to emulation server");
        }

        private string GetMediaHelpChatText()
        {
            return Translate("Media Player commands.\n") +
                Translate(" /media — Open settings\n") +
                Translate(" /media twitch <url> — Tune into a Twitch stream\n") +
                Translate(" /media rtmp <url> — Tune into an RTMP stream\n") +
                Translate(" /media play <url> — Play a media URL\n") +
                Translate(" /media stop — Stop current stream\n") +
                Translate(" /media video — Toggle video window\n") +
                Translate(" /media emulate <ip> <session> — Connect to emulation server\n") +
                Translate(" /media screen [place|move|rotate|scale|reset|save] — 3D screen\n") +
                Translate(" /media listen — Tune into a shared stream\n") +
                Translate(" /media ytdlp-update — Update yt-dlp\n") +
                Translate(" /media help — Show this help");
        }

        private string GetScreenCommandHelpText()
        {
            return Translate("[Media Player] Screen commands:\n") +
                Translate(" /media screen place — Place screen at your look-at point\n") +
                Translate(" /media screen move <x> <y> <z> — Adjust position\n") +
                Translate(" /media screen rotate <yaw> [pitch] — Set rotation\n") +
                Translate(" /media screen scale <w> <h> — Set size (world units)\n") +
                Translate(" /media screen reset — Return to overlay mode\n") +
                Translate(" /media screen save — Save current placement");
        }

        private void UpdateMediaCommandHelp()
        {
            _commandManager.RemoveHandler("/media");
            _commandManager.AddHandler("/media", new Dalamud.Game.Command.CommandInfo(OnMediaCommand)
            {
                HelpMessage = GetMediaCommandHelpText(),
                ShowInHelp = true,
            });
        }

        private void InitializeLocalization()
        {
            string cachePath = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "translation-cache.json");
            Translator.CacheLocation = cachePath;
            Translator.LoadCache(cachePath);
            ApplyUiLanguageFromConfig();
            Translator.OnTranslationEvent += (_, _) =>
            {
                Interlocked.Increment(ref _translationRevision);
                EnqueueFrameworkAction(RefreshLocalizedOverlays);
            };
            Translator.OnError += (_, ex) =>
            {
                _pluginLog.Warning($"[Localization] {ex.Message}");
                if (_config.UiLanguage != (int)LanguageEnum.English && !_translationServerErrorNotified)
                {
                    _translationServerErrorNotified = true;
                    EnqueueFrameworkAction(() =>
                        PrintErrorChatFormat("[Media Player] Translation server unreachable ({0}). UI will stay in English until it responds.", Translator.ServerUrlDisplay));
                }
            };
        }
        public bool IsHousingMenuOpen => _wasHousingMenuOpen;
        public Dalamud.Plugin.Services.IObjectTable ObjectTable => _objectTable;
        public Dalamud.Plugin.Services.IPluginLog PluginLog => _pluginLog;
        public Dalamud.Plugin.Services.IChatGui Chat => _chat;
        public string LastStreamURL => _lastStreamURL;

        private bool _isDisposing;

        private DateTime _lastClipboardCheck = DateTime.MinValue;
        private DateTime _lastServerSyncPush = DateTime.MinValue;
        private DateTime _lastHistoryUpdate = DateTime.MinValue;
        private DateTime? _deferredTerritoryChangeTime = null;
        private DateTime _lastServerSyncFetch = DateTime.MinValue;
        private string _lastGridLocationKey = string.Empty;
        private DateTime _lastGridChangeTime = DateTime.MinValue;
        private long _serverTimeOffsetMs = 0;
        private bool _hasFetchedServerTime = false;
        private int _cachedRealPlayerCount = 0;
        private System.Numerics.Vector3? _cachedLocalPlayerPosition = null;
        private uint _cachedLocalPlayerWorldId = 0;

        private int _lastCookieHash;
        private bool _hasBeenInitialized;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private IntPtr _mainWindowHandle;
        private IDisposable _cefBrowserHandle;
        private Stopwatch _streamSetCooldown = new Stopwatch();
        private Stopwatch _screensaverTimer = new Stopwatch();

        private string _statusMessage = string.Empty;

        public unsafe bool IsVisitingIslandSanctuary()
        {
            if (_clientState.TerritoryType != 1055) return false;
            var mji = FFXIVClientStructs.FFXIV.Client.Game.MJI.MJIManager.Instance();
            return mji != null && !mji->IsPlayerInSanctuary;
        }

        // Current room TV state
        public Networking.Models.TvPlacement? CurrentTvPlacement { get; internal set; }
        private List<Networking.Models.TvPlacement> _nearbyTvs = new();

        // Input tracking
        private bool _wasLeftMousePressed = false;
        private bool _clickStartedOnTv = false;
        private DependencyManager _dependencyManager;

        public Plugin(
          IDalamudPluginInterface pluginInterface,
          ICommandManager commandManager,
          IChatGui chat,
          IClientState clientState,
          IFramework framework,
          IGameConfig gameConfig,
          IPluginLog pluginLog,
          ITextureProvider textureProvider,
          IGameGui gameGui,
          IObjectTable objectTable,
          IPartyList partyList,
          IGameInteropProvider gameInterop,
          Dalamud.Plugin.Services.IAddonLifecycle addonLifecycle)
        {
            _pluginInterface = pluginInterface;
            _commandManager = commandManager;
            _chat = chat;
            
            _mainWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
            
            _clientState = clientState;
            _framework = framework;
            _gameConfig = gameConfig;
            _pluginLog = pluginLog;
            _textureProvider = textureProvider;
            _gameGui = gameGui;
            _objectTable = objectTable;
            _partyList = partyList;
            _gameInterop = gameInterop;

            // Initialize dependency manager to download large binaries (libvlc, cef)
            string configDir = _pluginInterface.ConfigDirectory.FullName;
            string pluginDir = System.IO.Path.GetDirectoryName(_pluginInterface.AssemblyLocation.FullName) ?? "";
            string version = this.GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            _dependencyManager = new DependencyManager(configDir, pluginDir, version, _pluginLog);

            // Bypass Dalamud's assembly resolver for CefSharp natively (just in case)
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => { return null; };

            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
            {
                if (assemblyName.Name != null && assemblyName.Name.StartsWith("CefSharp"))
                {
                    string pluginDir = System.IO.Path.GetDirectoryName(_pluginInterface.AssemblyLocation.FullName) ?? "";
                    string cefPath = System.IO.Path.Combine(pluginDir, "cef", assemblyName.Name + ".dll");
                    if (System.IO.File.Exists(cefPath))
                    {
                        return context.LoadFromAssemblyPath(cefPath);
                    }
                }
                return null;
            };

            // Load configuration
            _config = (Configuration)_pluginInterface.GetPluginConfig()
                 ?? new Configuration();
            _config.Initialize(_pluginInterface);
            _config.Migrate();
            InitializeLocalization();

            // Initialize yt-dlp manager
            _ytDlpManager = new YtDlpManager(pluginDir, _config.PreferredQuality) { EnableSabrProxy = _config.EnableSabrProxy };
            _ytDlpManager.OnStatusUpdate += (s, msg) =>
            {
                _pluginLog.Info("[yt-dlp] " + msg);
                if (IsYtDlpLoadingMessage(msg) && !_playbackHasRenderedFrames)
                {
                    _mediaLoadingMessage = msg;
                }
            };
            _ytDlpManager.OnError += (s, ex) =>
            {
                _pluginLog.Warning(ex, "[yt-dlp] " + ex.Message);
                if (MediaPlayerCore.YtDlp.YtDlpManager.IsYouTubeSessionError(ex.Message))
                {
                    EnqueueFrameworkAction(() => PrintErrorChat(
                        "[Media Player] YouTube rejected the session. Export fresh cookies via VRCVideoCacher (private/incognito window), restart the plugin, and retry."));
                }
            };

            // Auto-download if missing, then always self-update
            _ytDlpInitTask = Task.Run(async () => await _ytDlpManager.EnsureAvailableAsync());

            // Initialize world-space video renderer
            _worldRenderer = new WorldVideoRenderer(_config.WorldScreen, _gameGui);

            ServerClient = new Networking.ServerClient(_config.ServerUrl, _pluginLog);
            _config.OnConfigurationChanged += (s, e) =>
            {
                // Only recreate ServerClient if the ServerUrl actually changed!
                // Otherwise we constantly dispose the HttpClient while requests are in flight!
                if (ServerClient.BaseUrl != _config.ServerUrl)
                {
                    ServerClient?.Dispose();
                    ServerClient = new Networking.ServerClient(_config.ServerUrl, _pluginLog);
                }
            };

            _uiCapture = new UILayerCapture(addonLifecycle);
            _uiCapture.Initialize();
            _titleTextureManager = new Compositing.TitleTextureManager(_textureProvider);
            _historyMenuTextureManager = new Compositing.HistoryMenuTextureManager(_textureProvider);
            _queueMenuTextureManager = new Compositing.QueueMenuTextureManager(_textureProvider);

            // Create windows
            _windowSystem = new WindowSystem("XivMediaPlayer");
            _videoWindow = new VideoWindow(this, _pluginInterface, _textureProvider, _pluginLog);
            _settingsWindow = new SettingsWindow(this, FixWindowsVolume);
            _screenSettingsWindow = new ScreenSettingsWindow(
              this,
              _gameGui,
              _config.WorldScreen,
              _worldRenderer,
              onSave: () =>
              {
                  _config.Save();
                  SaveScreenForCurrentLocation();
              },
              onPlaceAtCamera: () => PlaceScreenAtCamera()
            );

            _depthCapture = new DepthBufferCapture();
            _depthCapture.Initialize();

            _depthPreviewWindow = new DepthPreviewWindow(_textureProvider, _pluginLog);
            _depthPreviewWindow.Capture = _depthCapture;
            _depthPreviewWindow.UICapture = _uiCapture;
            _depthPreviewWindow.Config = _config;

            _windowSystem.AddWindow(_videoWindow);
            _windowSystem.AddWindow(_settingsWindow);
            _windowSystem.AddWindow(_screenSettingsWindow);
            _windowSystem.AddWindow(_depthPreviewWindow);

            // Register draw + config UI
            _pluginInterface.UiBuilder.Draw += OnDraw;
            _pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
            _pluginInterface.UiBuilder.DisableUserUiHide = true;
            _pluginInterface.UiBuilder.DisableGposeUiHide = true;
            _pluginInterface.UiBuilder.DisableCutsceneUiHide = true;
            _pluginInterface.UiBuilder.DisableAutomaticUiHide = true;

            // Register commands (help text refreshed when language changes)
            UpdateMediaCommandHelp();

            // Hook events
            _framework.Update += OnFrameworkUpdate;
            _clientState.TerritoryChanged += OnTerritoryChanged;
            _clientState.Login += OnLogin;
            _clientState.Logout += OnLogout;
            _videoWindow.WindowResized += OnVideoWindowResized;

            // Chat listener for twitch detection
            _chat.ChatMessage += OnChatMessage;

            // Run initial restore (deferred to allow housing data to load if logging in directly to a house)
            Task.Run(async () =>
            {
                await Task.Delay(3000);
                EnqueueFrameworkAction(() =>
                {
                    RestoreScreenForCurrentLocation();
                    RestoreMediaForCurrentLocation();
                });
            });

            // Start proxy server for stream routing
            MediaPlayerCore.StreamProxy.Instance.Start();
        }

        #region Framework / Initialization

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            if (_disposed) return;

            while (_frameworkActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _pluginLog.Warning(e, "[Media Player] Framework action failed.");
                }
            }

            if (!_clientState.IsLoggedIn) return;

            var localPlayerObj = GetLocalPlayer();
            if (localPlayerObj != null) {
                _playerObject?.Update(localPlayerObj);
            }
            
            _playerCamera?.Update();
            
            if (_worldRenderer?.Transform.Enabled == true && _tvAudioObject != null) {
                _tvAudioObject.SetPosition(_worldRenderer.Transform.Position);
            }

            // Cache local player data for background threads to avoid "Not on main thread!" exceptions
            var localPlayer = _objectTable[0] as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
            _cachedLocalPlayerPosition = localPlayer?.Position;
            _cachedLocalPlayerWorldId = localPlayer?.CurrentWorld.RowId ?? 0;

            // Cache real player count safely on the main thread for background sync tasks
            int realPlayerCount = 0;
            foreach (var obj in _objectTable)
            {
                if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                {
                    string name = obj.Name.TextValue.ToLowerInvariant();
                    if (!name.Contains("reborn") && !name.Contains("cnpc"))
                    {
                        realPlayerCount++;
                    }
                }
            }
            _cachedRealPlayerCount = realPlayerCount;

            if (_deferredTerritoryChangeTime.HasValue && DateTime.UtcNow >= _deferredTerritoryChangeTime.Value)
            {
                // Wait until local player and housing manager are fully loaded
                bool isHousingLoaded = true;
                if (_clientState.TerritoryType != 0)
                {
                    unsafe
                    {
                        var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
                        if (housingMgr != null && housingMgr->IsInside())
                        {
                            if (housingMgr->GetCurrentIndoorHouseId().Id == 0)
                            {
                                isHousingLoaded = false;
                            }
                        }
                    }

                    if (_cachedLocalPlayerWorldId == 0)
                    {
                        isHousingLoaded = false;
                    }
                }

                if (isHousingLoaded)
                {
                    _deferredTerritoryChangeTime = null;
                    RestoreScreenForCurrentLocation();
                    RestoreMediaForCurrentLocation();
                    _ = FetchServerDataForCurrentLocationAsync();
                }
                else
                {
                    // Delay another 0.5s to wait for loading to finish
                    _deferredTerritoryChangeTime = DateTime.UtcNow.AddSeconds(0.5);
                }
            }

            if (_deferredBgmRestoreTime.HasValue && DateTime.UtcNow >= _deferredBgmRestoreTime.Value)
            {
                unsafe
                {
                    if (!Conditions.Instance()->BetweenAreas)
                    {
                        _deferredBgmRestoreTime = null;
                        RestoreBgm();
                    }
                }
            }

            if (!_hasBeenInitialized && _clientState.IsLoggedIn)
            {
                if (!_dependencyManager.IsReady)
                {
                    if (!_dependencyManager.IsDownloading && !_dependencyManager.HasError)
                    {
                        _ = _dependencyManager.DownloadDependenciesAsync();
                    }
                    return;
                }

                try
                {
                    InitializeMediaManager();
                    // Validate initialization success
                    _hasBeenInitialized = _playerObject != null;
                    if (_hasBeenInitialized)
                    {
                        // Restore saved screen placement for current location on plugin load
                        RestoreScreenForCurrentLocation();

                        // Automatically fetch TV placement and media state from the server if already in a house
                        _ = FetchServerDataForCurrentLocationAsync();
                    }
                }
                catch (Exception e)
                {
                    _pluginLog.Error(e, "Failed to initialize media manager");
                }
            }

            if (_hasBeenInitialized)
            {
                TryEnsurePlaybackStarted();
            }

            // Auto-open/close screen placement menu based on Housing Menu state
            unsafe
            {
                var housingGoods = _gameGui.GetAddonByName("HousingGoods", 1);
                var mjiFurnishing = _gameGui.GetAddonByName("MJIFurnishing", 1);
                var mjiHousingGoods = _gameGui.GetAddonByName("MJIHousingGoods", 1);
                var mjiFurnishingGlamour = _gameGui.GetAddonByName("MJIFurnishingGlamour", 1);
                
                bool isHousingMenuOpen = (housingGoods != IntPtr.Zero) || (mjiFurnishing != IntPtr.Zero) || (mjiHousingGoods != IntPtr.Zero) || (mjiFurnishingGlamour != IntPtr.Zero);

                if (isHousingMenuOpen && !_wasHousingMenuOpen)
                {
                    _wasHousingMenuOpen = isHousingMenuOpen;
                    _screenSettingsWindow.IsOpen = true;
                    _screenSettingsWindow.SyncFromTransform();

                    if (CurrentTvPlacement != null && CurrentTvPlacement.OwnerId != _config.OwnerId && !string.IsNullOrEmpty(LocationKey))
                    {
                        // Only re-register if the server TV belongs to someone else.
                        // If it's ours already, the server coordinates are authoritative and we don't
                        // want to overwrite them with stale local coordinates.
                        CurrentTvPlacement.OwnerId = _config.OwnerId;
                        _pluginLog.Info($"[Social] Automatically restoring TV ownership for {LocationKey} because housing menu was opened.");
                        _screenSettingsWindow.RegisterTvAsync(LocationKey);
                    }
                }
                else if (!isHousingMenuOpen && _wasHousingMenuOpen)
                {
                    _wasHousingMenuOpen = isHousingMenuOpen;
                    _screenSettingsWindow.IsOpen = false;

                    // Auto-save and register TV when closing the menu
                    if (!string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("house_"))
                    {
                        _screenSettingsWindow.RegisterTvAsync(LocationKey);
                    }
                }
                else
                {
                    _wasHousingMenuOpen = isHousingMenuOpen;
                }
            }

            // Track grid location changes
            string currentLocKey = LocationKey;
            if (_lastGridLocationKey != currentLocKey)
            {
                if ((DateTime.UtcNow - _lastGridChangeTime).TotalSeconds >= 3)
                {
                    _lastGridLocationKey = currentLocKey;
                    _lastGridChangeTime = DateTime.UtcNow;

                    // If it changed purely due to grid crossing, not territory
                    if (!string.IsNullOrEmpty(currentLocKey) && currentLocKey.StartsWith("zone_"))
                    {
                        bool wasPublicTv = _worldRenderer?.Transform.Enabled == true;

                        RestoreScreenForCurrentLocation();

                        bool isPublicTv = _worldRenderer?.Transform.Enabled == true;

                        // If we are leaving a TV, entering a TV, or moving between TVs, stop the old stream to prevent double audio
                        if (wasPublicTv || isPublicTv) 
                        {
                            _mediaManager?.StopStream();
                        }

                        RestoreMediaForCurrentLocation();
                        _ = FetchServerDataForCurrentLocationAsync();
                    }
                }
            }

            // Sync Polling Loop
            bool isHouse = !string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("house_");
            bool isZone = !string.IsNullOrEmpty(LocationKey) && LocationKey.StartsWith("zone_");

            if (isHouse || (isZone && _config.EnableOutdoorPublicScreens))
            {
                bool isMediaOwner = _isLocalDj;

                // Only push if actively playing or loading.
                // Paused media should continue pushing so clients don't think the DJ crashed.
                if (isMediaOwner && ((_mediaManager?.ActiveStream != null) || !string.IsNullOrEmpty(_lastStreamURL)))
                {
                    if ((DateTime.UtcNow - _lastServerSyncPush).TotalSeconds >= 5)
                    {
                        _lastServerSyncPush = DateTime.UtcNow;
                        _pluginLog.Information($"[Social] Executing PushMediaToServerAsync. ActiveStream Time: {_mediaManager?.ActiveStream?.Time ?? 0}");
                        _ = PushMediaToServerAsync(isBackgroundSync: true);
                    }
                }
                else if ((DateTime.UtcNow - _lastServerSyncPush).TotalSeconds >= 5)
                {
                    _lastServerSyncPush = DateTime.UtcNow;
                    _pluginLog.Information($"[Social] Skipping Push. isMediaOwner={isMediaOwner} ({_currentMediaOwnerId} vs {_config.OwnerId}), ActiveStream={_mediaManager?.ActiveStream != null}");
                }

                // Polling interval.
                // Facilitates DJ handoff on media change.
                if (!_hasFetchedServerTime)
                {
                    _hasFetchedServerTime = true;
                    _ = FetchServerTimeAsync();
                }

                if ((DateTime.UtcNow - _lastServerSyncFetch).TotalSeconds >= 10)
                {
                    _lastServerSyncFetch = DateTime.UtcNow;
                    _ = FetchServerDataForCurrentLocationAsync();
                }
            }

            if ((DateTime.UtcNow - _lastHistoryUpdate).TotalSeconds >= 10)
            {
                _lastHistoryUpdate = DateTime.UtcNow;
                if (_mediaManager?.ActiveStream != null && !_isIntentionallyPaused) {
                    UpdateWatchHistory();
                }
            }

            // Clipboard cookie watcher. Check every 5 seconds.
            CheckClipboardForCookies();
        }

        private void CheckClipboardForCookies()
        {
            if ((DateTime.UtcNow - _lastClipboardCheck).TotalSeconds < 5) return;
            _lastClipboardCheck = DateTime.UtcNow;

            try
            {
                string? clipText = ImGui.GetClipboardText();
                if (string.IsNullOrEmpty(clipText)) return;

                int hash = clipText.GetHashCode();
                if (hash == _lastCookieHash) return;

                if (YtDlpManager.IsNetscapeCookieFormat(clipText))
                {
                    _lastCookieHash = hash;
                    if (_ytDlpManager.SaveCookiesFromText(clipText))
                    {
                        _pluginLog.Info("[yt-dlp] Auto-detected YouTube cookies from clipboard.");
                    }
                }
            }
            catch
            {
                // Clipboard access can throw. Silently ignore.
            }
        }

        private bool IsPlayerAlone()
        {
            try
            {
                int playerCount = 0;
                foreach (var obj in _objectTable)
                {
                    if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                    {
                        playerCount++;
                        if (playerCount > 1) return false;
                    }
                }
                return true; // only the local player
            }
            catch (Exception ex)
            {
                _pluginLog.Error(ex, "Failed to check if player is alone");
                return false; // assume not alone to be safe
            }
        }

        private unsafe void InitializeMediaManager()
        {
            var localPlayer = GetLocalPlayer();
            if (localPlayer == null)
            {
                _pluginLog.Warning("[Media Player] LocalPlayer is null, cannot initialize media manager.");
                _hasBeenInitialized = false; // Allow retry next frame
                return;
            }

            _pluginLog.Info("[Media Player] Initializing media manager...");
            _playerObject = new MediaGameObject(localPlayer.Name.TextValue, localPlayer.Position);
            _tvAudioObject = new MediaGameObject("TV", System.Numerics.Vector3.Zero);
            _camera = CameraManager.Instance()->GetActiveCamera();
            _playerCamera = new MediaCameraObject(_camera);
            _mediaManager = new MediaManager(_playerObject, _playerCamera, _dependencyManager.DependenciesDir);
            _mediaManager.SeekTimeClamper = ClampSeekTimeMs;
            _mediaManager.IsSabrDownloadActive = path => _ytDlpManager?.IsSabrDownloadActiveForPath(path) == true;
            _mediaManager.OnErrorReceived += OnMediaError;
            _mediaManager.OnNewMediaTriggered += _mediaManager_OnNewMediaTriggered;
            _mediaManager.OnPlaybackFinished += _mediaManager_OnPlaybackFinished;
            _mediaManager.LiveStreamVolume = _config.LivestreamVolume;
            _videoWindow.MediaManager = _mediaManager;
            _pluginLog.Info("[Media Player] Media manager initialized successfully.");
        }

        private Dalamud.Game.ClientState.Objects.Types.IGameObject GetLocalPlayer()
        {
            try
            {
                // ObjectTable[0] is always the local player in Dalamud
                var player = _objectTable[0];
                if (player != null)
                {
                    // _pluginLog.Debug($"[Media Player] Found LocalPlayer from ObjectTable: {player.Name}");
                }
                return player;
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to get LocalPlayer from ObjectTable");
            }
            return null;
        }

        #endregion

        #region Commands

        private void OnMediaCommand(string command, string args)
        {
            if (_disposed) return;

            string[] splitArgs = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (splitArgs.Length == 0)
            {
                _settingsWindow.IsOpen = true;
                return;
            }

            switch (splitArgs[0].ToLower())
            {
                case "depth":
                    _depthPreviewWindow.IsOpen = !_depthPreviewWindow.IsOpen;
                    if (_depthPreviewWindow.IsOpen)
                        PrintChat("[Media Player] Depth preview opened.");
                    else
                        PrintChat("[Media Player] Depth preview closed.");
                    break;
                case "twitch":
                    if (splitArgs.Length > 1 && splitArgs[1].Contains("twitch.tv"))
                    {
                        if (_playerObject != null)
                        {
                            _lastStreamObject = CurrentAudioSource;
                            PlayRouted(splitArgs[1], CurrentAudioSource, 0);
                        }
                    }
                    else
                    {
                        // Open twitch chat for current streamer
                        if (!string.IsNullOrEmpty(_currentStreamer))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo()
                                {
                                    FileName = @"https://www.twitch.tv/popout/" + _currentStreamer + @"/chat?popout=",
                                    UseShellExecute = true,
                                    Verb = "OPEN"
                                });
                            }
                            catch (Exception e)
                            {
                                _pluginLog.Warning(e, e.Message);
                            }
                        }
                        else
                        {
                            PrintErrorChat("[Media Player] No active stream. Use: /media twitch <url>");
                        }
                    }
                    break;

                case "rtmp":
                    if (splitArgs.Length > 1 && splitArgs[1].Contains("rtmp"))
                    {
                        if (_playerObject != null)
                        {
                            _lastStreamObject = CurrentAudioSource;
                            TuneIntoStream(splitArgs[1], CurrentAudioSource, 0);
                        }
                    }
                    break;

                case "play":
                    if (splitArgs.Length > 1)
                    {
                        string url = splitArgs[1];
                        if (_playerObject == null)
                        {
                            PrintErrorChat("[Media Player] Not initialized yet. Are you logged in?");
                            _pluginLog.Warning("[Media Player] _playerObject is null. _hasBeenInitialized=" + _hasBeenInitialized);
                            break;
                        }
                        _lastStreamObject = CurrentAudioSource;
                        /* if (url.Contains("twitch.tv")) {
                           TuneIntoStream(url, CurrentAudioSource, false);
                         } else if (url.StartsWith("rtmp")) {
                           TuneIntoStream(url, CurrentAudioSource, true);
                         } else */
                        if (YtDlpManager.IsUrlSupported(url))
                        {
                            // Invoke yt-dlp resolution
                            PrintVerbose("[Media Player] Resolving URL via yt-dlp...");
                            PlayRouted(url, CurrentAudioSource);
                        }
                        else
                        {
                            // Fallback: direct URL to VLC
                            TuneIntoStream(url, CurrentAudioSource, 0);
                        }
                    }
                    else
                    {
                        PrintErrorChat("[Media Player] Usage: /media play <url>");
                    }
                    break;

                case "ytdlp-update":
                    if (_ytDlpManager.IsAvailable())
                    {
                        PrintVerbose("[Media Player] Updating yt-dlp...");
                        Task.Run(async () =>
                        {
                            bool success = await _ytDlpManager.SelfUpdate();
                            EnqueueFrameworkAction(() =>
                            {
                                if (success)
                                    PrintChat("[Media Player] yt-dlp updated.");
                                else
                                    PrintChat("[Media Player] yt-dlp update failed.");
                            });
                        });
                    }
                    else
                    {
                        PrintErrorChat("[Media Player] yt-dlp not found. Set the path in /media settings.");
                    }
                    break;

                case "stop":
                    _mediaManager?.StopStream();
                    RestoreBgm();
                    ResetStreamValues();
                    PrintChat("[Media Player] Stream stopped.");
                    break;

                case "fixaudio":
                    RestoreBgm();
                    FixWindowsVolume();
                    PrintChat("[Media Player] Game audio restored.");
                    break;

                case "video":
                    _videoWindow.IsOpen = !_videoWindow.IsOpen;
                    break;

                case "emulate":
                    if (splitArgs.Length >= 3)
                    {
                        string ip = splitArgs[1];
                        string session = splitArgs[2];
                        _ = ConnectEmulationAsync(ip, session);
                    }
                    else
                    {
                        PrintErrorChat("[Media Player] Usage: /media emulate <ip> <session>");
                    }
                    break;

                case "listen":
                    if (!string.IsNullOrEmpty(_potentialStream) && _playerObject != null)
                    {
                        PlayRouted(_potentialStream, CurrentAudioSource, 0);
                    }
                    break;

                case "tv":
                case "screen":
                    string locKey = LocationKey;
                    bool isOutdoors = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("zone_");
                    bool isIsland = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("island_");
                    bool hasPrivileges = isOutdoors || isIsland || IsHousingMenuOpen;
                    
                    if (!hasPrivileges)
                    {
                        PrintErrorChat("[Media Player] The screen settings menu can only be accessed while the 'Edit Furnishings' housing menu is open or you are outdoors.");
                        break;
                    }

                    if (splitArgs.Length < 2)
                    {
                        // No subcommand: toggle the settings window
                        _screenSettingsWindow.Toggle();
                    }
                    else
                    {
                        HandleScreenCommand(splitArgs);
                    }
                    break;

                case "help":
                    PrintChat(GetMediaHelpChatText());
                    break;

                default:
                    _settingsWindow.Toggle();
                    break;
            }
        }

        private async Task ConnectEmulationAsync(string ip, string session)
        {
            PrintChatFormat("[Media Player] Connecting to emulation server at {0}...", ip);
            string rtsp = await Networking.EmulationClient.GetRtspUrlAsync(ip, session);
            if (string.IsNullOrEmpty(rtsp))
            {
                PrintErrorChat("[Media Player] Failed to retrieve stream info from emulation server.");
                return;
            }

            _emulationClient?.Dispose();
            _emulationClient = new Networking.EmulationClient(ip, session);
            _controllerService?.Dispose();
            _controllerService = new Networking.ControllerService(ip, session);
            _controllerService.Start();

            // Start FFmpeg backend instead of VLC for extreme low latency
            _mediaManager?.PlayFFmpegStream(rtsp);
            _lastStreamURL = rtsp;
            _ = PushMediaToServerAsync(isBackgroundSync: false);
        }

        internal void SendEmulationMouseState(float normX, float normY, float scroll, bool lmb, bool rmb)
        {
            if (_emulationClient != null)
            {
                byte xByte = (byte)(Math.Clamp(normX, 0f, 1f) * 255f);
                byte yByte = (byte)(Math.Clamp(1f - normY, 0f, 1f) * 255f);
                _emulationClient.SendMouseState(xByte, yByte, scroll, lmb, rmb);
            }
        }

        #endregion

        #region Stream Management

        private void TuneIntoStream(string url, MediaPlayerCore.IMediaGameObject audioGameObject, int startTimeMs = 0, Dictionary<string, string>? httpHeaders = null, bool isAutoSync = false)
        {
            if (_disposed) return;

            // Auto-detect Emulation Server URLs (e.g. rtsp://10.0.0.30:8554/live/screen_43815929)
            // Replace any backslashes with forward slashes (FFXIV chat/clipboard can mangle them)
            string normalizedUrl = url.Trim().Replace("\\", "/");
            if (normalizedUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) && normalizedUrl.Contains("/screen_"))
            {
                try
                {
                    var uri = new Uri(normalizedUrl);
                    string ip = uri.Host;
                    string session = uri.Segments.Last().Replace("screen_", "");

                    _emulationClient?.Dispose();
                    _emulationClient = new Networking.EmulationClient(ip, session);
                    _controllerService?.Dispose();
                    _controllerService = new Networking.ControllerService(ip, session);
                    _controllerService.Start();

                    // Start FFmpeg backend instead of VLC for extreme low latency video and audio
                    _mediaManager?.PlayFFmpegStream(normalizedUrl, audioGameObject, true);

                    _lastStreamURL = normalizedUrl;
                    _currentStreamer = "Emulation";
                    _streamURLs = new string[] { normalizedUrl };
                    _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                    if (!isAutoSync)
                    {
                        _ = PushMediaToServerAsync(isBackgroundSync: false);
                    }
                    _streamWasPlaying = true;

                    try { MuteBgm(); } catch { }
                    return;
                }
                catch { }
            }

            UpdateWatchHistory();

            url = CleanUrl(url);

            if (!isAutoSync && CurrentTvPlacement?.IsLocked == true && CurrentTvPlacement?.OwnerId != _config.OwnerId && !IsHousingMenuOpen)
            {
                PrintErrorChat("[Media Player] Cannot play stream: The TV in this room is locked by its owner.");
                return;
            }

            _streamURLs = new string[] { url };
            _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;
            if (_streamURLs.Length > 0)
            {
                _playbackHasRenderedFrames = false;
                ResetPlaybackEnsureState();
                string playUrl = ((int)_videoWindow.FeedType < _streamURLs.Length) ? _streamURLs[(int)_videoWindow.FeedType] : _streamURLs[0];
                if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !playUrl.Contains("127.0.0.1"))
                {
                    playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterDirectMediaSession(playUrl, httpHeaders);
                }
                _mediaManager.PlayStream(audioGameObject, playUrl, _config.SpatialAudioEnabled, startTimeMs, httpHeaders);
                _lastStreamURL = url;
                _currentStreamer = "Stream";
                PrintVerbose(@"[Media Player] Playing stream!" +
                  "\r\nUse \"/media video\" to toggle the video feed." +
                  "\r\nUse \"/media stop\" to stop the stream.");
            }

            if (!isAutoSync)
            {
                _ = PushMediaToServerAsync(isBackgroundSync: false);
            }
            _streamWasPlaying = true;
            try
            {
                MuteBgm();
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, e.Message);
            }
            _streamSetCooldown.Stop();
            _streamSetCooldown.Reset();
            _streamSetCooldown.Start();
        }

        private bool _isResolvingMedia = false;
        private string _mediaLoadingMessage = "";
        private bool _playbackHasRenderedFrames = false;
        private int _playbackEnsureAttempts = 0;
        private DateTime _lastPlaybackEnsureUtc = DateTime.MinValue;
        private string _playbackEnsureUrl = "";
        private Guid _currentResolutionId = Guid.Empty;
        private bool _liveProxyFallbackPending = false;
        private bool _lastStreamIsLive = false;

        private string? _sabrSeekCachePath;
        private long _sabrSeekCacheTick;
        private long _sabrSeekCacheDurationMs;
        private long _sabrSeekCacheMaxSeekMs;
        private const long SabrSeekCacheTtlMs = 500;

        private void InvalidateSabrSeekCache()
        {
            _sabrSeekCachePath = null;
            _sabrSeekCacheTick = 0;
        }

        private void EnsureSabrSeekCache(string mediaPath, long metadataLength, long vlcLength, long fileTimeMs)
        {
            long now = Environment.TickCount64;
            if (_sabrSeekCachePath != null
                && string.Equals(_sabrSeekCachePath, mediaPath, StringComparison.OrdinalIgnoreCase)
                && now - _sabrSeekCacheTick < SabrSeekCacheTtlMs)
            {
                return;
            }

            long muxedMs = MatroskaMuxFrontier.ProbeDurationMs(mediaPath);
            bool fullyBuffered = _ytDlpManager?.IsSabrFileFullyBuffered(mediaPath, metadataLength, muxedMs) == true;

            long durationMs;
            if (!fullyBuffered && metadataLength > 0)
            {
                durationMs = metadataLength;
            }
            else
            {
                durationMs = Math.Max(vlcLength, metadataLength);
                if (durationMs > metadataLength && metadataLength > 0)
                {
                    _currentMediaDurationMs = durationMs;
                }
            }

            _sabrSeekCachePath = mediaPath;
            _sabrSeekCacheTick = now;
            _sabrSeekCacheDurationMs = durationMs;
            _sabrSeekCacheMaxSeekMs = ComputeSabrMaxSeekMs(
                durationMs, muxedMs, fullyBuffered, vlcLength, fileTimeMs);
        }

        private static long ComputeSabrMaxSeekMs(
            long fullDuration,
            long muxedMs,
            bool fullyBuffered,
            long vlcLength,
            long fileTimeMs)
        {
            const long safetyMs = 3000;

            if (fullyBuffered)
            {
                return fullDuration > 0 ? fullDuration : Math.Max(0, vlcLength);
            }

            if (muxedMs > safetyMs)
            {
                long cap = fullDuration > 0 ? fullDuration : muxedMs;
                return Math.Min(cap, muxedMs - safetyMs);
            }

            if (fullDuration > 0 && vlcLength >= fullDuration - 2000)
            {
                return Math.Min(fullDuration, Math.Max(0, fileTimeMs));
            }

            if (vlcLength > safetyMs)
            {
                long cap = fullDuration > 0 ? fullDuration : vlcLength;
                return Math.Min(cap, vlcLength - safetyMs);
            }

            long fallbackCap = fullDuration > 0 ? fullDuration : long.MaxValue;
            return Math.Min(fallbackCap, Math.Max(0, fileTimeMs));
        }

        private void PlayResolvedLiveStream(
            IMediaGameObject audioGameObject,
            string resolvedStreamUrl,
            Dictionary<string, string> resolvedHeaders,
            bool isYouTube,
            int finalStartTimeMs,
            string? resolvedSlaveAudioUrl)
        {
            string playUrl = resolvedStreamUrl;
            bool useProxy = _liveProxyFallbackPending;

            if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !playUrl.Contains("127.0.0.1"))
            {
                if (!useProxy && isYouTube && YtDlpManager.IsHlsStreamUrl(resolvedStreamUrl))
                {
                    _pluginLog.Information("[Live] Direct HLS from CDN (VLC forwards cookies to segments).");
                    playUrl = resolvedStreamUrl;
                }
                else if (YtDlpManager.IsHlsStreamUrl(resolvedStreamUrl))
                {
                    try
                    {
                        playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterStream(resolvedStreamUrl, resolvedHeaders);
                        _pluginLog.Information($"[Live] Proxying HLS via {playUrl}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning(ex, "[Live] HLS proxy setup failed.");
                        PrintErrorChatFormat("[Media Player] Live stream setup failed: {0}", ex.Message);
                        return;
                    }
                }
                else
                {
                    playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterDirectMediaSession(resolvedStreamUrl, resolvedHeaders);
                    _pluginLog.Warning("[Live] Non-HLS live URL — playback may fail.");
                }
            }

            _liveProxyFallbackPending = false;
            _mediaManager.PlayStream(audioGameObject, playUrl, _config.SpatialAudioEnabled, finalStartTimeMs, resolvedHeaders, false, resolvedSlaveAudioUrl, true);
        }
        private bool _isIntentionallyPaused = false;
        private DateTime _lastUrlLoadTime = DateTime.MinValue;

        /// <summary>
        /// True while yt-dlp is resolving or VLC has not produced the first video frame yet.
        /// </summary>
        public bool IsMediaLoading => ComputeIsMediaLoading();

        /// <summary>
        /// Human-readable buffering status for the loading overlay.
        /// </summary>
        public string MediaLoadingMessage => GetMediaLoadingMessage();

        /// <summary>
        /// Animated 0–1 value for an indeterminate loading bar (ping-pong).
        /// </summary>
        public float MediaLoadingPulse
        {
            get
            {
                double t = (Environment.TickCount64 % 2000) / 2000.0;
                return (float)(t < 0.5 ? t * 2.0 : 2.0 - t * 2.0);
            }
        }

        private static bool IsYtDlpLoadingMessage(string msg)
        {
            return msg.Contains("buffer", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("SABR", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Resolving", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Preparing", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Downloading", StringComparison.OrdinalIgnoreCase);
        }

        private bool ComputeIsMediaLoading()
        {
            if (_isResolvingMedia)
            {
                return true;
            }

            if (_mediaManager?.ActiveStream?.IsPendingResumeSeek == true)
            {
                return true;
            }

            if (_mediaManager == null)
            {
                return false;
            }

            bool hasFrames;
            lock (_mediaManager.FrameLock)
            {
                hasFrames = _mediaManager.LastFrameWidth > 0
                    && _mediaManager.LastFrame != null
                    && _mediaManager.LastFrame.Length > 0;
            }

            if (hasFrames)
            {
                bool firstFrameThisSession = !_playbackHasRenderedFrames;
                _playbackHasRenderedFrames = true;
                _mediaLoadingMessage = "";
                if (firstFrameThisSession)
                {
                    TryEnsurePlaybackStarted(force: true);
                }
                return false;
            }

            // Seek / stream restart can briefly drop frames. Don't treat that as initial load.
            if (_playbackHasRenderedFrames)
            {
                return false;
            }

            var activeStream = _mediaManager.ActiveStream;
            if (activeStream == null)
            {
                return false;
            }

            if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Stopped && !_streamWasPlaying)
            {
                return false;
            }

            return true;
        }

        private void RefreshLocalizedOverlays()
        {
            if (_isQueueMenuOpen)
            {
                _queueMenuTextureManager?.UpdateQueue(_mediaQueue,
                    string.IsNullOrEmpty(_currentMediaTitle) ? Translate("Nothing Playing") : _currentMediaTitle);
            }

            if (_isHistoryMenuOpen)
            {
                _historyMenuTextureManager?.UpdateHistory(_config.WatchHistory);
            }

            _titleTextureManager?.InvalidateLoadingCache();
        }

        private string TranslateLoadingStatus(string msg)
        {
            if (Localization.TranslationGuard.ShouldSkipTranslation(msg))
            {
                return msg;
            }

            if (msg.StartsWith("SABR buffering...", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(msg, @"\(([0-9.]+)\s*MB ready\)");
                if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mb))
                {
                    return string.Format(Translate("SABR buffering... ({0:0.#} MB ready)"), mb);
                }

                return msg;
            }

            return Translate(msg);
        }

        private string GetMediaLoadingMessage()
        {
            if (_mediaManager?.ActiveStream?.IsPendingResumeSeek == true)
            {
                return Translate("Buffering to resume position...");
            }

            if (!string.IsNullOrWhiteSpace(_mediaLoadingMessage))
            {
                return TranslateLoadingStatus(_mediaLoadingMessage);
            }

            var path = _mediaManager?.ActiveStream?.SoundPath;
            if (_ytDlpManager?.TryGetSabrBufferStatus(path, out var sabrStatus) == true)
            {
                double mb = sabrStatus.BufferedBytes / (1024.0 * 1024.0);
                if (sabrStatus.IsDownloading)
                {
                    return mb > 0.01
                        ? string.Format(Translate("Buffering video... {0:0.#} MB downloaded"), mb)
                        : Translate("Buffering video... starting download");
                }
            }

            if (_isResolvingMedia)
            {
                return Translate("Preparing video...");
            }

            return Translate("Loading video...");
        }

        private void ResetPlaybackEnsureState()
        {
            _playbackEnsureAttempts = 0;
            _lastPlaybackEnsureUtc = DateTime.MinValue;
            _playbackEnsureUrl = _lastStreamURL ?? "";
        }

        /// <summary>
        /// Stops VLC/SABR/proxy state before loading a different URL. Must run off the game thread.
        /// </summary>
        private async Task TeardownPlaybackForMediaSwitchAsync()
        {
            _mediaManager?.StopStream();
            await Task.Delay(350).ConfigureAwait(false);
            _ytDlpManager?.ReleaseSabrSessions();
            MediaPlayerCore.StreamProxy.Instance.ClearSessions();
            await Task.Delay(100).ConfigureAwait(false);
        }

        /// <summary>
        /// Retries playback when media loaded but VLC did not start playing automatically.
        /// </summary>
        private void TryEnsurePlaybackStarted(bool force = false)
        {
            if (_disposed || _isIntentionallyPaused || _isResolvingMedia || !_streamWasPlaying)
            {
                return;
            }

            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null || string.IsNullOrEmpty(_lastStreamURL))
            {
                return;
            }

            if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                ResetPlaybackEnsureState();
                return;
            }

            var vlcState = activeStream.VlcState;
            if (!force && (vlcState == LibVLCSharp.Shared.VLCState.Opening || vlcState == LibVLCSharp.Shared.VLCState.Buffering))
            {
                return;
            }

            if (_playbackEnsureUrl != _lastStreamURL)
            {
                ResetPlaybackEnsureState();
            }

            if (!force && (DateTime.UtcNow - _lastPlaybackEnsureUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            if (_playbackEnsureAttempts >= 8)
            {
                return;
            }

            _lastPlaybackEnsureUtc = DateTime.UtcNow;
            _playbackEnsureAttempts++;

            if (activeStream.EnsurePlaying())
            {
                _pluginLog.Information($"[Media Player] Auto-started playback (attempt {_playbackEnsureAttempts}).");
                if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                {
                    ResetPlaybackEnsureState();
                }
            }
        }

        private bool IsUrlSafeForPublic(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                
                string[] safeDomains = {
                    "youtube.com", "youtu.be",
                    "twitch.tv",
                    "vimeo.com",
                    "soundcloud.com"
                };
                
                foreach (var domain in safeDomains)
                {
                    if (host == domain || host.EndsWith("." + domain))
                        return true;
                }
            }
            catch { }
            
            return false;
        }

        private void PlayRouted(string url, IMediaGameObject audioGameObject, int startTimeMs = 0, bool isAutoSync = false)
        {
            if (startTimeMs == 0 && (url.Contains("youtube.com") || url.Contains("youtu.be")))
            {
                startTimeMs = ExtractYouTubeStartTimeMs(url);
            }

            url = CleanUrl(url);

            if (YtDlpManager.IsUrlSupported(url) && _ytDlpManager.IsAvailable())
            {
                PlayViaYtDlp(url, audioGameObject, startTimeMs, isAutoSync);
            }
            else
            {
                TuneIntoStream(url, audioGameObject, startTimeMs, null, isAutoSync);
            }
        }
        private void PlayViaYtDlp(string url, IMediaGameObject audioGameObject, int startTimeMs = 0, bool isAutoSync = false)
        {
            if (_disposed) return;
            UpdateWatchHistory();
            DateTime resolutionStartTime = DateTime.UtcNow;

            url = CleanUrl(url);

            // Intercept local files or unsupported URLs and route them directly to TuneIntoStream
            if (!YtDlpManager.IsUrlSupported(url) || !_ytDlpManager.IsAvailable())
            {
                TuneIntoStream(url, audioGameObject, startTimeMs, null, isAutoSync);
                return;
            }

            _isIntentionallyPaused = false;
            _lastUrlLoadTime = DateTime.UtcNow;

            if (url != _lastStreamURL) _mediaErrorCount = 0;

            // If it's an auto-sync and we're already resolving something, ignore it to prevent spam.
            // But if it's a manual play, we ALLOW it to interrupt the current resolution!
            if (isAutoSync && _isResolvingMedia) return;

            if (!isAutoSync && CurrentTvPlacement?.IsLocked == true && CurrentTvPlacement?.OwnerId != _config.OwnerId && !IsHousingMenuOpen)
            {
                PrintErrorChat("[Media Player] Cannot play: The TV in this room is locked by its owner.");
                return;
            }

            string locationKey = _lastLocationKey;
            if (locationKey != null && locationKey.StartsWith("zone_") && _config.OnlySafeDomainsPublicScreens)
            {
                if (!IsUrlSafeForPublic(url))
                {
                    if (!isAutoSync) PrintErrorChat("[Media Player] Cannot play: Safe Mode is enabled for outdoor screens. Only verified domains (YouTube, Twitch, Vimeo) are allowed.");
                    _pluginLog.Warning($"[Social] Blocked playback of unsafe URL {url} due to Safe Mode.");
                    return;
                }
            }

            if (!isAutoSync)
            {
                _isLocalDj = true;
                _currentMediaOwnerId = _config.OwnerId;
            }

            Guid resolutionId = Guid.NewGuid();
            _currentResolutionId = resolutionId;
            _playbackHasRenderedFrames = false;
            ResetPlaybackEnsureState();

            // Direct playback for raw feeds (ignore query strings)
            string urlWithoutQuery = url.Split('?')[0];
            if (urlWithoutQuery.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                urlWithoutQuery.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                Task.Run(async () =>
                {
                    await TeardownPlaybackForMediaSwitchAsync().ConfigureAwait(false);
                    if (resolutionId != _currentResolutionId) return;

                    EnqueueFrameworkAction(() =>
                    {
                        if (_disposed || resolutionId != _currentResolutionId) return;

                        _lastStreamURL = url;
                        _lastStreamIsLive = urlWithoutQuery.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
                        _lastStreamObject = audioGameObject;
                        _streamURLs = new string[] { url };
                        _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                        string playUrl = url;
                        if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            if (urlWithoutQuery.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                            {
                                playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterStream(url, null);
                            }
                            else
                            {
                                playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterDirectMediaSession(url, null);
                            }
                        }

                        _mediaManager.PlayStream(audioGameObject, playUrl, _config.SpatialAudioEnabled, startTimeMs, null);

                        _currentMediaDurationMs = null;
                        _currentStreamer = "Direct Stream";
                        _currentMediaTitle = "Direct Stream";

                        PrintChat("[Media Player] Playing direct stream!\r\nUse \"/media video\" to toggle the video feed.\r\nUse \"/media stop\" to stop.");

                        if (!isAutoSync)
                        {
                            _ = PushMediaToServerAsync(isBackgroundSync: false);
                        }

                        _streamWasPlaying = true;
                        try { MuteBgm(); } catch (Exception e) { _pluginLog.Warning(e, e.Message); }
                        _isResolvingMedia = false;
                    });
                });
                return;
            }

            _isResolvingMedia = true;
            _mediaLoadingMessage = "Preparing video...";

            Task.Run(async () =>
            {
                if (_ytDlpInitTask != null && !_ytDlpInitTask.IsCompleted)
                {
                    EnqueueFrameworkAction(() => PrintChat("[Media Player] Waiting for yt-dlp download/update to finish..."));
                    await _ytDlpInitTask;
                }
                if (resolutionId != _currentResolutionId) return;

                await TeardownPlaybackForMediaSwitchAsync().ConfigureAwait(false);
                if (resolutionId != _currentResolutionId) return;

                try
                {
                    _lastStreamURL = url; // Save the original requested URL so PushMediaToServerAsync pushes it instead of the raw .m3u8

                    bool isYouTube = url.Contains("youtube.com") || url.Contains("youtu.be");
                    bool? youTubeLiveProbe = null;
                    if (isYouTube)
                    {
                        youTubeLiveProbe = await _ytDlpManager.ProbeYouTubeLiveBroadcastAsync(url).ConfigureAwait(false);
                        if (resolutionId != _currentResolutionId) return;

                        if (youTubeLiveProbe == true)
                        {
                            EnqueueFrameworkAction(() => _mediaLoadingMessage = "Connecting to live stream...");
                        }
                        else if (youTubeLiveProbe == false && _config.EnableSabrProxy)
                        {
                            string loadingMsg = startTimeMs > 0
                                ? "Buffering to resume position..."
                                : "Buffering video...";
                            EnqueueFrameworkAction(() => _mediaLoadingMessage = loadingMsg);
                        }
                    }

                    // Metadata fetch runs a second yt-dlp process and can trigger bot checks alongside SABR.
                    // For SABR VOD, fetch metadata after download starts. Concurrent yt-dlp processes
                    // fight over browser cookies and can kill long downloads.
                    bool isYouTubeSabr = _config.EnableSabrProxy && isYouTube && youTubeLiveProbe == false;

                    MediaPlayerCore.YtDlp.YtDlpMetadata? metadata = null;
                    string[]? streamUrls = null;

                    if (isYouTubeSabr)
                    {
                        // Lightweight title/uploader fetch. Run before SABR download starts so we
                        // don't run two yt-dlp processes concurrently (cookie/browser contention).
                        try
                        {
                            metadata = await _ytDlpManager.GetLightMetadata(url);
                            if (resolutionId != _currentResolutionId) return;
                            if (metadata?.FilesizeApprox > 0)
                            {
                                _ytDlpManager.RegisterExpectedFilesize(url, metadata.FilesizeApprox.Value);
                            }
                        }
                        catch (Exception metadataEx)
                        {
                            _pluginLog.Warning(metadataEx, "[yt-dlp] Failed to get light metadata for SABR.");
                        }

                        // Start SABR download. Duration can still be refined from VLC once parsed.
                        try
                        {
                            streamUrls = await _ytDlpManager.ResolveStreamUrl(url);
                            if (resolutionId != _currentResolutionId) return;
                        }
                        catch (Exception resolveEx)
                        {
                            if (HandleYtDlpResolveFailure(resolveEx, url)) return;
                        }
                    }
                    else
                    {
                        Task<YtDlpMetadata?> metadataTask = _ytDlpManager.GetMetadata(url);
                        var resolveTask = _ytDlpManager.ResolveStreamUrl(url);

                        try
                        {
                            streamUrls = await resolveTask;
                            if (resolutionId != _currentResolutionId) return;
                        }
                        catch (Exception resolveEx)
                        {
                            if (HandleYtDlpResolveFailure(resolveEx, url)) return;
                        }

                        try
                        {
                            metadata = await metadataTask;
                        }
                        catch (Exception metadataEx)
                        {
                            _pluginLog.Warning(metadataEx, "[yt-dlp] Failed to get metadata.");
                            if (HandleYtDlpMetadataFailure(metadataEx, url)) return;
                        }
                    }

                    if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                    {
                        _pluginLog.Warning($"[Media Player] Invalid stream URL rejected: {url}");
                        return;
                    }

                    if (streamUrls == null || streamUrls.Length == 0 || string.IsNullOrEmpty(streamUrls[0]))
                        {
                            // Fallback to CefSharp for heavily protected sites
                            EnqueueFrameworkAction(() => PrintChat("[Media Player] yt-dlp failed. Falling back to embedded browser resolver..."));

                        MediaPlayerCore.Resolvers.CefSharpResolverResult? cefResult = null;
                        try
                        {
                            MediaPlayerCore.Resolvers.CefSharpResolver.Initialize(_dependencyManager.DependenciesDir);
                            cefResult = await MediaPlayerCore.Resolvers.CefSharpResolver.ResolveStreamUrlAsync(url);
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Warning(ex, "[Media Player] CefSharp resolver crashed.");
                        }

                        if (cefResult != null && !string.IsNullOrEmpty(cefResult.Url))
                        {
                            streamUrls = new string[] { cefResult.Url };
                            metadata = new MediaPlayerCore.YtDlp.YtDlpMetadata { HttpHeaders = cefResult.Headers };
                            _cefBrowserHandle = cefResult.BrowserHandle;

                            if (!_isLocalDj && url != streamUrls[0] && startTimeMs < 5000)
                            {
                                _pluginLog.Information("[Social] Guest successfully resolved a raw Cef URL to a direct stream. Rescuing the host by pushing the .m3u8 back to the server!");
                                string rescuedStreamUrl = streamUrls[0];
                                EnqueueFrameworkAction(() =>
                                {
                                    _lastStreamURL = rescuedStreamUrl;
                                    _isLocalDj = true;
                                    _ = PushMediaToServerAsync(isBackgroundSync: false);
                                });
                            }

                            EnqueueFrameworkAction(() => PrintChat("[Media Player] Embedded browser successfully found stream URL."));

                            // Merge headers
                            if (metadata == null) metadata = new MediaPlayerCore.YtDlp.YtDlpMetadata();
                            if (metadata.HttpHeaders == null) metadata.HttpHeaders = new Dictionary<string, string>();
                            foreach (var kvp in cefResult.Headers)
                            {
                                metadata.HttpHeaders[kvp.Key] = kvp.Value;
                            }

                            // Proxy the stream so VLC can bypass Cloudflare using our extracted Cookies and Headers
                            try
                            {
                                streamUrls[0] = MediaPlayerCore.StreamProxy.Instance.RegisterStream(cefResult.Url, metadata.HttpHeaders, cefResult.M3u8Content);
                                _pluginLog.Info($"[Media Player] Proxying stream URL: {streamUrls[0]}");
                            }
                            catch (Exception proxyEx)
                            {
                                _pluginLog.Warning(proxyEx, "[Media Player] Failed to proxy stream URL, falling back to direct.");
                            }
                        }
                        else
                        {
                            var fallbackHeaders = metadata?.HttpHeaders;
                            EnqueueFrameworkAction(() =>
                            {
                                PrintErrorChat("[Media Player] Failed to resolve URL natively. Trying direct playback...");
                                TuneIntoStream(url, audioGameObject, startTimeMs, fallbackHeaders);
                            });
                            return;
                        }
                    }
                    string title = metadata?.Title;
                    if (string.IsNullOrWhiteSpace(title) || title == "Unknown") title = url;
                    string uploader = metadata?.Uploader ?? "";

                    // Twitch streams often don't explicitly return is_live=true, but they lack a duration!
                    // Also explicitly check if it's a twitch channel URL (not a video)
                    bool isTwitchLive = url.Contains("twitch.tv") && !url.Contains("/videos/");
                    bool isYouTubeLive = youTubeLiveProbe == true
                        || (youTubeLiveProbe == null && isYouTube && YtDlpManager.IsYouTubeLiveUrlHeuristic(url));
                    bool isLive = isYouTubeLive
                        || metadata?.IsLiveBroadcast == true
                        || (isTwitchLive && metadata?.IsLiveBroadcast != false)
                        || (!isYouTube && !isTwitchLive && metadata != null && metadata.Duration == null);
                    var resolvedStreamUrl = streamUrls[0];
                    if (isYouTube && youTubeLiveProbe != false && YtDlpManager.IsHlsStreamUrl(resolvedStreamUrl))
                    {
                        isLive = true;
                    }

                    var resolvedHeaders = _ytDlpManager.BuildPlaybackHeaders(
                        metadata?.HttpHeaders,
                        _ytDlpManager.LastResolvedHttpHeaders,
                        url);
                    var resolvedSlaveAudioUrl = (!isLive && streamUrls.Length > 1) ? streamUrls[1] : null;
                    var resolvedDurationMs = isLive ? null : metadata?.Duration * 1000.0;
                    string statusMsg = isLive ? "LIVE" : (metadata?.Duration.HasValue == true
                      ? TimeSpan.FromSeconds(metadata.Duration.Value).ToString(@"mm\:ss") : "");

                    EnqueueFrameworkAction(() =>
                    {
                        if (_disposed || resolutionId != _currentResolutionId || string.IsNullOrEmpty(resolvedStreamUrl)) return;

                        _lastStreamIsLive = isLive;
                        _lastStreamObject = audioGameObject;
                        _streamURLs = new string[] { resolvedStreamUrl };
                        _videoWindow.IsOpen = _config.DefaultVideoOpen == 0;

                        int finalStartTimeMs = isLive ? 0 : startTimeMs;
                        if (isAutoSync && !isLive)
                        {
                            finalStartTimeMs += (int)(DateTime.UtcNow - resolutionStartTime).TotalMilliseconds;
                        }

                        if (isLive)
                        {
                            PlayResolvedLiveStream(
                                audioGameObject,
                                resolvedStreamUrl,
                                resolvedHeaders,
                                isYouTube,
                                finalStartTimeMs,
                                resolvedSlaveAudioUrl);
                        }
                        else
                        {
                            string playUrl = resolvedStreamUrl;
                            if (playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !playUrl.Contains("127.0.0.1"))
                            {
                                playUrl = MediaPlayerCore.StreamProxy.Instance.RegisterDirectMediaSession(resolvedStreamUrl, resolvedHeaders);
                            }

                            _mediaManager.PlayStream(audioGameObject, playUrl, _config.SpatialAudioEnabled, finalStartTimeMs, resolvedHeaders, false, resolvedSlaveAudioUrl, false);
                        }

                        _lastStreamURL = url;
                        _currentMediaDurationMs = resolvedDurationMs;
                        _currentStreamer = !string.IsNullOrEmpty(uploader) ? uploader : title;
                        _currentMediaTitle = title;

                        PrintChatFormat("[Media Player] Now playing: {0}{1}{2}\r\nUse \"/media video\" to toggle the video feed.\r\nUse \"/media stop\" to stop.",
                            title,
                            !string.IsNullOrEmpty(uploader) ? string.Format(Translate(" by {0}"), uploader) : "",
                            !string.IsNullOrEmpty(statusMsg) ? string.Format(Translate(" [{0}]"), statusMsg) : "");

                        if (!isAutoSync)
                        {
                            _ = PushMediaToServerAsync(isBackgroundSync: false);
                        }

                        _streamWasPlaying = true;
                        try
                        {
                            MuteBgm();
                        }
                        catch (Exception e)
                        {
                            _pluginLog.Warning(e, e.Message);
                        }
                        _streamSetCooldown.Stop();
                        _streamSetCooldown.Reset();
                        _streamSetCooldown.Start();
                    });
                }
                finally
                {
                    if (resolutionId == _currentResolutionId)
                    {
                        _isResolvingMedia = false;
                    }
                }
            });
        }

        private void ChangeStreamQuality()
        {
            if (_streamURLs != null)
            {
                if (_streamWasPlaying && _streamURLs.Length > 0)
                {
                    if ((int)_videoWindow.FeedType < _streamURLs.Length)
                    {
                        if (_lastStreamObject != null)
                        {
                            try
                            {
                                _mediaManager.ChangeStream(_lastStreamObject, _streamURLs[(int)_videoWindow.FeedType], _videoWindow.Size.Value.X);
                            }
                            catch (Exception e)
                            {
                                _pluginLog.Warning(e, e.Message);
                            }
                        }
                    }
                }
            }
        }

        private void OnVideoWindowResized(object sender, EventArgs e)
        {
            ChangeStreamQuality();
        }

        private void _mediaManager_OnNewMediaTriggered(object sender, EventArgs e)
        {
            EnqueueFrameworkAction(() => {
                PrintVerbose("[Media Player] Starting Stream...");
                _mediaErrorCount = 0; // Reset errors on successful start
            });
        }

        private void _mediaManager_OnPlaybackFinished(object? sender, string e)
        {
            PrintVerbose("[Media Player] Playback finished.");

            if (_lastStreamIsLive && !_playbackHasRenderedFrames && _streamWasPlaying)
            {
                bool isYouTube = (_lastStreamURL ?? "").Contains("youtube", StringComparison.OrdinalIgnoreCase)
                    || (_lastStreamURL ?? "").Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

                if (!_liveProxyFallbackPending
                    && _lastStreamObject != null
                    && _streamURLs != null
                    && _streamURLs.Length > 0
                    && !string.IsNullOrEmpty(_streamURLs[0])
                    && isYouTube
                    && YtDlpManager.IsHlsStreamUrl(_streamURLs[0]))
                {
                    _pluginLog.Warning("[Media Player] Live direct HLS failed — retrying via proxy.");
                    _liveProxyFallbackPending = true;
                    _mediaManager?.StopStream();
                    var headers = _ytDlpManager.BuildPlaybackHeaders(
                        null,
                        _ytDlpManager.LastResolvedHttpHeaders,
                        _lastStreamURL ?? "");
                    PlayResolvedLiveStream(_lastStreamObject, _streamURLs[0], headers, true, 0, null);
                    return;
                }

                _pluginLog.Warning("[Media Player] Live stream ended before any video frames were decoded.");
                TryEnsurePlaybackStarted(force: true);
                return;
            }

            var activeStream = _mediaManager?.ActiveStream;
            string? sabrPath = activeStream?.SoundPath;
            if (_ytDlpManager?.IsSabrDownloadActiveForPath(sabrPath) == true
                && _lastStreamObject != null
                && activeStream != null)
            {
                _pluginLog.Information("[SABR] Buffer end reached while still downloading; resuming playback.");
                long resumeMs = activeStream.Time;
                _mediaManager.ChangeStream(_lastStreamObject, sabrPath!, _videoWindow.Size.Value.X, (int)resumeMs);
                return;
            }

            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                _config.WatchHistory.Remove(_lastStreamURL);
                _config.Save();
            }

            if (_mediaQueue.Count == 0 || e == "Emulation")
            {
                ResetStreamValues();
            }
            else
            {
                PlayNext();
            }
        }

        private unsafe void ResetStreamValues(bool pushToServer = true)
        {
            _lastStreamObject = null;
            _streamURLs = null;
            _potentialStream = "";
            _lastStreamURL = "";
            _currentMediaDurationMs = null;
            InvalidateSabrSeekCache();
            _currentStreamer = "";
            _currentMediaTitle = "";
            _mediaLoadingMessage = "";
            _playbackHasRenderedFrames = false;
            ResetPlaybackEnsureState();
            _videoWindow.IsOpen = false;
            _emulationClient?.Dispose();
            _emulationClient = null;
            _controllerService?.Dispose();
            _controllerService = null;
            _cefBrowserHandle?.Dispose();
            _cefBrowserHandle = null;

            _mediaManager?.StopStream();
            _ytDlpManager?.ReleaseSabrSessions();
            MediaPlayerCore.StreamProxy.Instance.ClearSessions();

            bool wasPlaying = _streamWasPlaying;
            _streamWasPlaying = false;
            _streamSetCooldown.Stop();
            _streamSetCooldown.Reset();
            _mediaErrorCount = 0; // Reset error count when stream stops
            _isLocalDj = false;

            if (wasPlaying)
            {
                _deferredBgmRestoreTime = DateTime.UtcNow.AddSeconds(1);
            }

            if (pushToServer) {
                _ = PushMediaToServerAsync(isBackgroundSync: false);
            }
        }

        #endregion

        #region Chat Twitch Detection

        private void PrintVerbose(string message)
        {
            if (_config.VerboseChatLogging)
            {
                PrintChat(message);
            }
        }

        private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage msg)
        {
            if (_disposed || _mediaManager == null) return;

            var type = msg.LogKind;
            if (type == XivChatType.Yell || type == XivChatType.Shout || type == XivChatType.TellIncoming)
            {
                TwitchChatCheck(msg.Message.TextValue, type);
            }
        }

        private unsafe void TwitchChatCheck(string messageText, XivChatType type)
        {
            if (_config.TuneIntoTwitchStreams && IsResidential())
            {
                if (!_streamSetCooldown.IsRunning || _streamSetCooldown.ElapsedMilliseconds > 10000)
                {
                    var strings = messageText.Split(' ');
                    foreach (string value in strings)
                    {
                        if (value.Contains("twitch.tv") && _lastStreamURL != value)
                        {
                            if (_playerObject != null)
                            {
                                var audioGameObject = CurrentAudioSource;
                                if (_mediaManager.IsAllowedToStartStream(audioGameObject))
                                {
                                    _lastStreamObject = CurrentAudioSource;
                                    PlayRouted(value.Trim('(').Trim(')').Trim('[').Trim(']').Trim('!').Trim('@'), audioGameObject, 0);
                                }
                            }
                            break;
                        }
                    }
                }
            }
            else if (_config.TuneIntoTwitchStreamPrompt)
            {
                var strings = messageText.Split(' ');
                foreach (string value in strings)
                {
                    if (value.Contains("twitch.tv") && _lastStreamURL != value)
                    {
                        _potentialStream = value;
                        _lastStreamURL = value;
                        string cleanedURL = RemoveSpecialSymbols(value);
                        string streamer = cleanedURL.Replace(@"https://", null).Replace(@"www.", null).Replace("twitch.tv/", null);
                        PrintChatFormat("[Media Player] {0} is hosting a stream! Use \"/media listen\" to tune in.", streamer);
                    }
                }
            }
        }

        private unsafe bool IsResidential()
        {
            return HousingManager.Instance()->IsInside() || HousingManager.Instance()->OutdoorTerritory != null;
        }

        #endregion

        #region Event Handlers

        private void OnTerritoryChanged(uint territoryId)
        {
            SaveMediaStateForCurrentLocation();
            _videoWindow.IsOpen = false;
            if (_screenSettingsWindow != null) _screenSettingsWindow.IsOpen = false;
            _mediaManager?.CleanSounds();
            ResetStreamValues(false);
            CurrentTvPlacement = null;

            _deferredTerritoryChangeTime = DateTime.UtcNow.AddSeconds(3);
        }

        private async Task FetchServerDataForCurrentLocationAsync()
        {
            // Fetch public TVs from the server
            var keys = GetCurrentLocationKeys();
            if (keys.Count == 0) return;

            string primaryKey = GetLocationKey();
            bool isHouse = !string.IsNullOrEmpty(primaryKey) && primaryKey.StartsWith("house_");
            bool isZone = !string.IsNullOrEmpty(primaryKey) && primaryKey.StartsWith("zone_");

            if (isHouse || (isZone && _config.EnableOutdoorPublicScreens))
            {
                var tvs = await ServerClient.GetTvsBatchAsync(keys);
                _nearbyTvs = tvs;

                if (tvs.Count > 0)
                {
                    // If multiple TVs are found, prioritize exact key match (plot over grid), then closest to player
                    if (tvs.Count > 1)
                    {
                        var playerPos = _cachedLocalPlayerPosition;
                        if (playerPos != null)
                        {
                            tvs = tvs.OrderBy(t => t.LocationKey != primaryKey)
                                     .ThenBy(t => System.Numerics.Vector3.Distance(playerPos.Value, new System.Numerics.Vector3(t.PositionX, t.PositionY, t.PositionZ))).ToList();
                        }
                    }

                    var tv = tvs[0];
                    CurrentTvPlacement = tv;
                    
                    // Apply to the active renderer transform only if we aren't actively editing it.
                    // Server placement state is authoritative, if a TV exists on the server,
                    // render it unless the owner removes it from the area.
                    if (_worldRenderer != null && !IsHousingMenuOpen && !(_screenSettingsWindow?.IsOpen == true))
                    {
                        _worldRenderer.Transform.Enabled = true;
                        _worldRenderer.Transform.Position = new System.Numerics.Vector3(tv.PositionX, tv.PositionY, tv.PositionZ);
                        _worldRenderer.Transform.RotationDegrees = new System.Numerics.Vector3(tv.RotationX, tv.RotationY, tv.RotationZ);
                        _worldRenderer.Transform.Scale = new System.Numerics.Vector2(tv.ScaleX, tv.ScaleY);
                        _worldRenderer.Transform.Opacity = tv.Opacity;
                        _worldRenderer.Transform.IsProjectorMode = tv.IsProjectorMode;
                        _worldRenderer.Transform.ScreensaverColor = new System.Numerics.Vector3(tv.ScreensaverColorR, tv.ScreensaverColorG, tv.ScreensaverColorB);
                        _worldRenderer.Transform.ScreensaverStyle = tv.ScreensaverStyle;
                        
                        _screenSettingsWindow?.SyncFromTransform();
                    }

                    // Persist server coordinates into local config so that RestoreScreenForCurrentLocation
                    // (which fires before this async fetch completes) won't push stale coordinates.
                    // This prevents co-owners of the same house from overwriting each other's TV placement.
                    var serverKey = !string.IsNullOrEmpty(tv.LocationKey) ? tv.LocationKey : primaryKey;
                    _config.ScreenPlacements[serverKey] = new MediaPlayerCore.Compositing.WorldScreenTransform {
                        Position = new System.Numerics.Vector3(tv.PositionX, tv.PositionY, tv.PositionZ),
                        RotationDegrees = new System.Numerics.Vector3(tv.RotationX, tv.RotationY, tv.RotationZ),
                        Scale = new System.Numerics.Vector2(tv.ScaleX, tv.ScaleY),
                        Enabled = true,
                        Opacity = tv.Opacity,
                        IsProjectorMode = tv.IsProjectorMode,
                        ScreensaverColor = new System.Numerics.Vector3(tv.ScreensaverColorR, tv.ScreensaverColorG, tv.ScreensaverColorB),
                        ScreensaverStyle = tv.ScreensaverStyle
                    };
                    // Also store under the primary key in case they differ (e.g. batch vs single key)
                    if (serverKey != primaryKey)
                    {
                        _config.ScreenPlacements[primaryKey] = _config.ScreenPlacements[serverKey];
                    }
                    _config.Save();

                    _pluginLog.Info($"[Social] Loaded public TV placement from room {tv.LocationKey}.");
                }
                else
                {
                    CurrentTvPlacement = null;
                    if (!IsHousingMenuOpen)
                    {
                        RestoreScreenForCurrentLocation();
                    }
                }

                // Automatically sync the media playback from the server upon entering the room
                await FetchMediaFromServerAsync();
            }
            else if (isZone)
            {
                // If outdoor screens are disabled, ensure we don't hold onto a stale TV placement
                CurrentTvPlacement = null;
            }
        }

        /// <summary>
        /// Saves the current screen placement to the config for the current location.
        /// </summary>
        private void SaveScreenForCurrentLocation()
        {
            if (_worldRenderer?.Transform == null) return;
            var key = _lastLocationKey;
            if (string.IsNullOrEmpty(key)) return;
            var transform = _worldRenderer.Transform.Clone();
            transform.Enabled = _worldRenderer.Transform.Enabled;
            _config.ScreenPlacements[key] = transform;
            _config.Save();
        }

        /// <summary>
        /// Restores a saved screen placement for the current location, if one exists.
        /// </summary>
        private void RestoreScreenForCurrentLocation()
        {
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;
            if (_config.ScreenPlacements.TryGetValue(key, out var saved))
            {
                _worldRenderer.Transform.Position = saved.Position;
                _worldRenderer.Transform.RotationDegrees = saved.RotationDegrees;
                _worldRenderer.Transform.Scale = saved.Scale;
                _worldRenderer.Transform.Enabled = saved.Enabled;
                _worldRenderer.Transform.Opacity = saved.Opacity;
                _worldRenderer.Transform.IsProjectorMode = saved.IsProjectorMode;
                _worldRenderer.Transform.ScreensaverColor = saved.ScreensaverColor;
            }
            else
            {
                _worldRenderer.Transform.Position = System.Numerics.Vector3.Zero;
                _worldRenderer.Transform.RotationDegrees = System.Numerics.Vector3.Zero;
                _worldRenderer.Transform.Scale = new System.Numerics.Vector2(3.0f, 1.6875f);
                _worldRenderer.Transform.Enabled = false; // Turn off 3D screen in new zones by default
                _worldRenderer.Transform.Opacity = 1.0f;
                _worldRenderer.Transform.IsProjectorMode = false;
                _worldRenderer.Transform.ScreensaverColor = new System.Numerics.Vector3(0.0f, 0.0f, 0.0f);
                _worldRenderer.Transform.ScreensaverStyle = 0;
            }
        }

        /// <summary>
        /// Saves the current media URL, queue, and timecode for the current location.
        /// </summary>
        private void SaveMediaStateForCurrentLocation()
        {
            var key = CurrentTvPlacement?.LocationKey ?? _lastLocationKey;
            if (string.IsNullOrEmpty(key)) return;

            var state = new RoomMediaState();

            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream != null && !string.IsNullOrEmpty(activeStream.SoundPath))
            {
                // We use _lastStreamURL to save the original un-resolved YouTube/Twitch URL
                // so we can re-resolve it via yt-dlp upon entering the room next time!
                string fallbackPath = activeStream.SoundPath;
                // Never save local StreamProxy URLs they are ephemeral and won't work on next launch or for other players
                if (fallbackPath != null && fallbackPath.Contains("127.0.0.1")) fallbackPath = "";
                state.CurrentUrl = !string.IsNullOrEmpty(_lastStreamURL) ? _lastStreamURL : fallbackPath;
                state.TimecodeMs = activeStream.Time;
            }

            state.Playlist = new System.Collections.Generic.List<string>(_mediaQueue);

            _config.RoomMediaStates[key] = state;
            _config.Save();
            _pluginLog.Information($"Saved media state for {key}: {state.CurrentUrl} @ {state.TimecodeMs}ms ({state.Playlist.Count} queued)");
        }

        /// <summary>
        /// Restores media playback from config for the current location.
        /// </summary>
        private void RestoreMediaForCurrentLocation()
        {
            var key = CurrentTvPlacement?.LocationKey ?? GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;

            // Track location for future saving
            _lastLocationKey = key;

            if (_config.RoomMediaStates.TryGetValue(key, out var state))
            {
                _pluginLog.Information($"Restoring media state for {key}");

                // Restore queue
                _mediaQueue.Clear();
                if (state.Playlist != null)
                {
                    foreach (var url in state.Playlist)
                    {
                        _mediaQueue.Enqueue(url);
                    }
                }

                // Start playback if there was a URL and auto resume is enabled
                if (_config.AutoResumeMedia && !string.IsNullOrEmpty(state.CurrentUrl) && _playerObject != null)
                {
                    PrintChat("[Media Player] Resuming playback in this room...");
                    _lastStreamObject = CurrentAudioSource;
                    PlayRouted(state.CurrentUrl, CurrentAudioSource, (int)state.TimecodeMs, isAutoSync: true);
                }
            }
        }

        public async Task PushMediaToServerAsync(bool isBackgroundSync, long? overrideTimeMs = null)
        {
            var key = CurrentTvPlacement?.LocationKey ?? _lastLocationKey;
            
            var activeStream = _mediaManager?.ActiveStream;
            string lastUrl = CleanUrl(_lastStreamURL ?? "");
            string soundPath = activeStream?.SoundPath ?? "";
            // Never let local StreamProxy URLs (e.g. http://127.0.0.1:xxxxx/stream.m3u8?sid=...) leak as fallback
            if (soundPath.Contains("127.0.0.1")) soundPath = "";
            long activeTime = overrideTimeMs ?? (long)(activeStream?.Time ?? 0);
            bool isIntentionallyPaused = _isIntentionallyPaused;
            var mediaQueueArray = _mediaQueue.ToArray();
            var duration = _currentMediaDurationMs;

            await Task.Run(async () =>
            {
                _pluginLog.Information($"[Sync] PushMediaToServerAsync invoked. Key: {key}");
                _pluginLog.Information($"[Sync] Attempting to push media to server for key: {key}");
                if (string.IsNullOrEmpty(key) || (!key.StartsWith("house_") && !key.StartsWith("zone_")))
                {
                    _pluginLog.Information($"[Sync] Aborting push because key is invalid.");
                    return;
                }

                // Don't push local files
                if (!string.IsNullOrEmpty(lastUrl) && !lastUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !lastUrl.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) && !lastUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) return;
                
                // Don't push local StreamProxy URLs to the sync server
                if (!string.IsNullOrEmpty(lastUrl) && lastUrl.Contains("127.0.0.1")) return;
                
                // Only return early if we are doing a background sync and have nothing to push.
                // If it's a foreground sync (e.g. user pressed stop, or video finished), WE MUST push the empty state!
                if (isBackgroundSync && string.IsNullOrEmpty(lastUrl) && string.IsNullOrEmpty(soundPath) && !_isResolvingMedia) return;

                var sync = new Networking.Models.RoomMediaStateSync
                {
                    LocationKey = key,
                    CurrentUrl = !string.IsNullOrEmpty(lastUrl) ? lastUrl : soundPath,
                    TimecodeMs = activeTime,
                    // Only push "Paused" if the DJ explicitly pressed the pause button!
                    // Otherwise, random network buffering on the DJ's client will accidentally force-pause the entire room!
                    IsPlaying = !isIntentionallyPaused,
                    OwnerId = _config.OwnerId,
                    PlaylistJson = System.Text.Json.JsonSerializer.Serialize(mediaQueueArray),
                    BypassLock = IsHousingMenuOpen || key.StartsWith("zone_"),
                    DurationMs = duration,
                    IsBackgroundSync = isBackgroundSync
                };

                // Update local config immediately so we don't lose our place if we crash or the server is unavailable
                var state = new RoomMediaState
                {
                    CurrentUrl = sync.CurrentUrl,
                    TimecodeMs = sync.TimecodeMs,
                    Playlist = new List<string>(mediaQueueArray)
                };
                _config.RoomMediaStates[key] = state;
                _config.Save();

                try
                {
                    await ServerClient.UpdateMediaStateAsync(key, sync);
                    _currentMediaOwnerId = _config.OwnerId;

                    // If we successfully pushed a foreground sync, we are definitely the DJ now.
                    if (!isBackgroundSync)
                    {
                        PrintChat("[Media Player] Server push successful!");
                        _isLocalDj = true;
                        _currentMediaOwnerId = _config.OwnerId;
                    }
                    _pluginLog.Information($"[Sync] Payload successfully pushed to server.");
                }
                catch (InvalidOperationException)
                {
                    // We were deposed as the DJ!
                    _isLocalDj = false;
                    _currentMediaOwnerId = "";
                    await FetchMediaFromServerAsync();
                }
                catch (UnauthorizedAccessException)
                {
                    _isLocalDj = false;
                    _currentMediaOwnerId = "";
                    PrintErrorChat("[Media Player] Cannot share media: The TV in this room is locked by its owner.");
                    await FetchMediaFromServerAsync();
                }
                catch (ArgumentException ex)
                {
                    _isLocalDj = false; // Strip DJ status so background sync stops spamming the server
                    _currentMediaOwnerId = "";

                    if (IsPlayerAlone())
                    {
                        PrintChatFormat("[Media Player] {0} (Playing locally only since you are alone).", ex.Message);
                    }
                    else
                    {
                        PrintErrorChatFormat("[Media Player] {0} Cannot share video because others are around.", ex.Message);
                        await FetchMediaFromServerAsync();
                    }
                }
                catch (HttpRequestException ex)
                {
                    _pluginLog.Warning(ex, "[Sync] Server connection failed.");
                    if (!isBackgroundSync)
                    {
                        PrintErrorChat("[Media Player] Cannot connect to sync server. It may be offline.");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error(ex, "[Sync] Failed to push media state to server.");
                }
            });
        }

        private async Task FetchServerTimeAsync()
        {
            if (ServerClient == null) return;
            long st = await ServerClient.GetServerTimeAsync();
            if (st > 0) {
                _serverTimeOffsetMs = st - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _pluginLog.Information($"[Time Sync] Server time offset calculated: {_serverTimeOffsetMs}ms");
            } else {
                _hasFetchedServerTime = false;
            }
        }

        public async Task FetchMediaFromServerAsync()
        {
            var key = CurrentTvPlacement?.LocationKey ?? _lastLocationKey;
            _pluginLog.Information($"[Sync] FetchMediaFromServerAsync invoked. Key: {key}");
            if (string.IsNullOrEmpty(key)) return;
            bool isHouse = key.StartsWith("house_");
            bool isZone = key.StartsWith("zone_");
            if (!isHouse && !(isZone && _config.EnableOutdoorPublicScreens)) return;

            var sync = await ServerClient.GetMediaStateAsync(key);
            if (sync == null || string.IsNullOrEmpty(sync.CurrentUrl)) return;

            _currentMediaOwnerId = sync.OwnerId;

            if (_isLocalDj)
            {
                // Media change events override DJ status. If someone else changed the URL, step down!
                if (sync.OwnerId != _config.OwnerId && sync.CurrentUrl != _lastStreamURL)
                {
                    _pluginLog.Information($"[Social] Another player entered in new media! Stepping down as DJ.");
                    _isLocalDj = false;
                }
                else
                {
                    return;
                }
            }

            int realPlayerCount = _cachedRealPlayerCount;
            bool isRoomEmpty = realPlayerCount <= 1; // 1 means only we are here

            _pluginLog.Information($"[Social] Reclaim Check: RealPlayers={realPlayerCount}, isRoomEmpty={isRoomEmpty}, Owner={sync.OwnerId}=={_config.OwnerId}, LocalStateFound={_config.RoomMediaStates.TryGetValue(key, out var localState)}, CurrentUrl={localState?.CurrentUrl}=={sync.CurrentUrl}");

            if (isRoomEmpty && sync.OwnerId == _config.OwnerId && localState != null && localState.CurrentUrl == sync.CurrentUrl)
            {
                _pluginLog.Information("[Social] Reclaiming DJ status and trusting local timecode over server state. The room was empty.");
                _isLocalDj = true;
                return;
            }

            // Use the DataAgeMs calculated purely by the server to completely eliminate client clock drift issues!
            // We only add the age if the video is currently playing.
            var targetTimeMs = sync.IsPlaying ? sync.TimecodeMs + (long)sync.DataAgeMs : sync.TimecodeMs;

            if (sync.DurationMs.HasValue && sync.DurationMs.Value > 0)
            {
                // If the DJ left without pausing, the server assumes it's still playing infinitely.
                // Cap the target time to just before the end so VLC naturally triggers EndReached instead of silently failing and restarting!
                if (targetTimeMs >= sync.DurationMs.Value)
                {
                    targetTimeMs = (long)sync.DurationMs.Value - 500;
                    if (targetTimeMs < 0) targetTimeMs = 0;
                }
            }

            _pluginLog.Information($"[Social] Fetched media sync: Server TimecodeMs={sync.TimecodeMs}, DataAgeMs={sync.DataAgeMs}. Calculated TargetTimeMs={targetTimeMs}.");

            // Update local config
            var state = new RoomMediaState
            {
                CurrentUrl = sync.CurrentUrl,
                TimecodeMs = targetTimeMs,
                Playlist = new List<string>(System.Text.Json.JsonSerializer.Deserialize<string[]>(sync.PlaylistJson) ?? Array.Empty<string>())
            };
            _config.RoomMediaStates[key] = state;
            _config.Save();

            // Actually play it if it's different or out of sync
            if (_mediaManager != null && _mediaManager.IsFFmpegPlaying)
            {
                return; // NEVER interrupt a local FFmpeg stream with a server sync!
            }
            if (_isResolvingMedia) return;

            var activeStream = _mediaManager?.ActiveStream;
            bool isLocalEnded = activeStream != null && activeStream.VlcState == LibVLCSharp.Shared.VLCState.Ended;
            bool isDifferentUrl = activeStream == null || (!string.IsNullOrEmpty(_lastStreamURL) && _lastStreamURL != sync.CurrentUrl) || (isLocalEnded && sync.IsPlaying);
            // Only sync VODs. Live streams cannot be reliably timecode-synced.
            bool isOutofSync = !_lastStreamIsLive && activeStream != null && Math.Abs(activeStream.Time - targetTimeMs) > 2500;
            bool localIsPlaying = activeStream != null && activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing;

            if (isDifferentUrl)
            {
                _pluginLog.Information($"[Social] Syncing NEW media from server: {sync.CurrentUrl} at {targetTimeMs}ms (Playing: {sync.IsPlaying})");
                EnqueueFrameworkAction(() =>
                {
                    PrintChat("[Media Player] Server Sync: Now playing media loaded by the room owner.");

                    _mediaQueue.Clear();
                    foreach (var url in state.Playlist) _mediaQueue.Enqueue(url);

                    if (_playerObject != null)
                    {
                        // Starts the stream. If sync.IsPlaying is false, we should pause it immediately after it loads...
                        // But yt-dlp might take a while, so we just let it start and the next poll will poll it.
                        PlayRouted(state.CurrentUrl, CurrentAudioSource, (int)targetTimeMs, isAutoSync: true);
                    }
                });
            }
            else if (activeStream != null)
            {
                if (isOutofSync)
                {
                    // If the server is paused, and the data is old, ignore timecode sync!
                    if (!sync.IsPlaying && sync.DataAgeMs >= 15000 && localIsPlaying)
                    {
                        _pluginLog.Information("[Social] Ignoring timecode sync because server is paused and data is stale.");
                    }
                    else
                    {
                        _pluginLog.Information($"[Social] Adjusting timecode to sync with server. Local Time: {activeStream.Time}ms | Target Time: {targetTimeMs}ms");
                        SeekToMs((long)targetTimeMs, syncToServer: false, userInitiated: false);
                    }
                }

                if (!_lastStreamIsLive)
                {
                    if (sync.IsPlaying && !localIsPlaying)
                    {
                        _pluginLog.Information($"[Social] Server says play, but we are paused. Resuming.");
                        activeStream.Resume();
                    }
                    else if (!sync.IsPlaying && localIsPlaying)
                    {
                        bool isNewlyLoaded = (DateTime.UtcNow - _lastUrlLoadTime).TotalSeconds < 20;

                        // Check sync staleness or new stream status
                        if (sync.DataAgeMs < 15000 || isNewlyLoaded)
                        {
                            _pluginLog.Information($"[Social] Server says paused (NewlyLoaded: {isNewlyLoaded}). Pausing.");
                            activeStream.Pause();
                        }
                        else
                        {
                            _pluginLog.Information($"[Social] Server says paused, but it is {sync.DataAgeMs}ms old. Ignoring.");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Generates a unique key for the current location.
        public string LocationKey => GetLocationKey();

        /// <summary>
        /// Generates a unique string identifier for the current in-game location.
        /// Regular zones: "zone_{territoryId}"
        /// Housing: "house_{worldId}_{territoryId}_{ward}_{plot}_{room}"
        /// </summary>
        public unsafe List<string> GetCurrentLocationKeys()
        {
            var keys = new List<string>();
            var primaryKey = GetLocationKey();
            if (!string.IsNullOrEmpty(primaryKey))
            {
                keys.Add(primaryKey);
                
                // If standing on a plot, also search the fallback grid for TVs in case a plot TV doesn't exist
                if (primaryKey.Contains("_plot_"))
                {
                    var territoryId = _clientState.TerritoryType;
                    ushort worldId = (ushort)_cachedLocalPlayerWorldId;
                    var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
                    short ward = housingMgr != null ? housingMgr->GetCurrentWard() : (short)-1;

                    var playerPos = _cachedLocalPlayerPosition;
                    if (playerPos != null)
                    {
                        int gridX = (int)Math.Floor(playerPos.Value.X / 60.0f);
                        int gridZ = (int)Math.Floor(playerPos.Value.Z / 60.0f);
                        keys.Add($"zone_{worldId}_{ward}_{territoryId}_grid_{gridX}_{gridZ}");
                    }
                }
            }
            return keys;
        }

        public unsafe string GetLocationKey()
        {
            try
            {
                var territoryId = _clientState.TerritoryType;
                if (territoryId == 0) return null;

                ushort worldId = (ushort)_cachedLocalPlayerWorldId;

                var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
                short ward = housingMgr != null ? housingMgr->GetCurrentWard() : (short)-1;
                short plot = housingMgr != null ? housingMgr->GetCurrentPlot() : (short)-1;
                short room = housingMgr != null ? housingMgr->GetCurrentRoom() : (short)-1;
                ulong indoorHouseId = housingMgr != null ? housingMgr->GetCurrentIndoorHouseId().Id : 0;

                if (housingMgr != null && housingMgr->IsInside())
                {
                    return $"house_{worldId}_{territoryId}_{ward}_{plot}_{room}_{indoorHouseId}";
                }

                if (housingMgr != null && plot >= 0 && ward >= 0)
                {
                    return $"zone_{worldId}_{ward}_{territoryId}_plot_{plot}";
                }

                if (territoryId == 1055)
                {
                    var mji = FFXIVClientStructs.FFXIV.Client.Game.MJI.MJIManager.Instance();
                    if (mji != null && mji->IsPlayerInSanctuary)
                    {
                        var localPlayer = GetLocalPlayer();
                        if (localPlayer != null)
                        {
                            return $"island_{worldId}_{localPlayer.Name.TextValue}";
                        }
                    }
                    else
                    {
                        // Visiting another island. Try to guess owner from party leader, or use automated fallback.
                        if (_partyList.Length > 0 && _partyList[0] != null)
                        {
                            return $"island_{worldId}_{_partyList[0].Name.TextValue}";
                        }

                        // Fallback: guess the owner based on the first other player in the object table.
                        // The island owner is almost always the first person in the instance.
                        var lp = GetLocalPlayer();
                        foreach (var obj in _objectTable)
                        {
                            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc && 
                                lp != null && pc.Name.TextValue != lp.Name.TextValue)
                            {
                                return $"island_{worldId}_{pc.Name.TextValue}";
                            }
                        }

                        return null; // Don't know who we are visiting
                    }
                }

                var playerPos = _cachedLocalPlayerPosition;
                if (playerPos != null)
                {
                    int gridX = (int)Math.Floor(playerPos.Value.X / 60.0f);
                    int gridZ = (int)Math.Floor(playerPos.Value.Z / 60.0f);
                    return $"zone_{worldId}_{ward}_{territoryId}_grid_{gridX}_{gridZ}";
                }

                return $"zone_{worldId}_{ward}_{territoryId}";
            }
            catch
            {
                return $"zone_0_-1_{_clientState.TerritoryType}";
            }
        }

        private void OnLogin()
        {
            _hasBeenInitialized = false;
        }

        private void OnLogout(int type, int code)
        {
            if (_videoWindow != null) _videoWindow.IsOpen = false;
            if (_screenSettingsWindow != null) _screenSettingsWindow.IsOpen = false;
            if (_settingsWindow != null) _settingsWindow.IsOpen = false;
            _mediaManager?.CleanSounds();
            ResetStreamValues();
        }

        private int _mediaErrorCount = 0;
        private DateTime _lastMediaErrorTime = DateTime.MinValue;

        private void OnMediaError(object? sender, MediaError e)
        {
            string errorMsg = e.Exception?.Message ?? string.Empty;

            // Harmless on live/HLS demuxers. Querying playback time is unsupported.
            if (errorMsg.Contains("DEMUX_GET_TIME", StringComparison.OrdinalIgnoreCase)
                || errorMsg.Contains("DEMUX_GET_LENGTH", StringComparison.OrdinalIgnoreCase)
                || errorMsg.Contains("Failed to create demuxer", StringComparison.OrdinalIgnoreCase)
                || errorMsg.Contains("dav1d", StringComparison.OrdinalIgnoreCase)
                || errorMsg.Contains("Decoder feed error", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.IsNullOrEmpty(errorMsg))
            {
                return;
            }

            _pluginLog.Warning(e.Exception, $"[Media Player] Media error: {errorMsg}");

            if (!errorMsg.Contains("demux", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if ((DateTime.UtcNow - _lastMediaErrorTime).TotalMilliseconds < 500)
            {
                // Group errors that occur within 500ms into a single "error event"
                _pluginLog.Warning(e.Exception, $"[Media Player] Media error occurred. (grouped)");
                return;
            }

            _lastMediaErrorTime = DateTime.UtcNow;
            _mediaErrorCount++;
            _pluginLog.Warning(e.Exception, $"[Media Player] Media error occurred. Error count: {_mediaErrorCount}");
            if (_mediaErrorCount < 5)
            {
                RequestRefreshCurrentMedia();
            }
            else if (_mediaErrorCount == 5)
            {
                PrintErrorChat("[Media Player] Failed to play media after multiple attempts.");
                EnqueueFrameworkAction(() =>
                {
                    _mediaManager?.StopStream();
                    ResetStreamValues();
                });
            }
        }

        private void UpdateWatchHistory()
        {
            if (string.IsNullOrEmpty(_lastStreamURL) || _mediaManager?.ActiveStream == null) return;
            
            long time = _mediaManager.ActiveStream.Time;
            long length = _mediaManager.ActiveStream.Length;
            
            // Only track media that has been watched for at least 5 seconds
            if (time <= 5000) return;
            
            // If it has reached within 5 seconds of the end, don't update it, let the End hook remove it
            if (length > 0 && time >= length - 5000) return;

            string title = !string.IsNullOrEmpty(_currentMediaTitle) && _currentMediaTitle != "Loading..." 
                ? _currentMediaTitle 
                : _lastStreamURL;

            var entry = new MediaHistoryEntry {
                Url = _lastStreamURL,
                Title = title,
                TimecodeMs = time,
                LastPlayed = DateTime.UtcNow
            };
            
            _config.WatchHistory[_lastStreamURL] = entry;
            _config.Save();
        }

        /// <summary>
        /// Mutes the in-game BGM, saving the previous state so we can restore it.
        /// </summary>
        private void MuteBgm()
        {
            try
            {
                _gameConfig.TryGet(SystemConfigOption.IsSndBgm, out bool wasMuted);
                if (!wasMuted)
                {
                    _bgmWasMutedByUs = true;
                    _gameConfig.Set(SystemConfigOption.IsSndBgm, true);
                }
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to mute BGM");
            }
        }

        /// <summary>
        /// Restores BGM if we were the ones who muted it.
        /// </summary>
        private void RestoreBgm()
        {
            try
            {
                if (_bgmWasMutedByUs)
                {
                    _bgmWasMutedByUs = false;
                    _gameConfig.Set(SystemConfigOption.IsSndBgm, false);
                }
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to restore BGM");
            }
        }

        /// <summary>
        /// Fixes the Windows audio mixer volume for this process.
        /// VLC can zero it out via the Windows audio session API.
        /// Uses COM interfaces + WaveOutEvent trick (same approach as ArtemisRoleplayingKit).
        /// </summary>
        private void FixWindowsVolume()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                VolumeMixer.SetApplicationVolume(pid, 100);
                VolumeMixer.SetApplicationMute(pid, false);

                // Force Windows to re-register the audio session at full volume
                using (var tempPlayer = new NAudio.Wave.WaveOutEvent())
                {
                    tempPlayer.Volume = 1;
                }
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to fix Windows volume");
            }
        }

        #endregion
#region UI

        private unsafe void OnDraw()
        {
            if (_worldRenderer != null) _worldRenderer.UseDepthOcclusion = _config.DepthOcclusionEnabled;

            bool useDifferenceFallback = false;
            if (_config.EnableWanderersCampfireFix && _objectTable != null) {
                foreach (var obj in _objectTable) {
                    if (obj == null || obj.Name == null) continue;
                    var name = obj.Name.ToString();
                    // Wanderer's Campfire, Feu de camp du vagabond, Wanderers Lagerfeuer
                    if (obj.DataId == 197274 || (name.Contains("Wanderer's Campfire", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("Wanderers Lagerfeuer", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("Feu de camp du vagabond", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("放浪神の焚き火", StringComparison.OrdinalIgnoreCase))) {
                        useDifferenceFallback = true;
                        break;
                    }
                }
            }

            // Reset per-frame depth capture flag
            _depthCapture?.BeginFrame();

            if (!_dependencyManager.IsReady)
            {
                if (_dependencyManager.IsDownloading || _dependencyManager.HasError)
                {
                    ImGui.SetNextWindowPos(new System.Numerics.Vector2(ImGui.GetIO().DisplaySize.X / 2 - 200, ImGui.GetIO().DisplaySize.Y / 2 - 50));
                    ImGui.SetNextWindowSize(new System.Numerics.Vector2(400, 100));
                    if (ImGui.Begin(Translate("XivMediaPlayer - Initial Setup"), ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove))
                    {
                        ImGui.TextWrapped(Translate(_dependencyManager.Status));
                        if (_dependencyManager.IsDownloading)
                        {
                            ImGui.ProgressBar(_dependencyManager.DownloadProgress, new System.Numerics.Vector2(-1, 0));
                        }
                        if (_dependencyManager.HasError)
                        {
                            ImGui.TextColored(new System.Numerics.Vector4(1, 0, 0, 1), Translate(_dependencyManager.ErrorMessage));
                            if (ImGui.Button(Translate("Retry Download")))
                            {
                                _ = _dependencyManager.DownloadDependenciesAsync();
                            }
                        }
                        ImGui.End();
                    }
                }
                return;
            }

            if (_uiCapture != null)
            {
                _uiCapture.CaptureFrame();
            }

            // Decode frames every tick, even if the video window is closed,
            // so the world-space renderer always has fresh textures.
            _videoWindow.UpdateFrame();

            _windowSystem.Draw();

            // World-space video rendering
            if (_worldRenderer?.IsActive == true && _clientState.IsLoggedIn)
            {
                // Only read depth to CPU when occlusion is on
                if (_depthCapture != null)
                    _depthCapture.ReadDepthEnabled = _worldRenderer.UseDepthOcclusion;

                _videoWindow.GetCurrentVideoTexture(out IntPtr videoSrv, out int videoWidth, out int videoHeight, out int videoTrueWidth, out int videoTrueHeight);
                if (videoSrv != IntPtr.Zero)
                {
                    // Get camera info for depth occlusion
                    System.Numerics.Vector3? cameraPos = null;
                    System.Numerics.Vector3? cameraForward = null;
                    float nearPlane = 0.1f, farPlane = 10000f;
                    float fovY = 0.785f;
                    float aspectRatio = 1.0f;
                    System.Numerics.Vector3 cameraRight = System.Numerics.Vector3.UnitX;
                    System.Numerics.Vector3 cameraUp = System.Numerics.Vector3.UnitY;
                    System.Numerics.Matrix4x4? viewProjMatrix = null;

                    if (_camera != null)
                    {
                        try
                        {
                            var sceneCamera = _camera->CameraBase.SceneCamera;
                            var rawView = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->ViewMatrix : sceneCamera.ViewMatrix;
                            if (sceneCamera.RenderCamera == null) return;
                            var rawProj = sceneCamera.RenderCamera->ProjectionMatrix;
                            var view = System.Runtime.CompilerServices.Unsafe.As<
                              FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                              System.Numerics.Matrix4x4>(ref rawView);

                            // FFXIV matrices often leave the 4th column uninitialized or zeroed.
                            // We MUST set M44 = 1.0 to make it an affine transformation matrix so Invert() works!
                            view.M14 = 0f;
                            view.M24 = 0f;
                            view.M34 = 0f;
                            view.M44 = 1f;

                            System.Numerics.Matrix4x4.Invert(view, out var invView);
                            
                            cameraPos = invView.Translation;
                            cameraRight = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M11, invView.M12, invView.M13));
                            cameraUp = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M21, invView.M22, invView.M23));
                            cameraForward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M31, invView.M32, invView.M33));
                            
                            fovY = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->FoV : 0.785f;
                            aspectRatio = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->AspectRatio : 1.0f;
                            nearPlane = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->NearPlane : 0.1f;
                            farPlane = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->FarPlane : 10000f;
                            
                            var proj = System.Runtime.CompilerServices.Unsafe.As<
                              FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                              System.Numerics.Matrix4x4>(ref rawProj);
                            
                            viewProjMatrix = view * proj;
                        }
                        catch { }
                    }

                    System.Numerics.Vector2? hoverUV = null;
                    float progress = 0f;
                    float bufferProgress = 1f;
                    bool isPlaying = false;
                    float playbackState = 0.0f; // 0 = Stop, 1 = Play, 2 = Paused

                    var activeStream = _mediaManager?.ActiveStream;
                    if (activeStream != null)
                    {
                        long durationMs = GetPlaybackDurationMs();
                        if (durationMs > 0)
                        {
                            GetSeekBarProgress(out progress, out bufferProgress);
                        }

                        isPlaying = activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing;
                        if (isPlaying) playbackState = 1.0f;
                        else if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Paused) playbackState = 2.0f;
                    }
                    if (_mediaManager != null && _mediaManager.IsFFmpegPlaying)
                    {
                        isPlaying = true;
                        playbackState = 1.0f;
                    }
                    
                    if (_isResolvingMedia) {
                        playbackState = 1.0f;
                    }
                    
                    if (isPlaying || _isResolvingMedia) {
                        _screensaverTimer.Stop();
                        _screensaverTimer.Reset();
                    } else {
                        if (!_screensaverTimer.IsRunning) _screensaverTimer.Start();
                    }
                    
                    float showScreensaver = (_screensaverTimer.ElapsedMilliseconds > 5000
                        || (_isResolvingMedia && !isPlaying)
                        || IsMediaLoading) ? 1.0f : 0.0f;
                    float timeSeconds = (float)(((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + _serverTimeOffsetMs) / 1000.0) % 864000.0);

                    var mousePos = ImGui.GetIO().MousePos;
                    var (tl, tr, br, bl) = _worldRenderer.Transform.Corners;

                    bool vTL = _gameGui.WorldToScreen(tl, out var sTL);
                    bool vTR = _gameGui.WorldToScreen(tr, out var sTR);
                    bool vBR = _gameGui.WorldToScreen(br, out var sBR);
                    bool vBL = _gameGui.WorldToScreen(bl, out var sBL);

                    System.Numerics.Vector2 uv = new System.Numerics.Vector2(-1, -1);
                    if (cameraPos.HasValue && cameraForward.HasValue)
                    {
                        var viewport = ImGui.GetMainViewport();
                        float ndcX = ((mousePos.X - viewport.Pos.X) / viewport.Size.X) * 2f - 1f;
                        float ndcY = -(((mousePos.Y - viewport.Pos.Y) / viewport.Size.Y) * 2f - 1f);

                        float fovDist = 1.0f / (float)Math.Tan(fovY * 0.5f);
                        var rayOrigin = cameraPos.Value;
                        var rayDir = System.Numerics.Vector3.Normalize(ndcX * aspectRatio * cameraRight + ndcY * cameraUp - fovDist * cameraForward.Value);

                        var tvRight = tr - tl;
                        var tvDown = bl - tl;
                        var tvNormal = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(tvRight, tvDown));

                        float denom = System.Numerics.Vector3.Dot(tvNormal, rayDir);
                        if (Math.Abs(denom) > 1e-6f)
                        {
                            float t = System.Numerics.Vector3.Dot(tl - rayOrigin, tvNormal) / denom;
                            if (t > 0f)
                            {
                                var hitPoint = rayOrigin + rayDir * t;
                                var d = hitPoint - tl;
                                float u = System.Numerics.Vector3.Dot(d, tvRight) / tvRight.LengthSquared();
                                float v = System.Numerics.Vector3.Dot(d, tvDown) / tvDown.LengthSquared();
                                uv = new System.Numerics.Vector2(u, v);
                            }
                        }
                    }
                    else if (vTL || vTR || vBR || vBL)
                    {
                        uv = MathUtils.InverseBilinear(mousePos, sTL, sTR, sBR, sBL);
                    }

                    // UI Alpha Mask Check
                    if (!_config.DisableUIBlockDetection && _uiCapture != null && uv.X >= 0 && uv.Y >= 0)
                    {
                        var io = ImGui.GetIO();
                        float scaleX = io.DisplaySize.X > 0 ? _uiCapture.Width / io.DisplaySize.X : 1.0f;
                        float scaleY = io.DisplaySize.Y > 0 ? _uiCapture.Height / io.DisplaySize.Y : 1.0f;
                        int physX = (int)(mousePos.X * scaleX);
                        int physY = (int)(mousePos.Y * scaleY);

                        IntPtr unk68Ptr = SceneColorProbe.GetToneAdjustSourceSrvPtr();
                        
                        bool isOccluding = _uiCapture.IsPixelOccluding(physX, physY, unk68Ptr, _depthCapture, useDifferenceFallback);
                        if (isOccluding)
                        {
                            uv = new System.Numerics.Vector2(-1, -1);
                        }
                    }

                    // We must calculate mouse state unconditionally every frame so that holding the mouse
                    // and dragging it OVER the window doesn't falsely trigger a "Click" event!
                    bool hasFocus = GetForegroundWindow() == _mainWindowHandle;
                    bool isLeftMousePressed = hasFocus && (GetAsyncKeyState(0x01) & 0x8000) != 0; // VK_LBUTTON
                    bool isRightMousePressed = hasFocus && (GetAsyncKeyState(0x02) & 0x8000) != 0; // VK_RBUTTON
                    bool isMouseClicked = isLeftMousePressed && !_wasLeftMousePressed;
                    bool isMouseReleased = !isLeftMousePressed && _wasLeftMousePressed;
                    _wasLeftMousePressed = isLeftMousePressed;

                    bool isOnTv = uv.X >= 0 && uv.X <= 1 && uv.Y >= 0 && uv.Y <= 1;
                    if (isMouseClicked)
                    {
                        _clickStartedOnTv = isOnTv;
                    }

                    if (_clickStartedOnTv && isLeftMousePressed)
                    {
                        ImGui.GetIO().WantCaptureMouse = true;
                    }

                    if (isOnTv)
                    {
                        hoverUV = uv;
                        float scroll = ImGui.GetIO().MouseWheel;
                        if (Math.Abs(scroll) > 0.01f)
                        {
                            ImGui.GetIO().WantCaptureMouse = true;
                        }
                        
                        // Pass native mouse state to Emulation Server if active
                        SendEmulationMouseState(uv.X, uv.Y, scroll, isLeftMousePressed, isRightMousePressed);

                        if (_currentStreamer != "Emulation" && _currentStreamer != "Camera")
                        {
                            if (isMouseReleased && _clickStartedOnTv)
                            {
                            // Handle Volume Slider Drag
                            if (uv.Y > 0.95f && uv.Y < 0.97f && uv.X > 0.32f && uv.X < 0.60f)
                            {
                                if (_mediaManager != null)
                                {
                                    float volProgress = (uv.X - 0.32f) / 0.28f;
                                    _mediaManager.LiveStreamVolume = Math.Clamp(volProgress * 3f, 0f, 3f);
                                    _config.LivestreamVolume = _mediaManager.LiveStreamVolume;
                                }
                            }
                            
                            // Seek Bar Drag (0.32 - 0.60, matches drawn bar at y 0.90-0.92)
                            if (uv.Y > 0.90f && uv.Y < 0.92f && uv.X >= 0.32f && uv.X <= 0.60f)
                            {
                                if (activeStream != null && !BlocksYouTubeUserSeek())
                                {
                                    float seekProgress = (uv.X - 0.32f) / 0.28f;
                                    long durationMs = GetPlaybackDurationMs();
                                    if (durationMs > 0)
                                    {
                                        SeekToMs((long)(seekProgress * durationMs));
                                        _isLocalDj = true;
                                    }
                                }
                            }
                        }

                        if (isMouseReleased)
                        {
                            _config.Save(); // Save volume if it changed
                        }

                        if (isMouseReleased && _clickStartedOnTv)
                        {
                            _pluginLog.Information($"Media Control Clicked at UV: {uv.X:F2}, {uv.Y:F2}");

                            if (_isQueueMenuOpen)
                            {
                                var action = _queueMenuTextureManager?.GetActionAtUV(uv.X, uv.Y);
                                if (action == "close") {
                                    _isQueueMenuOpen = false;
                                } else if (action == "clear") {
                                    _mediaQueue.Clear();
                                    _ = PushMediaToServerAsync(false);
                                    _queueMenuTextureManager?.UpdateQueue(_mediaQueue,
                    string.IsNullOrEmpty(_currentMediaTitle) ? Translate("Nothing Playing") : _currentMediaTitle);
                                } else if (action == "paste") {
                                    Thread thread = new Thread(() =>
                                    {
                                        string clip = "";
                                        for (int i = 0; i < 5; i++)
                                        {
                                            try { clip = System.Windows.Forms.Clipboard.GetText(); if (!string.IsNullOrEmpty(clip)) break; } catch { }
                                            Thread.Sleep(50);
                                        }
                                        if (!string.IsNullOrEmpty(clip))
                                        {
                                            EnqueueFrameworkAction(() =>
                                            {
                                                _mediaQueue.Enqueue(clip);
                                                PrintChatFormat("[Media Player] Queued ({0}): {1}", _mediaQueue.Count, clip);
                                                if (_mediaManager?.ActiveStream == null || _mediaManager.ActiveStream.PlaybackState == NAudio.Wave.PlaybackState.Stopped)
                                                {
                                                    if (_playerObject != null) PlayRouted(_mediaQueue.Dequeue(), CurrentAudioSource);
                                                }
                                                else _ = PushMediaToServerAsync(false);
                                                _queueMenuTextureManager?.UpdateQueue(_mediaQueue,
                    string.IsNullOrEmpty(_currentMediaTitle) ? Translate("Nothing Playing") : _currentMediaTitle);
                                            });
                                        }
                                        else
                                        {
                                            EnqueueFrameworkAction(() => PrintErrorChat("[Media Player] Failed to read clipboard or clipboard was empty."));
                                        }
                                    });
                                    thread.SetApartmentState(ApartmentState.STA);
                                    thread.Start();
                                } else if (action != null && action.StartsWith("remove:")) {
                                    if (int.TryParse(action.Split(':')[1], out int idx)) {
                                        var list = _mediaQueue.ToList();
                                        if (idx >= 0 && idx < list.Count) {
                                            list.RemoveAt(idx);
                                            _mediaQueue = new Queue<string>(list);
                                            _ = PushMediaToServerAsync(false);
                                            _queueMenuTextureManager?.UpdateQueue(_mediaQueue,
                    string.IsNullOrEmpty(_currentMediaTitle) ? Translate("Nothing Playing") : _currentMediaTitle);
                                        }
                                    }
                                }
                                return; // Handled
                            }

                            // Handle Transport Controls (Y between 0.85 and 0.95)
                            if (uv.Y > 0.85f && uv.Y < 0.95f)
                            {
                                // Prev (0.02 - 0.06)
                                if (uv.X >= 0.02f && uv.X <= 0.06f)
                                {
                                    PlayPrevious();
                                }
                                // Rewind (0.07 - 0.11)
                                else if (uv.X >= 0.07f && uv.X <= 0.11f)
                                {
                                    SeekRelative(-_config.SeekIncrementSeconds);
                                }
                                // Play/Pause (0.12 - 0.16)
                                else if (uv.X >= 0.12f && uv.X <= 0.16f)
                                {
                                    TogglePlayPause();
                                }
                                // Fast Forward (0.17 - 0.21)
                                else if (uv.X >= 0.17f && uv.X <= 0.21f)
                                {
                                    SeekRelative(_config.SeekIncrementSeconds);
                                }
                                // Next (0.22 - 0.26)
                                else if (uv.X >= 0.22f && uv.X <= 0.26f)
                                {
                                    PlayNext();
                                }
                                // Stop (0.27 - 0.31)
                                else if (uv.X >= 0.27f && uv.X <= 0.31f)
                                {
                                    Stop();
                                }

                                // Loop (0.62 - 0.66)
                                else if (uv.X >= 0.62f && uv.X <= 0.66f)
                                {
                                    _config.LoopEnabled = !_config.LoopEnabled;
                                    _config.Save();
                                    if (_config.LoopEnabled)
                                        PrintChat("[Media Player] Loop: ON");
                                    else
                                        PrintChat("[Media Player] Loop: OFF");
                                }
                                // Shuffle (0.68 - 0.72)
                                else if (uv.X >= 0.68f && uv.X <= 0.72f)
                                {
                                    _config.ShuffleEnabled = !_config.ShuffleEnabled;
                                    _config.Save();
                                    if (_config.ShuffleEnabled)
                                        PrintChat("[Media Player] Shuffle: ON");
                                    else
                                        PrintChat("[Media Player] Shuffle: OFF");
                                }
                                // Refresh (0.74 - 0.78)
                                else if (uv.X >= 0.74f && uv.X <= 0.78f)
                                {
                                    RequestRefreshCurrentMedia();
                                }
                                // Lock (0.80 - 0.84)
                                else if (uv.X >= 0.80f && uv.X <= 0.84f)
                                {
                                    if (CurrentTvPlacement != null && CurrentTvPlacement.OwnerId == _config.OwnerId)
                                    {
                                        CurrentTvPlacement.IsLocked = !CurrentTvPlacement.IsLocked;
                                        if (!string.IsNullOrEmpty(LocationKey))
                                        {
                                            _screenSettingsWindow.RegisterTvAsync(LocationKey);
                                            if (CurrentTvPlacement.IsLocked)
                                                PrintChat("[Media Player] TV is now Locked.");
                                            else
                                                PrintChat("[Media Player] TV is now Unlocked.");
                                        }
                                    }
                                    else if (CurrentTvPlacement == null)
                                    {
                                        CurrentTvPlacement = new Networking.Models.TvPlacement { OwnerId = _config.OwnerId, IsLocked = false };
                                        if (!string.IsNullOrEmpty(LocationKey))
                                        {
                                            _screenSettingsWindow.RegisterTvAsync(LocationKey);
                                        }
                                        PrintChat("[Media Player] TV registered and Unlocked.");
                                    }
                                    else { PrintChat("[Media Player] You do not own this TV."); }
                                }
                                // Paste (0.85 - 0.89)
                                else if (uv.X >= 0.85f && uv.X <= 0.89f)
                                {
                                    if (_playerObject != null)
                                    {
                                        PrintVerbose("[Media Player] Reading clipboard...");
                                        Thread thread = new Thread(() =>
                                        {
                                            string clip = "";
                                            for (int i = 0; i < 5; i++)
                                            {
                                                try { clip = System.Windows.Forms.Clipboard.GetText(); if (!string.IsNullOrEmpty(clip)) break; } catch { }
                                                Thread.Sleep(50);
                                            }
                                            if (!string.IsNullOrEmpty(clip))
                                            {
                                                EnqueueFrameworkAction(() =>
                                                {
                                                    PrintVerbose("[Media Player] Loading URL from clipboard...");
                                                    PlayRouted(clip, CurrentAudioSource);
                                                });
                                            }
                                            else
                                            {
                                                EnqueueFrameworkAction(() => PrintErrorChat("[Media Player] Failed to read clipboard or clipboard was empty."));
                                            }
                                        });
                                        thread.SetApartmentState(ApartmentState.STA);
                                        thread.Start();
                                    }
                                }
                                // Queue (0.90 - 0.94)
                                else if (uv.X >= 0.90f && uv.X <= 0.94f)
                                {
                                    EnqueueFrameworkAction(() =>
                                    {
                                        _isQueueMenuOpen = !_isQueueMenuOpen;
                                        if (_isQueueMenuOpen)
                                        {
                                            _queueMenuTextureManager?.UpdateQueue(_mediaQueue,
                    string.IsNullOrEmpty(_currentMediaTitle) ? Translate("Nothing Playing") : _currentMediaTitle);
                                        }
                                    });
                                }
                                // Kill/Stop (0.95 - 0.99)
                                else if (uv.X >= 0.95f && uv.X <= 0.99f)
                                {
                                    Stop();
                                }
                            }
                            // History Top Left (0.02 - 0.08, 0.04 - 0.12)
                            else if (uv.Y >= 0.04f && uv.Y <= 0.12f && uv.X >= 0.02f && uv.X <= 0.08f)
                            {
                                EnqueueFrameworkAction(() =>
                                {
                                    _isHistoryMenuOpen = !_isHistoryMenuOpen;
                                    if (_isHistoryMenuOpen)
                                    {
                                        _historyMenuTextureManager?.UpdateHistory(_config.WatchHistory);
                                    }
                                });
                            }
                            // DMCA Top Right (0.92 - 0.98, 0.04 - 0.12)
                            else if (uv.Y >= 0.04f && uv.Y <= 0.12f && uv.X >= 0.92f && uv.X <= 0.98f)
                            {
                                string url = _lastStreamURL;
                                if (!string.IsNullOrEmpty(url)) {
                                    string domain = Translate("the site administrator");
                                    try {
                                        Uri uri = new Uri(url);
                                        domain = uri.Host;
                                        PrintChat("[Media Player] Opening DMCA Information...");
                                    } catch { }
                                    
                                    string dmcaText = FormatDmcaClipboardText(url, domain);
                                    ImGui.SetClipboardText(dmcaText);
                                    PrintChat("[Media Player] DMCA contact info and URL copied to clipboard.");
                                } else {
                                    PrintErrorChat("[Media Player] No active media URL to copy.");
                                }
                            }
                            else if (_isHistoryMenuOpen)
                            {
                                // We clicked inside the TV bounds while the history menu was open.
                                var clickedEntry = _historyMenuTextureManager?.GetItemAtUV(uv.X, uv.Y);
                                if (clickedEntry != null)
                                {
                                    // Clicked a history item! Close menu and play it.
                                    _isHistoryMenuOpen = false;
                                    
                                    // Same routing logic as MediaBrowserWindow
                                    if (YtDlpManager.IsUrlSupported(clickedEntry.Url) && _ytDlpManager.IsAvailable())
                                    {
                                        PlayRouted(clickedEntry.Url, CurrentAudioSource, (int)clickedEntry.TimecodeMs);
                                    }
                                    else
                                    {
                                        TuneIntoStream(clickedEntry.Url, CurrentAudioSource, (int)clickedEntry.TimecodeMs);
                                    }
                                }
                                else
                                {
                                    // Clicked outside any items, close the menu
                                    _isHistoryMenuOpen = false;
                                }
                            }
                        }
                        }
                    }

                    // Update dynamic 3D text texture
                    if (_titleTextureManager != null)
                    {
                        if (IsMediaLoading)
                        {
                            _titleTextureManager.UpdateLoadingOverlay(MediaLoadingMessage, MediaLoadingPulse, _translationRevision);
                        }
                        else
                        {
                            _titleTextureManager.UpdateText(_currentMediaTitle, _currentStreamer);
                        }
                    }

                    bool isLocked = CurrentTvPlacement?.IsLocked ?? true;
                    float lockState = isLocked ? 1.0f : 0.0f;
                    if (_currentStreamer == "Emulation") {
                        lockState = -1.0f;
                    }
                    float volume = _mediaManager != null ? _mediaManager.LiveStreamVolume : 1f;
                    
                    IntPtr srvPtr = _isQueueMenuOpen 
                        ? (_queueMenuTextureManager?.TextureHandle ?? IntPtr.Zero) 
                        : _isHistoryMenuOpen 
                        ? (_historyMenuTextureManager?.TextureHandle ?? IntPtr.Zero) 
                        : (_titleTextureManager?.TextureHandle ?? IntPtr.Zero);

                    if (_currentStreamer == "Emulation") {
                        srvPtr = IntPtr.Zero;
                    }

                    _worldRenderer.EnableGlow = _config.DepthOcclusionEnabled && _config.TvGlowEnabled;
                    _worldRenderer.EnableUiCulling = _config.EnableUiCulling;
                    
                    // useDifferenceFallback is already calculated above when checking UI occlusion,
                    // but we re-calculate it here in case the logic above was skipped.
                    // (Actually we calculated it at the top of OnDraw, so we don't need to do it again here.)
                    
                    var mainViewport = ImGui.GetMainViewport();

                    if (IsMediaLoading)
                    {
                        hoverUV = new System.Numerics.Vector2(0.5f, 0.5f);
                    }
                    
                    _worldRenderer.Render(videoSrv, videoWidth, videoHeight, videoTrueWidth, videoTrueHeight, _depthCapture, 
                        _prevCameraPos ?? cameraPos, _prevCameraForward ?? cameraForward, _prevCameraRight ?? cameraRight, _prevCameraUp ?? cameraUp, 
                        fovY, aspectRatio, _uiCapture, nearPlane, farPlane, hoverUV, progress, bufferProgress, playbackState, lockState, volume, srvPtr, _config.LoopEnabled, _config.ShuffleEnabled, timeSeconds, showScreensaver, useDifferenceFallback: useDifferenceFallback, 
                        viewProjMatrix: _prevViewProjMatrix ?? viewProjMatrix, viewportPos: mainViewport.Pos, viewportSize: mainViewport.Size, uiBlendThreshold: _config.UIBlendThreshold);
                        
                    _prevCameraPos = cameraPos;
                    _prevCameraForward = cameraForward;
                    _prevCameraRight = cameraRight;
                    _prevCameraUp = cameraUp;
                    _prevViewProjMatrix = viewProjMatrix;
                }
                
                // Draw floating Emulation Controller UI
                if (_currentStreamer == "Emulation" && _worldRenderer.Transform != null) {
                    var (tl, tr, br, bl) = _worldRenderer.Transform.Corners;
                    if (_gameGui.WorldToScreen(tr, out var sTR)) {
                        ImGui.SetNextWindowPos(new System.Numerics.Vector2(sTR.X + 20, sTR.Y));
                        ImGui.SetNextWindowBgAlpha(0.8f);
                        if (ImGui.Begin(Translate("Emulation Controllers"), ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove)) {
                            ImGui.Text(Translate("Controller Slot"));
                            ImGui.Separator();
                            for (byte i = 0; i < 4; i++) {
                                if (ImGui.Selectable(string.Format(Translate("Player {0}"), i + 1), _controllerService?.PlayerSlot == i)) {
                                    if (_controllerService != null) _controllerService.PlayerSlot = i;
                                }
                            }
                            if (ImGui.Selectable(Translate("None"), _controllerService?.PlayerSlot == 255)) {
                                if (_controllerService != null) _controllerService.PlayerSlot = 255;
                            }
                            ImGui.End();
                        }
                    }
                }
            }

            DrawOutdoorGridDebug();
        }

        private unsafe void DrawOutdoorGridDebug()
        {
            if (!_config.ShowOutdoorGridDebug) return;

            var playerPos = GetLocalPlayer()?.Position;
            if (playerPos == null) return;

            var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
            if (housingMgr != null && housingMgr->IsInside()) return;

            var drawList = ImGui.GetBackgroundDrawList();
            uint color = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0, 1, 0, 0.5f));
            float thickness = 2.0f;

            int currentGridX = (int)Math.Floor(playerPos.Value.X / 60.0f);
            int currentGridZ = (int)Math.Floor(playerPos.Value.Z / 60.0f);

            void DrawLineSegmented(System.Numerics.Vector3 pStart, System.Numerics.Vector3 pEnd)
            {
                int segments = 10;
                for (int i = 0; i < segments; i++)
                {
                    float t1 = i / (float)segments;
                    float t2 = (i + 1) / (float)segments;
                    var pA = System.Numerics.Vector3.Lerp(pStart, pEnd, t1);
                    var pB = System.Numerics.Vector3.Lerp(pStart, pEnd, t2);
                    if (_gameGui.WorldToScreen(pA, out var spA) && _gameGui.WorldToScreen(pB, out var spB))
                    {
                        drawList.AddLine(spA, spB, color, thickness);
                    }
                }
            }

            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    float startX = (currentGridX + dx) * 60.0f;
                    float startZ = (currentGridZ + dz) * 60.0f;
                    float y = playerPos.Value.Y;

                    var p1 = new System.Numerics.Vector3(startX, y, startZ);
                    var p2 = new System.Numerics.Vector3(startX + 60f, y, startZ);
                    var p3 = new System.Numerics.Vector3(startX + 60f, y, startZ + 60f);
                    var p4 = new System.Numerics.Vector3(startX, y, startZ + 60f);

                    DrawLineSegmented(p1, p2);
                    DrawLineSegmented(p2, p3);
                    DrawLineSegmented(p3, p4);
                    DrawLineSegmented(p4, p1);

                    var center = new System.Numerics.Vector3(startX + 25f, y, startZ + 25f);
                    if (_gameGui.WorldToScreen(center, out var sCenter))
                    {
                        string text = $"Grid {currentGridX + dx}, {currentGridZ + dz}";
                        var textSize = ImGui.CalcTextSize(text);
                        sCenter.X -= textSize.X / 2;
                        drawList.AddText(sCenter, color, text);
                    }
                }
            }
        }

        private System.Numerics.Matrix4x4? _lastStabilizedVP;

        /// <summary>
        /// Computes the game's combined View * Projection matrix from the active camera.
        /// Reads both matrices directly from FFXIV to guarantee perfect sync.
        /// </summary>
        private unsafe System.Numerics.Matrix4x4? GetViewProjectionMatrix()
        {
            if (_camera == null) return null;

            try
            {
                var sceneCamera = _camera->CameraBase.SceneCamera;

                var rawView = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->ViewMatrix : sceneCamera.ViewMatrix;
                var view = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawView);

                if (sceneCamera.RenderCamera == null) return null;

                var rawProj = sceneCamera.RenderCamera->ProjectionMatrix;
                var proj = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawProj);

                var vp = System.Numerics.Matrix4x4.Multiply(view, proj);

                if (_lastStabilizedVP.HasValue)
                {
                    float diff = 0;
                    diff += Math.Abs(vp.M11 - _lastStabilizedVP.Value.M11);
                    diff += Math.Abs(vp.M12 - _lastStabilizedVP.Value.M12);
                    diff += Math.Abs(vp.M13 - _lastStabilizedVP.Value.M13);
                    diff += Math.Abs(vp.M21 - _lastStabilizedVP.Value.M21);
                    diff += Math.Abs(vp.M22 - _lastStabilizedVP.Value.M22);
                    diff += Math.Abs(vp.M23 - _lastStabilizedVP.Value.M23);
                    diff += Math.Abs(vp.M31 - _lastStabilizedVP.Value.M31);
                    diff += Math.Abs(vp.M32 - _lastStabilizedVP.Value.M32);
                    diff += Math.Abs(vp.M33 - _lastStabilizedVP.Value.M33);
                    diff += Math.Abs(vp.M41 - _lastStabilizedVP.Value.M41);
                    diff += Math.Abs(vp.M42 - _lastStabilizedVP.Value.M42);
                    diff += Math.Abs(vp.M43 - _lastStabilizedVP.Value.M43);

                    // Stabilize the combined ViewProjection matrix to filter out both
                    // camera float drift AND TAA/DLSS/FSR projection sub-pixel jitter.
                    if (diff < 0.002f)
                    {
                        vp = _lastStabilizedVP.Value;
                    }
                }
                _lastStabilizedVP = vp;

                return vp;
            }
            catch
            {
                return null;
            }
        }

        private void OnOpenConfig()
        {
            _settingsWindow.Toggle();
        }

        public void ToggleConfigUi()
        {
            _settingsWindow.Toggle();
        }

        public void HandleOutdoorSettingToggled()
        {
            var key = GetLocationKey();
            if (string.IsNullOrEmpty(key)) return;

            if (key.StartsWith("zone_"))
            {
                if (!_config.EnableOutdoorPublicScreens)
                {
                    _worldRenderer.Transform.Enabled = false;
                    _mediaManager?.StopStream();
                    _lastStreamURL = "";
                    _currentMediaOwnerId = "";
                    _isLocalDj = false;
                    _lastStreamObject = null;
                }
                else
                {
                    RestoreScreenForCurrentLocation();
                    RestoreMediaForCurrentLocation();
                    _ = FetchServerDataForCurrentLocationAsync();
                }
            }
        }

        #endregion

        #region Utilities

        private static bool IsHlsStreamUrl(string url) => YtDlpManager.IsHlsStreamUrl(url);

        private static string CleanUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();
            if (url.StartsWith("\"") && url.EndsWith("\"") && url.Length >= 2)
            {
                url = url.Substring(1, url.Length - 2);
            }
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                url = uri.LocalPath;
            }

            // Un-proxy local URLs (e.g. VRCVideoCacher) to ensure room sync sends the real underlying URL
            if (url.StartsWith("http://127.0.0.1") && url.Contains("target"))
            {
                int targetIndex = url.IndexOf("target");
                if (targetIndex > 0)
                {
                    string b64 = url.Substring(targetIndex + 6);
                    try
                    {
                        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                        string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        if (decoded.StartsWith("http")) url = decoded;
                    }
                    catch { }
                }
            }

            // Handle stream.m3u8?sid= proxy URLs by recovering the original URL from the active StreamProxy session
            if (url.StartsWith("http://127.0.0.1") && url.Contains("sid="))
            {
                try
                {
                    int sidIndex = url.IndexOf("sid=");
                    if (sidIndex > 0)
                    {
                        string sid = url.Substring(sidIndex + 4);
                        int ampIndex = sid.IndexOf('&');
                        if (ampIndex > 0) sid = sid.Substring(0, ampIndex);

                        string originalUrl = MediaPlayerCore.StreamProxy.Instance.GetOriginalUrl(sid);
                        if (!string.IsNullOrEmpty(originalUrl))
                        {
                            url = originalUrl;
                        }
                    }
                }
                catch { }
            }
            // Clean YouTube tracking noise (si, feature, pp, etc.)
            if (url.Contains("youtube.com") || url.Contains("youtu.be"))
            {
                try
                {
                    var ytUri = new Uri(url);
                    if (ytUri.Host.Contains("youtu.be"))
                    {
                        // youtu.be/ID?si=noise -> just keep the path
                        url = ytUri.GetLeftPart(UriPartial.Path);
                    }
                    else if (ytUri.Host.Contains("youtube.com") && ytUri.AbsolutePath.Contains("/watch"))
                    {
                        string q = ytUri.Query;
                        if (q.StartsWith("?")) q = q.Substring(1);
                        var parts = q.Split('&');
                        var keep = new System.Collections.Generic.List<string>();
                        foreach (var part in parts)
                        {
                            if (part.StartsWith("v=") || part.StartsWith("list=") || part.StartsWith("t="))
                            {
                                keep.Add(part);
                            }
                        }
                        url = ytUri.GetLeftPart(UriPartial.Path) + (keep.Count > 0 ? "?" + string.Join("&", keep) : "");
                    }
                }
                catch { }
            }

            return url;
        }

        private static int ExtractYouTubeStartTimeMs(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return 0;
            try
            {
                var uri = new Uri(url);
                string q = uri.Query;
                if (q.StartsWith("?")) q = q.Substring(1);
                var parts = q.Split('&');
                foreach (var part in parts)
                {
                    if (part.StartsWith("t="))
                    {
                        string tVal = part.Substring(2);
                        return ParseYouTubeTime(tVal);
                    }
                }
            }
            catch { }
            return 0;
        }

        private static int ParseYouTubeTime(string tVal)
        {
            if (string.IsNullOrWhiteSpace(tVal)) return 0;
            
            if (int.TryParse(tVal, out int rawSeconds))
            {
                return rawSeconds * 1000;
            }

            int totalSeconds = 0;
            int currentNum = 0;
            foreach (char c in tVal)
            {
                if (char.IsDigit(c))
                {
                    currentNum = currentNum * 10 + (c - '0');
                }
                else
                {
                    if (c == 'h') totalSeconds += currentNum * 3600;
                    else if (c == 'm') totalSeconds += currentNum * 60;
                    else if (c == 's') totalSeconds += currentNum;
                    currentNum = 0;
                }
            }
            return totalSeconds * 1000;
        }

        private static string RemoveSpecialSymbols(string value)
        {
            return Regex.Replace(value, @"[^a-zA-Z0-9:/._\-]", "");
        }

        private void EnqueueFrameworkAction(Action action)
        {
            if (!_disposed)
            {
                _frameworkActions.Enqueue(action);
            }
        }

        #endregion

        #region IDisposable

        private unsafe void HandleScreenCommand(string[] args)
        {
            if (args.Length < 2)
            {
                PrintChat(GetScreenCommandHelpText());
                return;
            }

            switch (args[1].ToLower())
            {
                case "place":
                    PlaceScreenAtCamera();
                    break;

                case "move":
                    if (args.Length >= 5 &&
                      float.TryParse(args[2], out float mx) &&
                      float.TryParse(args[3], out float my) &&
                      float.TryParse(args[4], out float mz))
                    {
                        _worldRenderer.MoveBy(new System.Numerics.Vector3(mx, my, mz));
                        var pos = _worldRenderer.Transform.Position;
                        PrintChatFormat("[Media Player] Screen moved to ({0:F1}, {1:F1}, {2:F1})", pos.X, pos.Y, pos.Z);
                    }
                    else
                    {
                        PrintErrorChat("[Media Player] Usage: /media screen move <x> <y> <z>");
                    }
                    break;

                case "rotate":
                    if (args.Length >= 3 && float.TryParse(args[2], out float yaw))
                    {
                        float pitch = args.Length >= 4 && float.TryParse(args[3], out float p) ? p : 0;
                        _worldRenderer.SetRotation(yaw, pitch);
                        PrintChatFormat("[Media Player] Screen rotation: yaw={0:F0}° pitch={1:F0}°", yaw, pitch);
                    }
                    else
                    {
                        PrintErrorChat("[Media Player] Usage: /media screen rotate <yaw> [pitch]");
                    }
                    break;

                case "scale":
                    if (args.Length >= 4 &&
                      float.TryParse(args[2], out float sw) &&
                      float.TryParse(args[3], out float sh))
                    {
                        _worldRenderer.SetScale(sw, sh);
                        PrintChatFormat("[Media Player] Screen size: {0:F1} x {1:F1} world units", sw, sh);
                    }
                    else
                    {
                        PrintErrorChat("[Media Player] Usage: /media screen scale <width> <height>");
                    }
                    break;

                case "reset":
                    _worldRenderer.Reset();
                    PrintChat("[Media Player] Screen returned to overlay mode.");
                    break;

                case "save":
                    _config.WorldScreen = _worldRenderer.Transform.Clone();
                    SaveScreenForCurrentLocation();
                    _config.Save();
                    var locKey = GetLocationKey();
                    PrintChatFormat("[Media Player] Screen placement saved for {0}.", locKey);
                    break;

                default:
                    PrintErrorChatFormat("[Media Player] Unknown screen command: {0}", args[1]);
                    break;
            }
        }

        private unsafe void PlaceScreenAtCamera()
        {
            if (_camera != null)
            {
                var sceneCamera = _camera->CameraBase.SceneCamera;
                var rawView = sceneCamera.RenderCamera != null ? sceneCamera.RenderCamera->ViewMatrix : sceneCamera.ViewMatrix;
                
                var viewMatrix = System.Runtime.CompilerServices.Unsafe.As<
                  FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4,
                  System.Numerics.Matrix4x4>(ref rawView);
                  
                viewMatrix.M14 = 0f;
                viewMatrix.M24 = 0f;
                viewMatrix.M34 = 0f;
                viewMatrix.M44 = 1f;
                
                System.Numerics.Matrix4x4.Invert(viewMatrix, out var invView);
                var camPos = invView.Translation;
                var forward = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(invView.M31, invView.M32, invView.M33));
                
                var screenPos = camPos - forward * 5.0f;
                _worldRenderer.PlaceAt(screenPos, camPos);
                PrintChatFormat("[Media Player] Screen placed at ({0:F1}, {1:F1}, {2:F1})", screenPos.X, screenPos.Y, screenPos.Z);
            }
            else
            {
                PrintErrorChat("[Media Player] Camera not available.");
            }
        }
        #region Playback Controls

        private bool HandleYtDlpResolveFailure(Exception resolveEx, string url)
        {
            _pluginLog.Warning(resolveEx, "[yt-dlp] Failed to resolve stream URL.");
            string errorStr = resolveEx.ToString();

            if (errorStr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
            {
                EnqueueFrameworkAction(() => PrintErrorChat("[Media Player] YouTube blocked the request (bot check). Please configure cookies via VRCVideoCacher or cookies.txt to play YouTube videos!"));
                return true;
            }

            if ((url.Contains("youtube.com") || url.Contains("youtu.be"))
                && !_config.EnableSabrProxy
                && (errorStr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
                    || errorStr.Contains("Only images are available", StringComparison.OrdinalIgnoreCase)
                    || errorStr.Contains("SABR", StringComparison.OrdinalIgnoreCase)))
            {
                EnqueueFrameworkAction(() => PrintErrorChat("[Media Player] YouTube playback failed. Check cookies in settings (VRCVideoCacher), or re-enable \"YouTube SABR mode\" if you turned it off."));
                return true;
            }

            if (errorStr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)
                || errorStr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            {
                YtDlpManager.MarkUrlAsFailed(url);
            }

            return false;
        }

        private bool HandleYtDlpMetadataFailure(Exception metadataEx, string url)
        {
            string errorStr = metadataEx.ToString();

            if (errorStr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase))
            {
                EnqueueFrameworkAction(() => PrintErrorChat("[Media Player] YouTube blocked the request (bot check). Please configure cookies via VRCVideoCacher or cookies.txt to play YouTube videos!"));
                return true;
            }

            if ((url.Contains("youtube.com") || url.Contains("youtu.be"))
                && !_config.EnableSabrProxy
                && (errorStr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
                    || errorStr.Contains("Only images are available", StringComparison.OrdinalIgnoreCase)
                    || errorStr.Contains("SABR", StringComparison.OrdinalIgnoreCase)))
            {
                EnqueueFrameworkAction(() => PrintErrorChat("[Media Player] YouTube playback failed. Check cookies in settings (VRCVideoCacher), or re-enable \"YouTube SABR mode\" if you turned it off."));
                return true;
            }

            if (errorStr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)
                || errorStr.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            {
                YtDlpManager.MarkUrlAsFailed(url);
            }

            return false;
        }

        /// <summary>
        /// Seeks the current stream forward or backward by the given number of seconds.
        /// </summary>
        public void SeekRelative(int seconds)
        {
            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null) return;
            if (BlocksYouTubeUserSeek()) return;

            SeekToMs(activeStream.Time + (seconds * 1000L));
        }

        /// <summary>
        /// True when YouTube SABR is still downloading and user seek input should be ignored.
        /// </summary>
        public bool BlocksYouTubeUserSeek()
        {
            if (_lastStreamIsLive)
            {
                return false;
            }

            var activeStream = _mediaManager?.ActiveStream;
            string? mediaPath = activeStream?.SoundPath;
            if (mediaPath == null || !YtDlpManager.IsSabrLocalFile(mediaPath))
            {
                return false;
            }

            if (_ytDlpManager?.IsSabrDownloadActiveForPath(mediaPath) == true)
            {
                return true;
            }

            long metadataLength = _currentMediaDurationMs.HasValue && _currentMediaDurationMs.Value > 0
                ? (long)_currentMediaDurationMs.Value
                : 0;
            long muxedMs = MatroskaMuxFrontier.ProbeDurationMs(mediaPath);
            return _ytDlpManager?.IsSabrFileFullyBuffered(mediaPath, metadataLength, muxedMs) != true;
        }

        /// <summary>
        /// Clamps a seek target to what is currently safe to decode (SABR buffer frontier).
        /// </summary>
        public long ClampSeekTimeMs(long targetMs)
        {
            if (_lastStreamIsLive)
            {
                return 0;
            }

            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null)
            {
                return Math.Max(0, targetMs);
            }

            long maxSeek = GetMaxSeekTimeMs();
            if (maxSeek <= 0)
            {
                return Math.Max(0, targetMs);
            }

            return Math.Clamp(targetMs, 0, maxSeek);
        }

        /// <summary>
        /// Seeks to the given time, clamped to the buffered portion for in-progress SABR downloads.
        /// </summary>
        public void SeekToMs(long targetMs, bool syncToServer = true, bool userInitiated = true)
        {
            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null)
            {
                return;
            }

            if (userInitiated && BlocksYouTubeUserSeek())
            {
                return;
            }

            long clamped = ClampSeekTimeMs(targetMs);
            activeStream.Time = clamped;

            if (syncToServer && _isLocalDj)
            {
                _ = PushMediaToServerAsync(isBackgroundSync: false, overrideTimeMs: clamped);
            }
        }

        /// <summary>
        /// Normalized seek bar positions (0-1). Seekable matches GetMaxSeekTimeMs / duration.
        /// </summary>
        public void GetSeekBarProgress(out float playbackProgress, out float seekableProgress)
        {
            playbackProgress = 0f;
            seekableProgress = 1f;

            long durationMs = GetPlaybackDurationMs();
            if (durationMs <= 0)
            {
                seekableProgress = 1f;
                return;
            }

            var activeStream = _mediaManager?.ActiveStream;
            long timeMs = activeStream?.Time ?? 0;
            long seekableMs = GetMaxSeekTimeMs();

            playbackProgress = Math.Clamp(timeMs / (float)durationMs, 0f, 1f);
            seekableProgress = seekableMs > 0
                ? Math.Clamp(seekableMs / (float)durationMs, 0f, 1f)
                : 0f;

            if (BlocksYouTubeUserSeek())
            {
                seekableProgress = playbackProgress;
            }
        }

        /// <summary>
        /// Latest time the user can seek to. During SABR download this stays behind the mux frontier.
        /// </summary>
        public long GetMaxSeekTimeMs()
        {
            if (_lastStreamIsLive)
            {
                return 0;
            }

            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null)
            {
                return 0;
            }

            string? mediaPath = activeStream.SoundPath;
            bool isSabrLocal = mediaPath != null && YtDlpManager.IsSabrLocalFile(mediaPath);

            if (!isSabrLocal)
            {
                long fullDuration = GetPlaybackDurationMs();
                return fullDuration > 0 ? fullDuration : Math.Max(0, activeStream.Length);
            }

            long metadataLength = _currentMediaDurationMs.HasValue && _currentMediaDurationMs.Value > 0
                ? (long)_currentMediaDurationMs.Value
                : 0;
            EnsureSabrSeekCache(mediaPath!, metadataLength, activeStream.Length, activeStream.Time);
            return _sabrSeekCacheMaxSeekMs;
        }

        /// <summary>
        /// Fraction of total duration that can be seeked to (0-1). Matches seek clamping during SABR.
        /// </summary>
        public float GetBufferedProgress()
        {
            GetSeekBarProgress(out _, out float seekableProgress);
            return seekableProgress;
        }

        /// <summary>
        /// Returns the best-known playback duration in milliseconds (VLC length or yt-dlp metadata).
        /// </summary>
        public long GetPlaybackDurationMs()
        {
            if (_lastStreamIsLive)
            {
                return 0;
            }

            var activeStream = _mediaManager?.ActiveStream;
            long vlcLength = activeStream?.Length ?? 0;
            long metadataLength = _currentMediaDurationMs.HasValue && _currentMediaDurationMs.Value > 0
                ? (long)_currentMediaDurationMs.Value
                : 0;

            string? mediaPath = activeStream?.SoundPath;
            bool isSabrLocal = mediaPath != null && YtDlpManager.IsSabrLocalFile(mediaPath);

            if (isSabrLocal)
            {
                EnsureSabrSeekCache(mediaPath!, metadataLength, vlcLength, activeStream?.Time ?? 0);
                return _sabrSeekCacheDurationMs;
            }

            if (vlcLength > 0)
            {
                return vlcLength;
            }

            return metadataLength;
        }

        /// <summary>
        /// Completely stops playback, clears the queue, and clears the saved room resume state.
        /// </summary>
        public void Stop()
        {
            PrintChat("[Media Player] Stopping media and clearing queue...");
            _mediaManager?.StopStream();
            _mediaQueue.Clear();
            ResetStreamValues(true);

            // Clear the saved room state so it doesn't auto-resume next time we enter
            var key = CurrentTvPlacement?.LocationKey ?? GetLocationKey();
            if (!string.IsNullOrEmpty(key) && _config.RoomMediaStates.ContainsKey(key))
            {
                _config.RoomMediaStates.Remove(key);
                _config.Save();
            }
        }

        /// <summary>
        /// Toggles play/pause on the current stream.
        /// </summary>
        public void TogglePlayPause()
        {
            var activeStream = _mediaManager?.ActiveStream;
            if (activeStream == null)
            {
                return;
            }

            if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                activeStream.Pause();
                _isIntentionallyPaused = true;
            }
            else
            {
                if (activeStream.PlaybackState == NAudio.Wave.PlaybackState.Stopped && !string.IsNullOrEmpty(_lastStreamURL))
                {
                    long resumeTime = activeStream.Time;
                    _mediaManager?.StopStream();
                    if (YtDlpManager.IsUrlSupported(_lastStreamURL) && _ytDlpManager.IsAvailable())
                    {
                        PlayRouted(_lastStreamURL, CurrentAudioSource, (int)resumeTime);
                    }
                    else
                    {
                        TuneIntoStream(_lastStreamURL, CurrentAudioSource, (int)resumeTime);
                    }
                    _isIntentionallyPaused = false;
                    return;
                }

                activeStream.Resume();
                _isIntentionallyPaused = false;
            }

            if (_isLocalDj)
            {
                _ = PushMediaToServerAsync(isBackgroundSync: false);
            }
        }

        /// <summary>
        /// Returns true if the stream is currently intentionally paused by the user.
        /// </summary>
        public bool IsIntentionallyPaused => _isIntentionallyPaused;

        /// <summary>
        /// Plays the next track from the media queue.
        /// If shuffle is enabled, picks a random track from the queue.
        /// </summary>
        public void PlayNext()
        {
            if (_mediaQueue.Count == 0 || _playerObject == null) return;

            // Record history
            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                _mediaHistory.Push(_lastStreamURL);
            }

            string nextUrl;
            if (_config.ShuffleEnabled && _mediaQueue.Count > 1)
            {
                // Shuffle queue logic
                var list = _mediaQueue.ToList();
                int idx = _shuffleRandom.Next(list.Count);
                nextUrl = list[idx];
                list.RemoveAt(idx);
                _mediaQueue = new Queue<string>(list);
            }
            else
            {
                nextUrl = _mediaQueue.Dequeue();
            }

            PrintChatFormat("[Media Player] Playing next: {0}", nextUrl);
            PlayRouted(nextUrl, CurrentAudioSource);
        }

        /// <summary>
        /// Plays the previous track from the media history stack.
        /// Pushes the current track back onto the front of the queue.
        /// </summary>
        public void PlayPrevious()
        {
            if (_mediaHistory.Count == 0 || _playerObject == null) return;

            // Requeue current media
            if (!string.IsNullOrEmpty(_lastStreamURL))
            {
                var list = _mediaQueue.ToList();
                list.Insert(0, _lastStreamURL);
                _mediaQueue = new Queue<string>(list);
            }

            string prevUrl = _mediaHistory.Pop();
            PrintChatFormat("[Media Player] Playing previous: {0}", prevUrl);
            PlayRouted(prevUrl, CurrentAudioSource);
        }

        /// <summary>
        /// Toggles mute on/off. Stores the pre-mute volume and restores it when unmuting.
        /// </summary>
        public void ToggleMute()
        {
            if (_mediaManager == null) return;

            if (_isMuted)
            {
                _mediaManager.LiveStreamVolume = _preMuteVolume;
                _config.LivestreamVolume = _preMuteVolume;
                _isMuted = false;
            }
            else
            {
                _preMuteVolume = _mediaManager.LiveStreamVolume;
                _mediaManager.LiveStreamVolume = 0;
                _isMuted = true;
            }
        }

        /// <summary>
        /// Whether the media player is currently muted.
        /// </summary>
        public bool IsMuted => _isMuted;

        /// <summary>
        /// Re-resolves and replays the current media URL at the current timecode.
        /// Useful when the 2D/3D screen fails to load.
        /// </summary>
        public void RefreshCurrentMedia()
        {
            RequestRefreshCurrentMedia();
        }

        public void RequestRefreshCurrentMedia()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            EnqueueFrameworkAction(() =>
            {
                _refreshQueued = false;
                DoRefreshCurrentMedia();
            });
        }

        internal void DoRefreshCurrentMedia()
        {
            if (string.IsNullOrEmpty(_lastStreamURL) || _playerObject == null) return;

            var activeStream = _mediaManager?.ActiveStream;
            int currentTimeMs = (!_lastStreamIsLive && activeStream != null) ? (int)activeStream.Time : 0;

            PrintVerbose("[Media Player] Refreshing media...");
            _mediaManager?.StopStream();
            
            if (YtDlpManager.IsUrlSupported(_lastStreamURL) && _ytDlpManager.IsAvailable())
            {
                PlayRouted(_lastStreamURL, CurrentAudioSource, currentTimeMs);
            }
            else
            {
                TuneIntoStream(_lastStreamURL, CurrentAudioSource, currentTimeMs);
            }
        }

        /// <summary>
        /// Kills the media manager and restarts it, then resumes the current media.
        /// Recovers from locked-up VLC states.
        /// </summary>
        public void KillAndRestart()
        {
            RequestKillAndRestart();
        }

        public void RequestKillAndRestart()
        {
            UpdateWatchHistory();
            _killRestartQueued = true;
            EnqueueFrameworkAction(() =>
            {
                _killRestartQueued = false;
                DoKillAndRestart();
            });
        }

        private void DoKillAndRestart()
        {
            PrintVerbose("[Media Player] Killing media pipeline and restarting...");

            // Save what we were playing
            string savedUrl = _lastStreamURL;
            var activeStream = _mediaManager?.ActiveStream;
            int savedTimeMs = activeStream != null ? (int)activeStream.Time : 0;

            // Tear down
            _mediaManager?.Dispose();
            _mediaManager = null;
            _cefBrowserHandle?.Dispose();
            _cefBrowserHandle = null;
            _videoWindow.MediaManager = null;

            // Reinitialize
            try
            {
                InitializeMediaManager();
            }
            catch (Exception e)
            {
                _pluginLog.Warning(e, "[Media Player] Failed to reinitialize MediaManager during kill.");
                PrintErrorChat("[Media Player] Failed to restart media pipeline.");
                return;
            }

            // Resume playback
            if (!string.IsNullOrEmpty(savedUrl) && _playerObject != null)
            {
                PrintChat("[Media Player] Resuming playback...");
                PlayRouted(savedUrl, CurrentAudioSource, savedTimeMs);
            }
            else
            {
                PrintVerbose("[Media Player] Media pipeline restarted.");
            }
        }

        #endregion

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // Clean up HidSharp's hidden window to prevent RegisterClass crashes on plugin reload
                IntPtr hwnd = FindWindow("HidSharpDeviceMonitor", null);
                if (hwnd != IntPtr.Zero)
                {
                    // Send WM_CLOSE (0x0010) to let the background thread destroy it and exit cleanly
                    SendMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
                
                IntPtr hInst = GetModuleHandle("HidSharp.dll");
                if (hInst == IntPtr.Zero) hInst = GetModuleHandle(null);
                UnregisterClass("HidSharpDeviceMonitor", hInst);
            }
            catch { }

            UpdateWatchHistory();

            SaveScreenForCurrentLocation();
            SaveMediaStateForCurrentLocation();

            _framework.Update -= OnFrameworkUpdate;
            _clientState.TerritoryChanged -= OnTerritoryChanged;
            _clientState.Login -= OnLogin;
            _clientState.Logout -= OnLogout;
            _videoWindow.WindowResized -= OnVideoWindowResized;
            _chat.ChatMessage -= OnChatMessage;
            if (_mediaManager != null)
            {
                _mediaManager.OnErrorReceived -= OnMediaError;
                _mediaManager.OnNewMediaTriggered -= _mediaManager_OnNewMediaTriggered;
                _mediaManager.OnPlaybackFinished -= _mediaManager_OnPlaybackFinished;
            }

            _pluginInterface.UiBuilder.Draw -= OnDraw;
            _pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;

            _commandManager.RemoveHandler("/media");

            while (_frameworkActions.TryDequeue(out _)) { }
            _videoWindow.MarkDisposed();
            _emulationClient?.Dispose();
            _controllerService?.Dispose();
            _uiCapture?.Dispose();
            _titleTextureManager?.Dispose();
            _historyMenuTextureManager?.Dispose();
            _queueMenuTextureManager?.Dispose();
            _worldRenderer?.Dispose();
            _depthCapture?.Dispose();
            _depthPreviewWindow?.Dispose();
            ServerClient?.Dispose();
            _mediaManager?.Dispose();
            _ytDlpManager?.Dispose();
            MediaPlayerCore.StreamProxy.Instance.Dispose();
            _windowSystem?.RemoveAllWindows();
        }

        #endregion
    }
}











