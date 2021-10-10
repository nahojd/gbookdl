using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AngleSharp;
using CommandLine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;

namespace gbookdl
{
	public class Options
	{
		[Option(Default = true, HelpText = "Create cbz file")]
		public bool CreateCbz { get; set; }
		[Option(Default = true, HelpText = "Remove downloaded images after creating cbz")]
		public bool Cleanup { get; set; }
		[Option(Default = "downloads", HelpText = "Set output directory (default: downloads)")]
		public string Outdir { get; set; }
	}

	class Program
	{
		const string baseAddress = "https://books.google.se";
		const string userAgent = "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:90.0) Gecko/20100101 Firefox/90.0";
		static HttpClient httpClient;
		static bool cookieSet = false;

		static Options options;

		static ProgressContext progressContext;
		static ProgressTask initTask;
		static ProgressTask getUrlsTask;
		static ProgressTask saveUrlsTask;
		static ProgressTask downloadTask;
		static ProgressTask zipTask;
		static ProgressTask waitTask;


		static async Task Main(string[] args)
		{
			if (args.Length == 0)
			{
				Console.WriteLine("Usage: dotnet run [id-of-book / file-with-ids.txt]");
				return;
			}

			CommandLine.Parser.Default.ParseArguments<Options>(args)
				.WithParsed(opts => {
					options = opts;
				});

			InitHttpClient();
			var ids = GetBookIds(args);
			foreach(var id in ids) {
				await DownloadBook(id);
				if (id != ids.Last()) {
					AnsiConsole.MarkupLine("[yellow]Waiting 30 secs before downloading next book...[/]");
					await Task.Delay(TimeSpan.FromSeconds(30));
				}

			}

		}

		private static string[] GetBookIds(string[] args) {
			var arg = args.Last();
			if (File.Exists(arg)) {
				return ReadLinesFromFile(arg).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
			}
			return new[] { arg };
		}

		private static void InitHttpClient()
		{
			httpClient = new HttpClient(new HttpClientHandler
			{
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
		}

		private static string[] ReadLinesFromFile(string filename) {
			using var reader = File.OpenText(filename);
			return reader.ReadToEnd().Split(Environment.NewLine).Select(x => x.Trim()).ToArray();
		}

		static async Task DownloadBook(string id)
		{
			AnsiConsole.MarkupLine($"[yellow]Download book [/][bold yellow]{id}[/] [yellow]from[/] [bold yellow]{baseAddress}[/]");

			await AnsiConsole.Progress()
				.Columns(new ProgressColumn[]
				{
					new TaskDescriptionColumn(),    // Task description
					new ProgressBarColumn(),        // Progress bar
					new PercentageColumn(),         // Percentage
					new RemainingTimeColumn(),      // Remaining time
					new SpinnerColumn(),            // Spinner
				})
				.StartAsync(async ctx => {
					progressContext = ctx;

					initTask = ctx.AddTask("[green]Init[/]");
					getUrlsTask = ctx.AddTask("[green]Getting page urls[/]");
					saveUrlsTask = ctx.AddTask("[green]Saving page urls to file[/]");
					downloadTask = ctx.AddTask("[green]Downloading pages[/]");
					waitTask = ctx.AddTask("[red]Waiting a little[/]");


					var title = await Init(id);
					AnsiConsole.MarkupLine($"Title: [yellow]{title}[/]");

					if (options.CreateCbz)
						zipTask = ctx.AddTask($"[green]Creating {title}.cbz[/]");

					Directory.CreateDirectory(options.Outdir);

					var contentUrls = await GetContentUrls(id);

					var dir = Directory.CreateDirectory(Path.Combine(options.Outdir, title));
					var index = 0;
					var urls = contentUrls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
					downloadTask.Description = $"[green]Downloading {urls.Count} pages[/]";
					downloadTask.MaxValue = urls.Count;
					downloadTask.StartTask();
					foreach(var url in urls) {
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
						}
						else {
							AnsiConsole.MarkupLine($"[red]ERROR: {resource} ({response.StatusCode}, {response.Content.Headers.ContentType.MediaType})[/]");
						}
						index++;
						downloadTask.Increment(1);
						if (downloadTask.Value < downloadTask.MaxValue)
							await Wait();
					}
					downloadTask.StopTask();

					if (options.CreateCbz) {
						zipTask.StartTask();
						ZipFile.CreateFromDirectory(dir.FullName, Path.Combine(options.Outdir, $"{title}.cbz"), CompressionLevel.Optimal, false);
						zipTask.Value = zipTask.MaxValue;
						zipTask.StopTask();

						if (options.Cleanup) {
							dir.Delete(true);
							File.Delete(Path.Combine(options.Outdir, $"content-{id}.txt"));
						}

					}

				});

			AnsiConsole.MarkupLine("[green]Download finished[/]");
		}

		static async Task<string[]> GetContentUrls(string id) {
			getUrlsTask.StartTask();
			var filename = Path.Combine(options.Outdir, $"content-{id}.txt");
			if (File.Exists(filename))
			{
				var urls = ReadLinesFromFile(filename);
				getUrlsTask.Value(getUrlsTask.MaxValue);
				getUrlsTask.StopTask();
				saveUrlsTask.Value(saveUrlsTask.MaxValue);
				saveUrlsTask.StopTask();
				return urls;
			}

			var pageDictionary = new Dictionary<string, string>();

			var firstPageData = await GetPageData(id, "PP1");
			foreach(var page in firstPageData)
				if (!pageDictionary.ContainsKey(page.Pid)) {
					pageDictionary.Add(page.Pid, page.Src);
				}
			getUrlsTask.Description = $"[green]Getting {pageDictionary.Count} page urls[/]";
			getUrlsTask.MaxValue = pageDictionary.Count;
			getUrlsTask.Increment(pageDictionary.Count(x => !string.IsNullOrWhiteSpace(x.Value)));

			var idx = 0;
			while (true) {
				if (idx >= pageDictionary.Count) //Stop just in case somethings goes off the rails
					break;

				var pid = pageDictionary.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Value)).Key;
				if (pid is null)
					break;

				var pageData = await GetPageData(id, pid);
				foreach (var page in pageData.Where(x => !string.IsNullOrWhiteSpace(x.Src))) {
					if (string.IsNullOrWhiteSpace(pageDictionary[page.Pid])) {
						pageDictionary[page.Pid] = page.Src;
						getUrlsTask.Increment(1);
					}
				}
				idx++;
			}
			getUrlsTask.Value(getUrlsTask.MaxValue);
			getUrlsTask.StopTask();

			//Save the content urls to a file
			saveUrlsTask.MaxValue = pageDictionary.Count;
			saveUrlsTask.StartTask();
			using var fileStream = File.OpenWrite(filename);
			using var writer = new StreamWriter(fileStream);
			foreach(var page in pageDictionary) {
				saveUrlsTask.Increment(1);
				writer.WriteLine(page.Value);
			}
			writer.Flush();
			writer.Close();
			saveUrlsTask.Value = pageDictionary.Count;
			saveUrlsTask.StopTask();

			return pageDictionary.Select(x => x.Value).ToArray();
		}

		static async Task<string> Init(string id)
		{

			initTask.StartTask();
			initTask.MaxValue = 100;

			var resource = $"/books?id={id}&printsec=frontcover&hl=en";

			var request = new HttpRequestMessage(HttpMethod.Get, resource);
			var response = await httpClient.SendAsync(request);

			initTask.Value = 50;

			if (!response.IsSuccessStatusCode) {
				AnsiConsole.MarkupLine($"[red]INIT ERROR: {(int)response.StatusCode} {response.ReasonPhrase}[/]");
				throw new Exception("Init failed");
			}

			//Set cookies (only first time)
			if (!cookieSet) {
				var cookieHeader = response.Headers.First(x => x.Key == "Set-Cookie");
				var cookies = cookieHeader.Value.Select(v => v.Substring(0, v.IndexOf(";")));
				httpClient.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
				cookieSet = true;
			}


			initTask.Value = 70;

			//Set referer
			httpClient.DefaultRequestHeaders.Remove("Referer");
			httpClient.DefaultRequestHeaders.Add("Referer", $"{baseAddress}/{resource}");

			initTask.Value = 80;

			//Get book title
			var content = await response.Content.ReadAsStringAsync();
			var context = BrowsingContext.New(Configuration.Default);
			var document = await context.OpenAsync(req => req.Content(content));
			var titleHeading = document.QuerySelector(".gb-volume-title");
			var title = titleHeading?.TextContent ?? id;


			await Wait();
			initTask.Value = 100;
			initTask.StopTask();

			return title;
		}

		static async Task<Page[]> GetPageData(string id, string pid)
		{
			await Wait();
			var resource = $"/books?id={id}&lpg=PP1&hl=sv&pg={pid}&jscmd=click3";
			//Console.WriteLine($"GET {resource}");
			var request = new HttpRequestMessage(HttpMethod.Get, resource);
			var response = await httpClient.SendAsync(request);

			var json = await response.Content.ReadAsStringAsync();
			var pages = JsonConvert.DeserializeObject<PageDataResponse>(json);
			return pages.Page;
		}

		static Random rnd = new Random();
		static async Task Wait() {
			var delay = rnd.Next(2000);
			// waitTask = progressContext.AddTask()
			waitTask.Description = $"Wait {delay} ms";
			waitTask.MaxValue = delay;
			waitTask.Value = 0;
			if (!waitTask.IsStarted)
				waitTask.StartTask();
			//waitTask.
			var timeLeft = delay;
			while (timeLeft > 0) {
				var wait = Math.Min(200, timeLeft);
				timeLeft = timeLeft - wait;
				await Task.Delay(wait);
				waitTask.Increment(wait);
			}
			//waitTask.StopTask();
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
