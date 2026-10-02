using System;
using System.Reflection;
using DOL.Language;
using DOL.GS.ServerProperties;

namespace DOL.UnitTests
{
    // Isolated tests need SkillBase's static property names, not disk language
    // files or a live server. Restore the previous language state afterward.
    internal sealed class PetTestLanguageScope : IDisposable
    {
        private static readonly PropertyInfo Translations = typeof(LanguageMgr).GetProperty(nameof(LanguageMgr.Translations));
        private readonly object _previous = Translations.GetValue(null);
        private readonly string _previousLanguage = Properties.SERV_LANGUAGE;

        public PetTestLanguageScope()
        {
            Properties.SERV_LANGUAGE ??= "EN";
            if (_previous == null)
                Translations.SetValue(null, Activator.CreateInstance(Translations.PropertyType));
        }

        public void Dispose()
        {
            Translations.SetValue(null, _previous);
            Properties.SERV_LANGUAGE = _previousLanguage;
        }
    }
}
