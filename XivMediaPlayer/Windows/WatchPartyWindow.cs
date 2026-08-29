using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using XivMediaPlayer.Networking.Models;

namespace XivMediaPlayer.Windows
{
    internal class WatchPartyWindow : Window
    {
        private readonly Plugin _plugin;
        private List<WatchPartyEvent> _events = new List<WatchPartyEvent>();
        private bool _isLoading = false;
        private string _statusMessage = string.Empty;

        // Post Event form fields
        private string _titleInput = string.Empty;
        private string _descriptionInput = string.Empty;
        private string _bannerUrlInput = string.Empty;
        private bool _isPosting = false;

        // Banner image cache
        private readonly ConcurrentDictionary<string, IDalamudTextureWrap?> _bannerCache = new();
        private readonly ConcurrentDictionary<string, bool> _bannerLoading = new();
        private readonly HttpClient _bannerHttpClient = new HttpClient();

        // Date/Time controls (Local Time)
        private int _startYear = DateTime.Now.Year;
        private int _startMonth = DateTime.Now.Month;
        private int _startDay = DateTime.Now.Day;
        private int _startHour = DateTime.Now.Hour;
        private int _startMinute = (DateTime.Now.Minute / 15) * 15;

        private int _endYear = DateTime.Now.AddHours(2).Year;
        private int _endMonth = DateTime.Now.AddHours(2).Month;
        private int _endDay = DateTime.Now.AddHours(2).Day;
        private int _endHour = DateTime.Now.AddHours(2).Hour;
        private int _endMinute = (DateTime.Now.Minute / 15) * 15;

        // Detected location cache
        private string _detectedDc = string.Empty;
        private string _detectedWorld = string.Empty;
        private string _detectedZone = string.Empty;
        private int _detectedWard = 0;
        private int _detectedPlot = 0;
        private int _detectedRoom = 0;
        private string _detectedLocKey = string.Empty;

        public WatchPartyWindow(Plugin plugin)
            : base("Watch Party Directory", ImGuiWindowFlags.NoCollapse)
        {
            _plugin = plugin;
            Size = new Vector2(600, 500);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void OnOpen()
        {
            RefreshEvents();
            RefreshDetectedLocation();
        }

        private void RefreshDetectedLocation()
        {
            var info = _plugin.GetDetailedLocationInfo();
            _detectedDc = info.DataCenter;
            _detectedWorld = info.World;
            _detectedZone = info.HousingZone;
            _detectedWard = info.Ward;
            _detectedPlot = info.Plot;
            _detectedRoom = info.Room;
            _detectedLocKey = info.LocationKey;
        }

        private void RefreshEvents()
        {
            _isLoading = true;
            _statusMessage = "Loading events...";
            Task.Run(async () =>
            {
                var list = await _plugin.ServerClient.GetEventsAsync();
                _events = list ?? new List<WatchPartyEvent>();
                _isLoading = false;
                _statusMessage = _events.Count == 0 ? "No active watch parties found." : string.Empty;
            });
        }

        public override void Draw()
        {
            if (ImGui.BeginTabBar("WatchPartyTabs"))
            {
                if (ImGui.BeginTabItem("Browse Watch Parties"))
                {
                    DrawBrowseTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Host a Watch Party"))
                {
                    DrawHostTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private void DrawBrowseTab()
        {
            ImGui.TextUnformatted("Upcoming and active community watch parties:");
            ImGui.SameLine();
            if (ImGui.Button("Refresh") && !_isLoading)
            {
                RefreshEvents();
            }

            ImGui.Separator();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), _statusMessage);
            }

            ImGui.BeginChild("EventsListChild", new Vector2(0, 0), true, ImGuiWindowFlags.HorizontalScrollbar);

            if (_events.Count == 0 && !_isLoading)
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No active watch parties at the moment. Host one in the next tab!");
            }
            else
            {
                float availWidth = ImGui.GetContentRegionAvail().X;
                float minCardWidth = 320f;
                float spacing = 12f;
                int columns = Math.Max(1, (int)((availWidth + spacing) / (minCardWidth + spacing)));
                float cardWidth = Math.Max(minCardWidth, (availWidth - (spacing * (columns - 1))) / columns);

                for (int i = 0; i < _events.Count; i++)
                {
                    var watchEvent = _events[i];

                    if (i % columns != 0)
                    {
                        ImGui.SameLine(0, spacing);
                    }

                    ImGui.PushID(watchEvent.Id);

                    // Fixed-size styled card container for uniform grid layout
                    ImGui.BeginChild($"Card_{watchEvent.Id}", new Vector2(cardWidth, 320), true);

                    // 1. Banner Image Header (or fallback space)
                    if (!string.IsNullOrWhiteSpace(watchEvent.BannerUrl))
                    {
                        DrawBannerImage(watchEvent.BannerUrl, cardWidth - 16, 120);
                    }
                    else
                    {
                        // Stylized default banner header slot
                        ImGui.Dummy(new Vector2(0, 4));
                    }

                    ImGui.Spacing();

                    // 2. Title Header
                    ImGui.TextColored(new Vector4(0.3f, 0.85f, 1.0f, 1.0f), watchEvent.Title);

                    // 3. Location Badge
                    string locStr = $"{watchEvent.World} ({watchEvent.DataCenter}) • {watchEvent.HousingZone} W{watchEvent.Ward} P{watchEvent.Plot}";
                    if (watchEvent.Room > 0) locStr += $" R{watchEvent.Room}";
                    ImGui.TextColored(new Vector4(0.95f, 0.82f, 0.35f, 1.0f), locStr);

                    // 4. Time Badge
                    string timeStr = $"Time: {watchEvent.StartTimeUtc.ToLocalTime():g} - {watchEvent.EndTimeUtc.ToLocalTime():t}";
                    ImGui.TextColored(new Vector4(0.4f, 0.95f, 0.55f, 1.0f), timeStr);

                    ImGui.Separator();

                    // 5. Description Body
                    if (!string.IsNullOrWhiteSpace(watchEvent.Description))
                    {
                        ImGui.TextWrapped(watchEvent.Description);
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No description provided.");
                    }

                    // 6. Footer (Delete button for owner)
                    if (_plugin.DiscordAuthClient.IsLoggedIn && !string.IsNullOrEmpty(_plugin.Config.DiscordUserId))
                    {
                        if (string.Equals(watchEvent.DiscordOwnerId, _plugin.Config.DiscordUserId, StringComparison.OrdinalIgnoreCase))
                        {
                            ImGui.Spacing();
                            if (ImGui.Button("Delete Listing"))
                            {
                                var idToDelete = watchEvent.Id;
                                Task.Run(async () =>
                                {
                                    bool ok = await _plugin.ServerClient.DeleteEventAsync(idToDelete);
                                    if (ok) RefreshEvents();
                                });
                            }
                        }
                    }

                    ImGui.EndChild();
                    ImGui.PopID();
                }
            }

            ImGui.EndChild();
        }

        private void DrawHostTab()
        {
            if (!_plugin.DiscordAuthClient.IsLoggedIn)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Discord Login Required");
                ImGui.TextWrapped("You must be logged in with Discord to post and advertise your watch party.");
                if (ImGui.Button("Log in with Discord"))
                {
                    _ = _plugin.DiscordAuthClient.StartLoginFlowAsync(msg => { });
                }
                return;
            }

            ImGui.TextUnformatted("Advertise your venue's watch party to the community!");
            ImGui.Separator();

            // Detected location details
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Current Detected Location:");
            string locStr = string.IsNullOrWhiteSpace(_detectedWorld)
                ? "Not inside housing venue"
                : $"{_detectedWorld} ({_detectedDc}) • {_detectedZone} Ward {_detectedWard}, Plot {_detectedPlot}" + (_detectedRoom > 0 ? $" (Room {_detectedRoom})" : "");
            ImGui.TextColored(new Vector4(0.9f, 0.9f, 0.4f, 1.0f), locStr);

            if (ImGui.Button("Re-detect Location"))
            {
                RefreshDetectedLocation();
            }

            ImGui.Spacing();

            ImGui.InputText("Event Title", ref _titleInput, 100);
            ImGui.InputTextMultiline("Description", ref _descriptionInput, 500, new Vector2(0, 80));
            ImGui.InputText("Banner Image URL (Optional)", ref _bannerUrlInput, 300);

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "Start Date & Time (Local Time):");

            ImGui.SetNextItemWidth(70);
            ImGui.InputInt("##StartYear", ref _startYear, 0); ImGui.SameLine();
            ImGui.Text("/"); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##StartMonth", ref _startMonth, 0); ImGui.SameLine();
            ImGui.Text("/"); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##StartDay", ref _startDay, 0); ImGui.SameLine();
            ImGui.Text(" @ "); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##StartHour", ref _startHour, 0); ImGui.SameLine();
            ImGui.Text(":"); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##StartMin", ref _startMinute, 0); ImGui.SameLine();
            ImGui.Text(" (YYYY/MM/DD HH:mm)");

            // Preset buttons for Start Time
            if (ImGui.Button("Set Start to Now"))
            {
                var now = DateTime.Now;
                _startYear = now.Year; _startMonth = now.Month; _startDay = now.Day;
                _startHour = now.Hour; _startMinute = now.Minute;
            }
            ImGui.SameLine();
            if (ImGui.Button("Tomorrow 8 PM"))
            {
                var tomorrow = DateTime.Today.AddDays(1).AddHours(20);
                _startYear = tomorrow.Year; _startMonth = tomorrow.Month; _startDay = tomorrow.Day;
                _startHour = 20; _startMinute = 0;
            }

            _startMonth = Math.Clamp(_startMonth, 1, 12);
            _startDay = Math.Clamp(_startDay, 1, DateTime.DaysInMonth(Math.Clamp(_startYear, 2024, 2100), _startMonth));
            _startHour = Math.Clamp(_startHour, 0, 23);
            _startMinute = Math.Clamp(_startMinute, 0, 59);

            DateTime startLocal;
            try { startLocal = new DateTime(_startYear, _startMonth, _startDay, _startHour, _startMinute, 0, DateTimeKind.Local); }
            catch { startLocal = DateTime.Now; }

            ImGui.TextColored(new Vector4(0.6f, 1.0f, 0.6f, 1.0f), $"Starts: {startLocal:f}");

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "End Date & Time (Local Time):");

            ImGui.SetNextItemWidth(70);
            ImGui.InputInt("##EndYear", ref _endYear, 0); ImGui.SameLine();
            ImGui.Text("/"); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##EndMonth", ref _endMonth, 0); ImGui.SameLine();
            ImGui.Text("/"); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##EndDay", ref _endDay, 0); ImGui.SameLine();
            ImGui.Text(" @ "); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##EndHour", ref _endHour, 0); ImGui.SameLine();
            ImGui.Text(":"); ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            ImGui.InputInt("##EndMin", ref _endMinute, 0); ImGui.SameLine();
            ImGui.Text(" (YYYY/MM/DD HH:mm)");

            // Preset buttons for End Time
            if (ImGui.Button("+1 Hr"))
            {
                var end = startLocal.AddHours(1);
                _endYear = end.Year; _endMonth = end.Month; _endDay = end.Day;
                _endHour = end.Hour; _endMinute = end.Minute;
            }
            ImGui.SameLine();
            if (ImGui.Button("+2 Hrs"))
            {
                var end = startLocal.AddHours(2);
                _endYear = end.Year; _endMonth = end.Month; _endDay = end.Day;
                _endHour = end.Hour; _endMinute = end.Minute;
            }
            ImGui.SameLine();
            if (ImGui.Button("+4 Hrs"))
            {
                var end = startLocal.AddHours(4);
                _endYear = end.Year; _endMonth = end.Month; _endDay = end.Day;
                _endHour = end.Hour; _endMinute = end.Minute;
            }

            _endMonth = Math.Clamp(_endMonth, 1, 12);
            _endDay = Math.Clamp(_endDay, 1, DateTime.DaysInMonth(Math.Clamp(_endYear, 2024, 2100), _endMonth));
            _endHour = Math.Clamp(_endHour, 0, 23);
            _endMinute = Math.Clamp(_endMinute, 0, 59);

            DateTime endLocal;
            try { endLocal = new DateTime(_endYear, _endMonth, _endDay, _endHour, _endMinute, 0, DateTimeKind.Local); }
            catch { endLocal = startLocal.AddHours(2); }

            ImGui.TextColored(new Vector4(0.6f, 1.0f, 0.6f, 1.0f), $"Ends:   {endLocal:f}");

            ImGui.Spacing();

            if (ImGui.Button("Publish Watch Party Event") && !_isPosting)
            {
                if (string.IsNullOrWhiteSpace(_titleInput))
                {
                    _statusMessage = "Please enter an event title.";
                }
                else if (string.IsNullOrWhiteSpace(_detectedWorld))
                {
                    _statusMessage = "Please move inside your housing venue before publishing.";
                }
                else if (endLocal <= startLocal)
                {
                    _statusMessage = "End time must be after start time.";
                }
                else
                {
                    _isPosting = true;
                    _statusMessage = "Publishing event...";

                    var newEvent = new WatchPartyEvent
                    {
                        Title = _titleInput.Trim(),
                        Description = _descriptionInput.Trim(),
                        BannerUrl = _bannerUrlInput.Trim(),
                        LocationKey = _detectedLocKey,
                        DataCenter = _detectedDc,
                        World = _detectedWorld,
                        HousingZone = _detectedZone,
                        Ward = _detectedWard,
                        Plot = _detectedPlot,
                        Room = _detectedRoom,
                        StartTimeUtc = startLocal.ToUniversalTime(),
                        EndTimeUtc = endLocal.ToUniversalTime()
                    };

                    Task.Run(async () =>
                    {
                        var created = await _plugin.ServerClient.CreateEventAsync(newEvent);
                        _isPosting = false;
                        if (created != null)
                        {
                            _titleInput = string.Empty;
                            _descriptionInput = string.Empty;
                            _bannerUrlInput = string.Empty;
                            _statusMessage = "Event published successfully!";
                            RefreshEvents();
                        }
                        else
                        {
                            _statusMessage = "Failed to publish event. Please check your connection.";
                        }
                    });
                }
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), _statusMessage);
            }
        }

        private void DrawBannerImage(string url, float targetW = 560f, float maxH = 200f)
        {
            // Already cached?
            if (_bannerCache.TryGetValue(url, out var wrap))
            {
                if (wrap != null)
                {
                    float aspect = (float)wrap.Width / wrap.Height;
                    float drawW = targetW;
                    float drawH = drawW / aspect;
                    if (drawH > maxH)
                    {
                        drawH = maxH;
                        drawW = drawH * aspect;
                    }
                    float offsetX = Math.Max(0, (targetW - drawW) * 0.5f);
                    if (offsetX > 0)
                    {
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
                    }
                    ImGui.Image(wrap.Handle, new Vector2(drawW, drawH));
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "(Banner failed to load)");
                }
                return;
            }

            // Start async download if not already in progress
            if (_bannerLoading.TryAdd(url, true))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        byte[] imgBytes = await _bannerHttpClient.GetByteArrayAsync(url);
                        using var ms = new System.IO.MemoryStream(imgBytes);
                        using var bmp = new Bitmap(ms);

                        int w = bmp.Width;
                        int h = bmp.Height;

                        // Convert to BGRA32 raw pixel data
                        var bmpData = bmp.LockBits(
                            new Rectangle(0, 0, w, h),
                            ImageLockMode.ReadOnly,
                            PixelFormat.Format32bppArgb);
                        try
                        {
                            int bytes = Math.Abs(bmpData.Stride) * h;
                            var rawData = new byte[bytes];
                            Marshal.Copy(bmpData.Scan0, rawData, 0, bytes);

                            var tex = _plugin.TextureProvider.CreateFromRaw(
                                RawImageSpecification.Bgra32(w, h), rawData);
                            _bannerCache[url] = tex;
                        }
                        finally
                        {
                            bmp.UnlockBits(bmpData);
                        }
                    }
                    catch
                    {
                        _bannerCache[url] = null; // Mark as failed
                    }
                    finally
                    {
                        _bannerLoading.TryRemove(url, out _);
                    }
                });
            }

            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Loading banner...");
        }

        public void Dispose()
        {
            foreach (var kvp in _bannerCache)
            {
                kvp.Value?.Dispose();
            }
            _bannerCache.Clear();
            _bannerHttpClient.Dispose();
        }
    }
}
