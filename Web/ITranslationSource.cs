using System.Threading.Tasks;

namespace Airi.Web
{
    public interface ITranslationSource
    {
        string Name { get; }
        Task<string> TranslateAsync(string text, string targetLanguage);
    }
}
