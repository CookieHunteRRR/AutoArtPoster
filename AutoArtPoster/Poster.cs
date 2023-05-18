using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net;

namespace AutoArtPoster
{
    internal class Poster
    {
        private HttpClient http;
        private string token;
        private string v;
        public string groupId;

        // MSK-3 для конверта в UTC
        private readonly HashSet<int> postingHours = new HashSet<int>() { 9, 13, 17 };

        private string currentDirectory;
        private string uploadFileName = "upload.txt";
        private List<string> savedPhotos;

        public Poster(HttpClient client)
        {
            http = client;
            currentDirectory = Directory.GetCurrentDirectory();

            LoadKeys();

            savedPhotos = new List<string>();
        }

        async public Task StartExecution()
        {
            string[] urls = GetUrlsFromUploadFile();

            await ExecuteAsync(urls);

            await DeleteTempFiles();

            Console.WriteLine("Работа скрипта завершена");
            Console.ReadKey(true);
        }

        async public Task ExecuteAsync(string[] imageUrls)
        {
            string uploadUrl = await GetUploadUrl();
            Console.WriteLine("Получен upload_url");

            await UploadImages(uploadUrl, imageUrls);

            await PostSavedImages();
        }

        public async Task DeleteTempFiles()
        {
            File.Delete($"{currentDirectory}/{uploadFileName}");
        }

        async public Task PostSavedImages()
        {
            var values = new Dictionary<string, string>();
            values.Add("owner_id", $"-{groupId}");
            values.Add("from_group", "1");
            values.Add("attachments", "");
            values.Add("publish_date", "");

            var latestPostponedPostInTicks = await GetLatestPostTime();

            var nextPublishDate = GetAppropriatePostTime(latestPostponedPostInTicks);
            foreach (var photoUrl in savedPhotos)
            {
                values["attachments"] = photoUrl;
                values["publish_date"] = nextPublishDate;

                var response = await CallVkMethod("wall.post", values);
                var postTime = DateTime.UnixEpoch.AddSeconds(Int32.Parse(nextPublishDate));
                Console.WriteLine($"Успешно создана отложенная запись на дату {postTime.Day}.{postTime.Month}.{postTime.Year} {postTime.Hour}:{postTime.Minute}");

                nextPublishDate = GetAppropriatePostTime(nextPublishDate);
                Thread.Sleep(500);
            }
        }

        private string GetAppropriatePostTime(string previousPost)
        {
            DateTime prevPost = DateTime.UnixEpoch.AddSeconds(Int32.Parse(previousPost));

            if (!postingHours.Contains(prevPost.Hour))
            {
                Console.WriteLine($"Последний пост в паблике отложен на {prevPost.Hour}:{prevPost.Minute} по UTC (MSK-3)");
                Console.WriteLine("Невозможно подобрать время для нового поста с таким временем последнего поста");
                Console.ReadKey(true);
                throw new Exception();
            }

            // В 20:00 по МСК
            if (prevPost.Hour == 17)
            {
                return ((DateTimeOffset)(prevPost.AddHours(16))).ToUnixTimeSeconds().ToString();
            }
            else
            {
                return ((DateTimeOffset)(prevPost.AddHours(4))).ToUnixTimeSeconds().ToString();
            }
        }

        async private Task<string> GetLatestPostTime()
        {
            // Определяем последний отложенный пост
            var values = new Dictionary<string, string>();
            values.Add("owner_id", $"-{groupId}");
            values.Add("filter", "postponed");
            values.Add("count", "1");
            var response = await CallVkMethod("wall.get", values);

            // Сначала делаем запрос ради получения количества постов в отложке
            var responseContent = await response.Content.ReadAsStringAsync();
            int postponedPostsCount = Int32.Parse(JsonSerializer.Deserialize<JsonElement>(responseContent)
                .GetProperty("response")
                .GetProperty("count")
                .ToString());

            // Затем добавляем оффсет, чтобы получить последний пост в отложке
            values.Add("offset", $"{postponedPostsCount - 1}");
            response = await CallVkMethod("wall.get", values);
            responseContent = await response.Content.ReadAsStringAsync();
            var latestDate = JsonSerializer.Deserialize<JsonElement>(responseContent)
                .GetProperty("response")
                .GetProperty("items")[0]
                .GetProperty("date")
                .ToString();

            return latestDate;
        }

        async public Task UploadImages(string uploadUrl, string[] imageUrls)
        {
            // до 6 изображений за раз (ограничение вк апи)
            var values = new Dictionary<string, string>();
            values.Add("group_id", groupId);
            values.Add("photo", "");
            values.Add("hash", "");
            values.Add("server", "");

            Console.WriteLine("Начало загрузки " + imageUrls.Length + " изображений");
            Console.Write("Изображений загружено: ");
            var cursorPos = Console.GetCursorPosition();
            int imageCount = 0;
            foreach (var imageUrl in imageUrls)
            {
                UploadedPhoto photoData;

                using (var multipartFormContent = new MultipartFormDataContent())
                {
                    var image = await http.GetAsync(imageUrl);
                    multipartFormContent.Add(image.Content, name: "file", fileName: "image.jpg");

                    var response = await http.PostAsync(uploadUrl, multipartFormContent);
                    photoData = GetUploadedPhoto(await response.Content.ReadAsStringAsync());
                }

                // Сохраняем загруженное изображение
                values["photo"] = photoData.photo;
                values["hash"] = photoData.hash;
                values["server"] = $"{photoData.server}";

                var saveResponse = await CallVkMethod("photos.saveWallPhoto", values);
                var savedPhotoUrl = GetSavedPhotoUrl(await saveResponse.Content.ReadAsStringAsync());
                savedPhotos.Add(savedPhotoUrl);

                imageCount++;
                Console.SetCursorPosition(cursorPos.Left, cursorPos.Top);
                Console.Write(imageCount);
            }
            Console.WriteLine();
            Console.WriteLine("Изображения загружены на сервер");
        }

        async public Task<string> GetUploadUrl()
        {
            var values = new Dictionary<string, string>();
            values.Add("group_id", groupId);
            var response = await CallVkMethod("photos.getWallUploadServer", values);

            var upload_url = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
                .GetProperty("response")
                .GetProperty("upload_url")
                .ToString();
            return upload_url;
        }

        public string[] GetUrlsFromUploadFile()
        {
            Console.WriteLine("Загрузка файла с ссылками на арты");
            if (!File.Exists(uploadFileName))
            {
                var file = File.Create(uploadFileName);
                file.Close();
                Console.WriteLine("Файл с ссылками не был обнаружен. Создан новый файл. Нажмите любую кнопку после заполнения файла");
                Console.ReadKey(true);
            }

            try
            {
                string[] urls = File.ReadAllLines(uploadFileName);
                if (urls.Length == 0) throw new Exception();
                return urls;
            }
            catch (Exception)
            {
                Console.WriteLine("Файла не существует, либо он пуст");
                Console.ReadKey(true);
                throw;
            }
        }

        public void LoadKeys()
        {
            KeysJson? keys;
            try
            {
                keys = JsonSerializer.Deserialize<KeysJson>(File.OpenRead(@$"{currentDirectory}/keys.json"));
            }
            catch (Exception)
            {
                Console.WriteLine("Не удалось десериализовать ключи либо открыть файл с ними");
                Console.ReadKey(true);
                throw;
            }

            token = keys.token;
            groupId = keys.groupId;
            v = keys.v;
        }

        /// <summary>
        /// Общий метод для вызова.
        /// В параметрах уже введен access_token и версия API. owner_id или group_id, как и прочие параметры нужно передавать в extraValues
        /// </summary>
        /// <param name="method">Вызываемый в VK API метод. Например, wall.get</param>
        /// <param name="extraValues">Параметры, которые войдут в POST-запрос. Например, filter=postponed</param>
        /// <returns>HttpResponseMessage, из которого можно достать JSON</returns>
        async public Task<HttpResponseMessage> CallVkMethod(string method, Dictionary<string, string> extraValues)
        {
            var values = new Dictionary<string, string>(extraValues);
            values.Add("access_token", token);
            values.Add("v", v);

            var content = new FormUrlEncodedContent(values);
            var response = await http.PostAsync($"https://api.vk.com/method/{method}?", content);

            // Проверка на ошибки
            var responseContent = await response.Content.ReadAsStringAsync();
            try
            {
                var error_code = JsonSerializer.Deserialize<JsonElement>(responseContent)
                    .GetProperty("error")
                    .GetProperty("error_code")
                    .ToString();
                var error_msg = JsonSerializer.Deserialize<JsonElement>(responseContent)
                    .GetProperty("error")
                    .GetProperty("error_msg")
                    .ToString();
                Console.WriteLine("VK API вернул ошибку " + error_code);
                Console.WriteLine(error_msg);
                Console.ReadKey(true);
                throw new Exception();
            }
            catch (KeyNotFoundException)
            {
                return response;
            }
            catch (Exception)
            {
                throw;
            }
        }

        private UploadedPhoto GetUploadedPhoto(string json)
        {
            UploadedPhoto response = JsonSerializer.Deserialize<UploadedPhoto>(json);
            return response;
        }

        private string GetSavedPhotoUrl(string json)
        {
            var owner_id = JsonSerializer.Deserialize<JsonElement>(json)
                .GetProperty("response")[0]
                .GetProperty("owner_id")
                .ToString();
            var id = JsonSerializer.Deserialize<JsonElement>(json)
                .GetProperty("response")[0]
                .GetProperty("id")
                .ToString();
            return $"photo{owner_id}_{id}";
        }
    }

    // загруженное на сервер но не добавленное в альбом
    public class UploadedPhoto
    {
        public int server { get; set; }
        public string photo { get; set; }
        public string hash { get; set; }
    }

    public class KeysJson
    {
        public string token { get; set; }
        public string groupId { get; set; }
        public string v { get; set; }
    }
}
