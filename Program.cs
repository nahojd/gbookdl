using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace gbookdl
{
	class Program
	{
		const string baseAddress = "https://books.google.se";
		const string userAgent = "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:90.0) Gecko/20100101 Firefox/90.0";
		const string downloadTo = "downloads";
		static HttpClient httpClient;

		static async Task Main(string[] args)
		{
			if (args.Length == 0) {
				Console.WriteLine("Usage: dotnet run [id-of-book]");
				return;
			}
			var id = args[0];

			httpClient = new HttpClient(new HttpClientHandler {
				AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
			});
			httpClient.BaseAddress = new Uri(baseAddress);
			httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
			httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
			httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
			httpClient.DefaultRequestHeaders.Add("Accept-Language", "sv,en");
			httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
			httpClient.DefaultRequestHeaders.Add("DNT", "1");
			httpClient.DefaultRequestHeaders.Add("Host", httpClient.BaseAddress.Host);
			httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");

			await DownloadBook(id);
		}

		static async Task DownloadBook(string id)
		{
			await Init(id);

			Directory.CreateDirectory(downloadTo);

			var contentUrls = await GetContentUrls(id);

			var dir = Directory.CreateDirectory(Path.Combine(downloadTo, id));
			var index = 0;
			foreach(var url in contentUrls.Where(x => !string.IsNullOrWhiteSpace(x))) {
				var resource = $"{url}&w=1280"; //Get the large version
				var request = new HttpRequestMessage(HttpMethod.Get, resource);
				var response = await httpClient.SendAsync(request);

				var contentType = response.Content.Headers.ContentType.MediaType;
				if (response.IsSuccessStatusCode && (contentType == "image/png" || contentType == "image/jpeg") ) {
					var ext = contentType == "image/png" ? "png" : "jpg";
					using(
						Stream contentStream = await response.Content.ReadAsStreamAsync(),
						fileStream = new FileStream(Path.Combine(dir.FullName, $"page{index}.{ext}"), FileMode.Create, FileAccess.Write, FileShare.None, 10240, true)) {
							await contentStream.CopyToAsync(fileStream);
					}
					Console.WriteLine($"Saved page {index+1} of {contentUrls.Length}");
				}
				else {
					Console.WriteLine($"ERROR: {resource} ({response.StatusCode}, {response.Content.Headers.ContentType.MediaType})");
				}
				index++;
				await Wait();
			}
		}

		static async Task<string[]> GetContentUrls(string id) {
			var filename = Path.Combine(downloadTo, $"content-{id}.txt");
			if (File.Exists(filename))
			{
				using var reader = File.OpenText(filename);
				return reader.ReadToEnd().Split(Environment.NewLine).Select(x => x.Trim()).ToArray();
			}

			var pageDictionary = new Dictionary<string, string>();

			var firstPageData = await GetPageData(id, "PP1");
			foreach(var page in firstPageData)
				if (!pageDictionary.ContainsKey(page.Pid)) {
					Console.WriteLine($"Add {page.Pid}: {page.Src}");
					pageDictionary.Add(page.Pid, page.Src);
				}

			var idx = 0;
			while (true) {
				if (idx >= pageDictionary.Count) //Stop just in case somethings goes off the rails
					break;

				var pid = pageDictionary.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Value)).Key;
				if (pid is null)
					break;

				var pageData = await GetPageData(id, pid);
				foreach (var page in pageData.Where(x => !string.IsNullOrWhiteSpace(x.Src))) {
					Console.WriteLine($"Add {page.Pid}: {page.Src}");
					pageDictionary[page.Pid] = page.Src;
				}
				idx++;
			}

			//Save the content urls to a file
			using var fileStream = File.OpenWrite(filename);
			using var writer = new StreamWriter(fileStream);
			foreach(var page in pageDictionary) {
				Console.WriteLine($"{page.Key}: {page.Value}");
				writer.WriteLine(page.Value);
			}
			writer.Flush();
			writer.Close();

			return pageDictionary.Select(x => x.Value).ToArray();
		}

		static async Task Init(string id) {
			var resource = $"/books?id={id}&printsec=frontcover&hl=en";
			var request = new HttpRequestMessage(HttpMethod.Get, resource);
			var response = await httpClient.SendAsync(request);

			//Set cookies
			var cookieHeader = response.Headers.First(x => x.Key == "Set-Cookie");
			var cookies = cookieHeader.Value.Select(v => v.Substring(0, v.IndexOf(";")));
			httpClient.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));

			//Add referer
			httpClient.DefaultRequestHeaders.Add("Referer", $"{baseAddress}/{resource}");

			await Wait();
		}

		static async Task<Page[]> GetPageData(string id, string pid)
		{
			await Wait();
			var resource = $"/books?id={id}&lpg=PP1&hl=sv&pg={pid}&jscmd=click3";
			Console.WriteLine($"GET {resource}");
			var request = new HttpRequestMessage(HttpMethod.Get, resource);
			var response = await httpClient.SendAsync(request);

			var json = await response.Content.ReadAsStringAsync();
			var pages = JsonConvert.DeserializeObject<PageDataResponse>(json);
			return pages.Page;
		}

		static Random rnd = new Random();
		static Task Wait() {
			var delay = rnd.Next(2000);
			Console.WriteLine($"Wait for {delay} ms...");
			return Task.Delay(delay);
		}
	}

	public class PageDataResponse {
		public Page[] Page { get; set; }
	}

	public class Page {
		public string Pid { get; set; }
		public string Src { get; set; }
	}
}
