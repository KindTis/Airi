using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Airi
{
    public class VideoMetaData
    {
        public string strTitle { get; set; } = string.Empty;
        public string strImagePath { get; set; } = string.Empty;
        public List<string> actors { get; set; } = new();
    }

    public class VideoMetaParser
    {
        private readonly HttpClient _httpClient;
        private readonly List<VideoMetaData> _videos;

        public VideoMetaParser()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Chrome/66.0.3359.181");
            _videos = new List<VideoMetaData>();
        }

        public async Task SearchAsync(string searchKeyword)
        {
            string videoUrl = "https://www.nanojav.com/jav/search/?q=" + Uri.EscapeDataString(searchKeyword);
            var (imgUrl, actorList) = await GetMetaDataAsync(videoUrl);

            if (string.IsNullOrEmpty(imgUrl))
            {
                return;
            }

            string sanitizedTitle = Regex.Replace(searchKeyword, @"[^\w\-]", "_");
            string downloadDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "thumb");
            Directory.CreateDirectory(downloadDirectory);

            string downloadPath = Path.Combine(downloadDirectory, sanitizedTitle + ".jpg");

            var video = new VideoMetaData
            {
                strTitle = searchKeyword,
                strImagePath = Path.Combine("thumb", sanitizedTitle + ".jpg"),
                actors = actorList
            };

            Console.WriteLine($"Downloading {imgUrl} -> {downloadPath}");

            try
            {
                await DownloadImageAsync(imgUrl, downloadPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Download Error: {ex.Message}");
                return;
            }

            _videos.Add(video);

            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Airi.json");
            try
            {
                string jsonData = JsonConvert.SerializeObject(_videos, Formatting.Indented);
                await File.WriteAllTextAsync(jsonPath, jsonData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"JSON Write Error: {ex.Message}");
            }
        }

        private async Task<(string imgUrl, List<string> actorList)> GetMetaDataAsync(string url)
        {
            Console.WriteLine($"Navigating to {url}");
            try
            {
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Request Error");
                    return (string.Empty, new List<string>());
                }

                string html = await response.Content.ReadAsStringAsync();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Searching for image URL
                var parentNode = doc.DocumentNode.SelectSingleNode("//div[@class='mb-5']");

                if (parentNode == null)
                {
                    Console.WriteLine("No results found");
                    return (string.Empty, new List<string>());
                }

                var imgNode = parentNode.SelectSingleNode(".//img[@class='cover']");
                string imgUrl = imgNode?.GetAttributeValue("src", string.Empty) ?? string.Empty;

                // Searching for actor list
                var actorNodes = doc.DocumentNode.SelectNodes("//div[@class='mb-2 buttons are-small']//a");
                var actorList = new List<string>();

                if (actorNodes != null)
                {
                    foreach (var actorNode in actorNodes)
                    {
                        string actor = Regex.Replace(actorNode.InnerText, @"\s+", string.Empty);
                        actorList.Add(actor);
                    }
                }

                return (imgUrl, actorList);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching metadata: {ex.Message}");
                return (string.Empty, new List<string>());
            }
        }

        private async Task DownloadImageAsync(string imageUrl, string imagePath)
        {
            try
            {
                var response = await _httpClient.GetAsync(imageUrl);

                if (response.IsSuccessStatusCode)
                {
                    await using var fs = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(fs);
                }
                else
                {
                    Console.WriteLine("Download Error: Unable to download image.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error downloading image: {ex.Message}");
            }
        }
    }
}

