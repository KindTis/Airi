using System.IO;
using System.Text.Json;
using Airi.Infrastructure;

namespace Airi.Web;

internal static class MetadataSourceConfiguration
{
    internal const string FileName = "metadata-sources.local.json";

    internal static IWebVideoMetaSource[] Load(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            var options = JsonSerializer.Deserialize<Options>(input, JsonSerializerOptions.Web);
            if (options?.LibreDmmMoviesUrl is { } moviesUrl && IsHttpsEndpoint(moviesUrl)
                && options.JavBusBaseUrl is { } catalogUrl && IsHttpsEndpoint(catalogUrl))
            {
                return
                [
                    new LibreDmmMetaSource(moviesUrl),
                    new JavBusMetaSource(catalogUrl, uncensored: false),
                    new JavBusMetaSource(catalogUrl, uncensored: true)
                ];
            }

            AppLogger.Error($"웹 메타데이터 주소 설정을 확인하세요. {FileName}에 HTTPS 주소 두 개가 필요합니다.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            AppLogger.Info($"웹 메타데이터 조회 설정 파일이 없습니다. {FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Error($"웹 메타데이터 주소 설정을 읽을 수 없습니다. {FileName} ({ex.GetType().Name})");
        }

        return [];
    }

    private static bool IsHttpsEndpoint(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length != 0
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private sealed record Options(Uri? LibreDmmMoviesUrl, Uri? JavBusBaseUrl);
}
