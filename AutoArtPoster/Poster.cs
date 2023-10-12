using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AutoArtPoster
{
    internal class Poster
    {
        private HttpClient _http;
        private Config _config;
        private string _currentDirectory;
        private List<string> _savedPhotos;

        // Часы, на которые откладывать посты по UTC (MSK-3)
        private HashSet<int> _postingHours = new HashSet<int>();
        private readonly string _configFileName = "config.json";
        private readonly string _uploadFileName = "upload.txt";

        public Poster(HttpClient client)
        {
            _http = client;
            _currentDirectory = Directory.GetCurrentDirectory();
            _savedPhotos = new List<string>();

            CreateConfigIfNotExist();
            _config = LoadKeysFromConfig();
            CheckConfigTimeValidity();
        }

        async public Task StartExecution()
        {
            await ValidateAccessToken();
            string[] urls = GetUrlsFromUploadFile();

            await ExecuteAsync(urls);

            DeleteTempFiles();
            Console.WriteLine("Работа скрипта завершена");
        }

        async private Task ExecuteAsync(string[] imageUrls)
        {
            string uploadUrl = await GetUploadUrl();
            await UploadImages(uploadUrl, imageUrls);
            await PostSavedImages();
        }

        async private Task UploadImages(string uploadUrl, string[] imageUrls)
        {
            var values = new Dictionary<string, string>
            {
                { "group_id", _config.groupId },
                { "photo", "" },
                { "hash", "" },
                { "server", "" }
            };

            Console.WriteLine("Начало загрузки " + imageUrls.Length + " изображений");
            Console.Write("Изображений загружено: ");
            var cursorPos = Console.GetCursorPosition();
            int imageCount = 0;
            foreach (var imageUrl in imageUrls)
            {
                UploadedPhoto photoData;

                using (var multipartFormContent = new MultipartFormDataContent())
                {
                    var image = await _http.GetAsync(imageUrl);
                    multipartFormContent.Add(image.Content, name: "file", fileName: "image.jpg");

                    var response = await _http.PostAsync(uploadUrl, multipartFormContent);
                    photoData = GetUploadedPhoto(await response.Content.ReadAsStringAsync());
                }

                // Сохраняем информацию о загруженном изображении
                values["photo"] = photoData.photo;
                values["hash"] = photoData.hash;
                values["server"] = $"{photoData.server}";

                var saveResponse = await CallVkMethod("photos.saveWallPhoto", values);
                var savedPhotoUrl = GetSavedPhotoUrl(await saveResponse.Content.ReadAsStringAsync());
                _savedPhotos.Add(savedPhotoUrl);

                imageCount++;
                Console.SetCursorPosition(cursorPos.Left, cursorPos.Top);
                Console.Write(imageCount);
            }
            Console.WriteLine();
            Console.WriteLine("Изображения загружены на сервер");
        }

        async private Task PostSavedImages()
        {
            var values = new Dictionary<string, string>
            {
                { "owner_id", $"-{_config.groupId}" },
                { "from_group", "1" },
                { "attachments", "" },
                { "publish_date", "" }
            };

            var latestPostponedPostInTicks = await GetLatestPostTime();

            var nextPublishDate = GetAppropriatePostTime(latestPostponedPostInTicks);
            foreach (var photoUrl in _savedPhotos)
            {
                values["attachments"] = photoUrl;
                values["publish_date"] = nextPublishDate;

                var response = await CallVkMethod("wall.post", values);
                
                var postTime = DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(Int32.Parse(nextPublishDate)), DateTimeKind.Utc);
                var localPostTime = postTime.ToLocalTime();
                Console.WriteLine($"Успешно создана отложенная запись на дату {postTime.Day}.{postTime.Month}.{postTime.Year} {GetNiceNumber(postTime.Hour)}:{GetNiceNumber(postTime.Minute)} UTC\n" +
                    $"Local: {localPostTime.Day}.{localPostTime.Month}.{localPostTime.Year} {GetNiceNumber(localPostTime.Hour)}:{GetNiceNumber(localPostTime.Minute)}");

                nextPublishDate = GetAppropriatePostTime(nextPublishDate);
                // Небольшая задержка, чтобы API не послал меня куда подальше из-за спама
                Thread.Sleep(500);
            }
        }

        private string GetAppropriatePostTime(string previousPost)
        {
            DateTime prevPost = DateTime.UnixEpoch.AddSeconds(Int32.Parse(previousPost));

            if (!_postingHours.Contains(prevPost.Hour))
            {
                Console.WriteLine($"Последний пост в паблике отложен на {prevPost.Hour}:{prevPost.Minute} по UTC (MSK-3)");
                Console.WriteLine("Невозможно подобрать время для нового поста с таким временем последнего поста");
                throw new Exception();
            }

            // В финальный час для постинга
            if (prevPost.Hour == _config.utcFinalHour)
            {
                // Создаем пост в указанное стартовое время следующего дня
                var hoursTillNextDay = 24 - prevPost.Hour + _config.utcStartingHour;
                return ((DateTimeOffset)prevPost.AddHours(hoursTillNextDay)).ToUnixTimeSeconds().ToString();
            }
            else
            {
                // Создаем пост через указанный промежуток от предыдущего поста
                return ((DateTimeOffset)prevPost.AddHours(_config.interval)).ToUnixTimeSeconds().ToString();
            }
        }

        async private Task<string> GetLatestPostTime()
        {
            // Определяем последний отложенный пост
            var values = new Dictionary<string, string>
            {
                { "owner_id", $"-{_config.groupId}" },
                { "filter", "postponed" },
                { "count", "1" }
            };
            var response = await CallVkMethod("wall.get", values);

            // Сначала делаем запрос ради получения количества постов в отложке
            var responseContent = await response.Content.ReadAsStringAsync();
            int postponedPostsCount = Int32.Parse(JsonSerializer.Deserialize<JsonElement>(responseContent)
                .GetProperty("response")
                .GetProperty("count")
                .ToString());

            if (postponedPostsCount < 1)
            {
                return GetLatestAppropriateTimeToday();
            }

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

        // Возвращаем сегодняшнюю дату в последний час для постинга
        private string GetLatestAppropriateTimeToday()
        {
            // доходим до ровных чисел
            var now = DateTimeOffset.UtcNow;
            now = now.AddSeconds(60 - now.Second);
            now = now.AddMinutes(60 - now.Minute);
            long nowAsUnix = now.ToUnixTimeSeconds();
            if (now.Hour == _config.utcFinalHour) return nowAsUnix.ToString();
            int diff;
            if (now.Hour > _config.utcFinalHour)
                diff = now.Hour - _config.utcFinalHour;
            else
                diff = _config.utcFinalHour - now.Hour;
            return (nowAsUnix + (3600 * diff)).ToString();
        }

        async private Task<string> GetUploadUrl()
        {
            var values = new Dictionary<string, string>
            {
                { "group_id", _config.groupId }
            };
            var response = await CallVkMethod("photos.getWallUploadServer", values);

            var upload_url = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
                .GetProperty("response")
                .GetProperty("upload_url")
                .ToString();

            Console.WriteLine("Получен upload_url");
            return upload_url;
        }

        private string[] GetUrlsFromUploadFile()
        {
            Console.WriteLine("Загрузка файла с ссылками на арты");
            if (!File.Exists(_uploadFileName))
            {
                var file = File.Create(_uploadFileName);
                file.Close();
                Console.WriteLine("Файл с ссылками не был обнаружен. Создан новый файл. Нажмите любую кнопку после заполнения файла");
                Console.ReadKey(true);
            }

            try
            {
                string[] urls = File.ReadAllLines(_uploadFileName);
                if (urls.Length == 0) throw new Exception();
                return urls;
            }
            catch (Exception)
            {
                Console.WriteLine("Файла не существует, либо он пуст");
                throw;
            }
        }

        /// <summary>
        /// Общий метод для вызова.
        /// В параметрах уже введен access_token и версия API. owner_id или group_id, как и прочие параметры нужно передавать в extraValues
        /// </summary>
        /// <param name="method">Вызываемый в VK API метод. Например, wall.get</param>
        /// <param name="extraValues">Параметры, которые войдут в POST-запрос. Например, filter=postponed</param>
        /// <returns>HttpResponseMessage, из которого можно достать JSON</returns>
        async private Task<HttpResponseMessage> CallVkMethod(string method, Dictionary<string, string> extraValues)
        {
            var values = new Dictionary<string, string>(extraValues)
            {
                { "access_token", _config.token },
                { "v", _config.v }
            };

            var content = new FormUrlEncodedContent(values);
            var response = await _http.PostAsync($"https://api.vk.com/method/{method}?", content);

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
                throw new VkApiException(error_code, error_msg);
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

        private void CheckConfigTimeValidity()
        {
            if (_config.utcStartingHour < 0 || _config.utcStartingHour > 23)
            {
                Console.WriteLine("Стартовый час не может быть меньше 0 и больше 23");
                throw new Exception();
            }
            if (_config.utcFinalHour < 0 || _config.utcFinalHour > 23)
            {
                Console.WriteLine("Финальный час не может быть меньше 0 и больше 23");
                throw new Exception();
            }
            if (_config.utcFinalHour <= _config.utcStartingHour)
            {
                // Ну вообще может в теории, но мне-то зачем так заморачиваться
                Console.WriteLine("Финальный час не может быть меньше или равен стартовому часу");
                throw new Exception();
            }
            if ((_config.utcFinalHour - _config.utcStartingHour) % _config.interval != 0)
            {
                Console.WriteLine("С выбранным интервалом невозможно будет запостить в финальный час");
                throw new Exception();
            }

            _postingHours.Add(_config.utcStartingHour);
            int nextHour = _config.utcStartingHour + _config.interval;
            while (nextHour != _config.utcFinalHour)
            {
                _postingHours.Add(nextHour);
                nextHour += _config.interval;
            }
            _postingHours.Add(_config.utcFinalHour);
        }

        private Config LoadKeysFromConfig()
        {
            Config keys;

            try
            {
                var fs = File.OpenRead(@$"{_currentDirectory}\{_configFileName}");
                keys = JsonSerializer.Deserialize<Config>(fs);
                fs.Close();
            }
            catch (Exception)
            {
                Console.WriteLine("Не удалось десериализовать ключи либо открыть файл с ними");
                throw;
            }

            return keys;
        }

        private void CreateConfigIfNotExist()
        {
            if (File.Exists(@$"{_currentDirectory}/{_configFileName}"))
                return;

            Config _data = new Config
            {
                utcStartingHour = 9,
                utcFinalHour = 17,
                interval = 4,
                clientId = "",
                token = "",
                groupId = "",
                v = "5.131",
            };
            
            string json = JsonSerializer.Serialize(_data);
            File.WriteAllText(@$"{_currentDirectory}/{_configFileName}", json);

            Console.WriteLine("Конфигурационный файл не был обнаружен. Создан новый файл. Нажмите любую кнопку после заполнения файла");
            Console.ReadKey(true);
        }

        async private Task ValidateAccessToken()
        {
            var values = new Dictionary<string, string>
            {
                { "owner_id", $"-{_config.groupId}" },
                { "count", "1" }
            };

            try
            {
                var response = await CallVkMethod("wall.get", values);
                return;
            }
            catch (VkApiException ex)
            {
                if (ex.ErrorCode == "5")
                {
                    GetAccessToken();
                }
                else
                {
                    throw;
                }
            }
        }

        private void GetAccessToken()
        {
            var urlToGetToken = @$"https://oauth.vk.com/authorize?client_id={_config.clientId}&display=page&redirect_uri=https://oauth.vk.com/blank.html&scope=groups,wall,photos&response_type=token&v={_config.v}";
            Process.Start("explorer.exe", $"\"{urlToGetToken}\"");

            Console.WriteLine("Введите полученный access_token:");
            string input = Console.ReadLine();

            // т.к. конфиг уже загружен, заменяем его и в коде, и в файле
            _config.token = input;
            File.WriteAllText(@$"{_currentDirectory}/{_configFileName}", JsonSerializer.Serialize(_config));
            Console.WriteLine("Access token обновлен");
        }

        private string GetNiceNumber(int numberToFix)
        {
            if (numberToFix < 10) return $"0{numberToFix}";
            return numberToFix.ToString();
        }

        private void DeleteTempFiles()
        {
            File.Delete(@$"{_currentDirectory}/{_uploadFileName}");
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

    // загруженное на сервер фото как данные, необходимые для добавления в альбом
    public class UploadedPhoto
    {
        public int server { get; set; }
        public string photo { get; set; }
        public string hash { get; set; }
    }

    public class Config
    {
        public int utcStartingHour { get; set; }
        public int utcFinalHour { get; set; }
        public int interval { get; set; }
        public string clientId { get; set; }
        public string token { get; set; }
        public string groupId { get; set; }
        public string v { get; set; }
    }

    public class VkApiException : Exception
    {
        public string ErrorCode;
        public string ErrorMsg;

        public VkApiException(string code, string msg) : base($"VK API вернул ошибку {code}: {msg}")
        {
            ErrorCode = code;
            ErrorMsg = msg;
        }
    }
}
