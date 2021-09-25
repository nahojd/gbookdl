﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;

namespace gbookdl
{
	class Program
	{
		const string baseAddress = "https://books.google.se";
		const string userAgent = "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:90.0) Gecko/20100101 Firefox/90.0";
		const string downloadTo = "downloads";
		static HttpClient httpClient;

		static ProgressContext progressContext;
		static ProgressTask initTask;
		static ProgressTask getUrlsTask;
		static ProgressTask saveUrlsTask;
		static ProgressTask downloadTask;
		static ProgressTask waitTask;


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

					await Init(id);

					Directory.CreateDirectory(downloadTo);

					var contentUrls = await GetContentUrls(id);

					var dir = Directory.CreateDirectory(Path.Combine(downloadTo, id));
					var index = 0;
					var urls = contentUrls.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
					downloadTask.Description = $"Downloading {urls.Count} pages";
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
							//Console.WriteLine($"Saved page {index+1} of {contentUrls.Length}");
						}
						else {
							AnsiConsole.MarkupLine($"[red]ERROR: {resource} ({response.StatusCode}, {response.Content.Headers.ContentType.MediaType})[/]");
						}
						index++;
						downloadTask.Increment(1);
						await Wait();
					}
					downloadTask.StopTask();
				});

			AnsiConsole.MarkupLine("[green]Download finished[/]");
		}

		static async Task<string[]> GetContentUrls(string id) {
			getUrlsTask.StartTask();
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
					//Console.WriteLine($"Add {page.Pid}: {page.Src}");
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
					//Console.WriteLine($"Add {page.Pid}: {page.Src}");
					pageDictionary[page.Pid] = page.Src;
					getUrlsTask.Increment(1);
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
				//Console.WriteLine($"{page.Key}: {page.Value}");
				saveUrlsTask.Increment(1);
				writer.WriteLine(page.Value);
			}
			writer.Flush();
			writer.Close();
			saveUrlsTask.StopTask();

			return pageDictionary.Select(x => x.Value).ToArray();
		}

		static async Task Init(string id) {

			initTask.StartTask();
			initTask.MaxValue = 100;

			var resource = $"/books?id={id}&printsec=frontcover&hl=en";
			var request = new HttpRequestMessage(HttpMethod.Get, resource);
			var response = await httpClient.SendAsync(request);

			initTask.Value = 60;

			if (!response.IsSuccessStatusCode) {
				AnsiConsole.MarkupLine($"[red]INIT ERROR: {(int)response.StatusCode} {response.ReasonPhrase}[/]");
				throw new Exception("Init failed");
			}

			//Set cookies
			var cookieHeader = response.Headers.First(x => x.Key == "Set-Cookie");
			var cookies = cookieHeader.Value.Select(v => v.Substring(0, v.IndexOf(";")));
			httpClient.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));

			initTask.Value = 80;

			//Add referer
			httpClient.DefaultRequestHeaders.Add("Referer", $"{baseAddress}/{resource}");

			initTask.Value = 90;

			await Wait();
			initTask.Value = 100;
			initTask.StopTask();
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
