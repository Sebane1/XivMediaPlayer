namespace XivMediaPlayer.Localization
{
    /// <summary>
    /// Short helper for UI string localization.
    /// </summary>
    public static class Translation
    {
        public static string Get(string text)
        {
            if (TranslationGuard.ShouldSkipTranslation(text))
            {
                return text;
            }

            return Translator.LocalizeUI(text);
        }

        public static string Get(string text, LanguageEnum sourceLanguage) => Translator.LocalizeUI(text, sourceLanguage);

        public static string Format(string format, params object[] args) => string.Format(Get(format), args);

        /// <summary>
        /// Preserves ImGui ID suffix after ## (e.g. "General##tab" -> translated + "##tab").
        /// </summary>
        public static string ImGuiLabel(string labelWithOptionalId) => Translator.LocalizeUI(labelWithOptionalId);
    }
}
