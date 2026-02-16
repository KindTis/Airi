using System.Threading;
using System.Threading.Tasks;
using Airi.Services;
using Xunit;

namespace Airi.Tests
{
    public sealed class NullTranslationServiceTests
    {
        [Fact]
        public async Task TranslateAsync_ReturnsOriginalText_AndIsDisabled()
        {
            var service = NullTranslationService.Instance;

            var translated = await service.TranslateAsync("sample text", null, "KO", CancellationToken.None);

            Assert.False(service.IsEnabled);
            Assert.Equal("sample text", translated);
        }
    }
}
