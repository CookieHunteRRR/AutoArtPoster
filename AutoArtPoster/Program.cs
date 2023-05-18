using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AutoArtPoster
{
    internal class Program
    {
        private static readonly HttpClient http = new HttpClient();

        static async Task Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var poster = new Poster(http);
            await poster.StartExecution();

            http.Dispose();
        }
    }
}