using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XivMediaPlayer.Localization
{
    /// <summary>
    /// UI translation via RoleplayingQuestCore-compatible proxy.
    /// Requests are serialized through a single queue so only one hits the server at a time.
    /// </summary>
    public static class Translator
    {
        private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, string>> Dictionary = new();
        private static readonly ConcurrentQueue<TranslationWorkItem> WorkQueue = new();
        private static readonly ConcurrentDictionary<string, byte> PendingKeys = new();

        private static readonly HttpClient HttpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private static readonly string[] LanguageStrings =
        {
            "English",
            "Français",
            "Deutsch",
            "日本語",
            "中文",
            "한국어",
            "Svenska",
            "Español"
        };

        private static LanguageEnum _uiLanguage = LanguageEnum.English;
        private static string _cacheLocation = string.Empty;
        private static string _serverUrl = XivMediaPlayer.Configuration.DefaultTranslationServerUrl;

        private static int _queueWorkerRunning;
        private static int _activeRequestCount;

        public static string ServerUrl
        {
            get => _serverUrl;
            set => _serverUrl = NormalizeServerUrl(value);
        }

        public static string ServerUrlDisplay => _serverUrl;

        public static string[] LanguageStringsDisplay => LanguageStrings;

        public static LanguageEnum UiLanguage
        {
            get => _uiLanguage;
            set => _uiLanguage = value;
        }

        public static string CacheLocation
        {
            get => _cacheLocation;
            set => _cacheLocation = value;
        }

        public static event EventHandler<Exception>? OnError;
        public static event EventHandler? OnTranslationEvent;

        public static string LastErrorMessage { get; private set; } = string.Empty;
        public static bool ServerRespondedSuccessfully { get; private set; }
        public static int PendingRequestCount => WorkQueue.Count + _activeRequestCount;

        public static int GetCachedCount(LanguageEnum language)
        {
            int languageId = (int)language;
            return Dictionary.TryGetValue(languageId, out var bucket) ? bucket.Count : 0;
        }

        public static void ClearLastError() => LastErrorMessage = string.Empty;

        public static async Task ProbeServerAsync()
        {
            ClearLastError();
            ServerRespondedSuccessfully = false;
            try
            {
                await LocalizeTextAsync("General", _uiLanguage, LanguageEnum.English);
                ServerRespondedSuccessfully = string.IsNullOrEmpty(LastErrorMessage);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        public static void LoadCache(string cacheLocation)
        {
            _cacheLocation = cacheLocation;
            if (!File.Exists(_cacheLocation))
            {
                return;
            }

            try
            {
                var loaded = JsonConvert.DeserializeObject<ConcurrentDictionary<int, ConcurrentDictionary<string, string>>>(
                    File.ReadAllText(_cacheLocation));
                if (loaded != null)
                {
                    Dictionary.Clear();
                    foreach (var pair in loaded)
                    {
                        Dictionary[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(null, ex);
            }
        }

        public static async Task<string> LocalizeTextAsync(string translationValue, LanguageEnum userLanguage, LanguageEnum textLanguage)
        {
            if (userLanguage == textLanguage || string.IsNullOrEmpty(translationValue))
            {
                return translationValue;
            }

            if (TranslationGuard.ShouldSkipTranslation(translationValue))
            {
                return translationValue;
            }

            int languageId = (int)userLanguage;
            string? cachedTranslation = GetCachedTranslation(translationValue, languageId);
            if (!string.IsNullOrWhiteSpace(cachedTranslation))
            {
                return cachedTranslation;
            }

            return await EnqueueAndWaitAsync(translationValue, userLanguage, textLanguage);
        }

        public static string LocalizeUI(string translationText)
        {
            return LocalizeUI(translationText, LanguageEnum.English);
        }

        public static string LocalizeUI(string translationText, LanguageEnum textLanguage)
        {
            LanguageEnum userLanguage = _uiLanguage;
            try
            {
                if (userLanguage == textLanguage || string.IsNullOrEmpty(translationText))
                {
                    return translationText;
                }

                string[] uiText = translationText.Split("##");
                string translationValue = uiText.Length > 1 ? uiText[0] : translationText;

                if (TranslationGuard.ShouldSkipTranslation(translationValue))
                {
                    return translationText;
                }

                int languageId = (int)_uiLanguage;

                if (string.IsNullOrEmpty(translationValue))
                {
                    return translationText;
                }

                string? cachedTranslation = GetCachedTranslation(translationValue, languageId);
                if (cachedTranslation != null)
                {
                    return cachedTranslation + (uiText.Length > 1 ? "##" + uiText[1] : "");
                }

                EnqueueFireAndForget(translationValue, userLanguage, textLanguage);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(null, ex);
            }

            return translationText;
        }

        public static string[] LocalizeTextArray(string[] strings)
        {
            return strings.Select(s => LocalizeUI(AddSpacesToSentence(s, true))).ToArray();
        }

        private sealed class TranslationWorkItem
        {
            public required string Text { get; init; }
            public required LanguageEnum UserLanguage { get; init; }
            public required LanguageEnum TextLanguage { get; init; }
            public TaskCompletionSource<string>? Completion { get; init; }
        }

        private static void EnqueueFireAndForget(string text, LanguageEnum userLanguage, LanguageEnum textLanguage)
        {
            if (!TryEnqueue(new TranslationWorkItem
            {
                Text = text,
                UserLanguage = userLanguage,
                TextLanguage = textLanguage,
            }))
            {
                return;
            }

            EnsureQueueWorkerRunning();
        }

        private static Task<string> EnqueueAndWaitAsync(string text, LanguageEnum userLanguage, LanguageEnum textLanguage)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryEnqueue(new TranslationWorkItem
            {
                Text = text,
                UserLanguage = userLanguage,
                TextLanguage = textLanguage,
                Completion = completion,
            }))
            {
                return WaitForCachedTranslationAsync(text, (int)userLanguage, text);
            }

            EnsureQueueWorkerRunning();
            return completion.Task;
        }

        private static async Task<string> WaitForCachedTranslationAsync(string text, int languageId, string fallback)
        {
            for (int i = 0; i < 600; i++)
            {
                string? cached = GetCachedTranslation(text, languageId);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }

                if (!PendingKeys.ContainsKey(MakePendingKey(languageId, text)))
                {
                    break;
                }

                await Task.Delay(100);
            }

            return GetCachedTranslation(text, languageId) ?? fallback;
        }

        private static bool TryEnqueue(TranslationWorkItem item)
        {
            string key = MakePendingKey((int)item.UserLanguage, item.Text);
            if (!PendingKeys.TryAdd(key, 0))
            {
                return false;
            }

            WorkQueue.Enqueue(item);
            return true;
        }

        private static void EnsureQueueWorkerRunning()
        {
            if (Interlocked.CompareExchange(ref _queueWorkerRunning, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(ProcessQueueAsync);
        }

        private static async Task ProcessQueueAsync()
        {
            try
            {
                while (WorkQueue.TryDequeue(out TranslationWorkItem? item))
                {
                    Interlocked.Increment(ref _activeRequestCount);
                    string key = MakePendingKey((int)item.UserLanguage, item.Text);
                    try
                    {
                        string result = await FetchTranslationFromServerAsync(item.Text, item.UserLanguage, item.TextLanguage);
                        item.Completion?.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        ReportError(ex);
                        item.Completion?.TrySetResult(item.Text);
                    }
                    finally
                    {
                        PendingKeys.TryRemove(key, out _);
                        Interlocked.Decrement(ref _activeRequestCount);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _queueWorkerRunning, 0);
                if (!WorkQueue.IsEmpty)
                {
                    EnsureQueueWorkerRunning();
                }
            }
        }

        private static async Task<string> FetchTranslationFromServerAsync(string translationValue, LanguageEnum userLanguage, LanguageEnum textLanguage)
        {
            int languageId = (int)userLanguage;

            string? cachedTranslation = GetCachedTranslation(translationValue, languageId);
            if (!string.IsNullOrWhiteSpace(cachedTranslation))
            {
                return cachedTranslation;
            }

            var languageRequest = new LanguageRequest
            {
                Language = userLanguage,
                TextLanguage = textLanguage,
                TranslationText = translationValue,
            };

            using var post = await HttpClient.PostAsync(
                _serverUrl,
                new StringContent(JsonConvert.SerializeObject(languageRequest), Encoding.UTF8, "application/json"));

            if (!post.IsSuccessStatusCode)
            {
                ReportError(new HttpRequestException($"Translation server returned HTTP {(int)post.StatusCode} ({post.StatusCode})."));
                return translationValue;
            }

            string value = NormalizeResponse(await post.Content.ReadAsStringAsync());
            if (string.IsNullOrWhiteSpace(value))
            {
                ReportError(new InvalidOperationException("Translation server returned an empty response."));
                return translationValue;
            }

            if (!Dictionary.ContainsKey(languageId))
            {
                Dictionary[languageId] = new ConcurrentDictionary<string, string>();
            }

            Dictionary[languageId][translationValue] = value;
            ClearLastError();
            ServerRespondedSuccessfully = true;
            OnTranslationEvent?.Invoke(null, EventArgs.Empty);

            try
            {
                if (!string.IsNullOrEmpty(_cacheLocation))
                {
                    await File.WriteAllTextAsync(_cacheLocation, JsonConvert.SerializeObject(Dictionary, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(null, ex);
            }

            return value.CleanPunctuation();
        }

        private static string MakePendingKey(int languageId, string text) => languageId + "\0" + text;

        private static string NormalizeResponse(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            {
                try
                {
                    return JsonConvert.DeserializeObject<string>(value) ?? value;
                }
                catch
                {
                    return value.Trim('"');
                }
            }

            return value;
        }

        private static void ReportError(Exception ex)
        {
            LastErrorMessage = ex.Message;
            OnError?.Invoke(null, ex);
        }

        private static string NormalizeServerUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return XivMediaPlayer.Configuration.DefaultTranslationServerUrl;
            }

            url = url.Trim().TrimEnd('/');
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "http://" + url;
            }

            return url;
        }

        private static string? GetCachedTranslation(string translationValue, int languageId)
        {
            if (!Dictionary.ContainsKey(languageId))
            {
                Dictionary[languageId] = new ConcurrentDictionary<string, string>();
            }

            if (Dictionary[languageId].TryGetValue(translationValue, out string? value))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.CleanPunctuation();
                }

                Dictionary[languageId].TryRemove(translationValue, out _);
            }

            return null;
        }

        private static string CleanPunctuation(this string value)
        {
            return value.Replace(" !", "!", StringComparison.Ordinal)
                .Replace(" ?", "?", StringComparison.Ordinal)
                .Replace(" .", ".", StringComparison.Ordinal)
                .Replace(" :", ":", StringComparison.Ordinal);
        }

        private static string AddSpacesToSentence(string text, bool preserveAcronyms)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var newText = new StringBuilder(text.Length * 2);
            newText.Append(text[0]);
            for (int i = 1; i < text.Length; i++)
            {
                if (char.IsUpper(text[i]))
                {
                    if ((text[i - 1] != ' ' && !char.IsUpper(text[i - 1]))
                        || (preserveAcronyms && char.IsUpper(text[i - 1])
                            && i < text.Length - 1 && !char.IsUpper(text[i + 1])))
                    {
                        newText.Append(' ');
                    }
                }

                newText.Append(text[i]);
            }

            return newText.ToString();
        }
    }
}
