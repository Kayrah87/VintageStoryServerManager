using System.Net;
using Microsoft.Extensions.Caching.Memory;
using VintageStoryServerManager.Core.ModDb;
using VintageStoryServerManager.Infrastructure.ModDb;

namespace VintageStoryServerManager.Tests.Infrastructure;

public sealed class ModDbClientTests
{
    private const string BrowseJson = """
        {"statuscode":"200","mods":[
          {"modid":792,"assetid":3829,"downloads":1228946,"follows":8605,"trendingpoints":1,"comments":1537,"name":"BetterRuins",
           "summary":"Ruins","modidstrs":["betterruins"],"author":"NiclAss","urlalias":"betterruins","side":"both","type":"mod",
           "logo":"https://moddbcdn.vintagestory.at/br.png","tags":["Worldgen"],"lastreleased":"2026-09-20 20:42:49"},
          {"modid":5,"name":"No logo","modidstrs":[],"side":"client","type":"externaltool","logo":null,"tags":[]}
        ]}
        """;

    private const string ModJson = """
        {"statuscode":"200","mod":{"modid":792,"name":"BetterRuins","author":"NiclAss","urlalias":"betterruins","side":"both",
          "logofile":"https://moddbcdn.vintagestory.at/br_480_320.png","homepageurl":"https://discord.gg/x","sourcecodeurl":"javascript:alert(1)",
          "wikiurl":"","downloads":10,"follows":2,"tags":["Worldgen","Ruins"],
          "screenshots":[{"mainfile":"https://moddbcdn.vintagestory.at/9.jpg","thumbnailfilename":"https://moddbcdn.vintagestory.at/9_55_60.jpg"}],
          "releases":[{"releaseid":1,"mainfile":"https://x/br.zip","filename":"br.zip","modidstr":"betterruins","modversion":"1.0.0","tags":["1.22.7"]}]}}
        """;

    [Fact]
    public async Task Parses_browse_results_and_caches_identical_queries()
    {
        var handler = new CountingHandler(BrowseJson);
        var client = Create(handler);
        var query = new ModDbQuery("ruins", ModDbSort.MostDownloaded);

        var first = await client.BrowseAsync(query);
        var second = await client.BrowseAsync(query);

        Assert.Single(handler.Requests);
        Assert.Same(first, second);
        var ruins = first[0];
        Assert.Equal("betterruins", ruins.Key);
        Assert.Equal("https://moddbcdn.vintagestory.at/br.png", ruins.Logo);
        Assert.Equal(8605, ruins.Follows);
        Assert.Equal("mod", ruins.Type);
        Assert.Null(first[1].Logo);
        Assert.Equal("5", first[1].Key);
    }

    [Fact]
    public async Task Parses_mod_details_with_images_and_only_safe_links()
    {
        var client = Create(new CountingHandler(ModJson));

        var mod = await client.GetModAsync("betterruins");

        Assert.Equal("https://moddbcdn.vintagestory.at/br_480_320.png", mod!.Logo);
        Assert.Equal("https://moddbcdn.vintagestory.at/9_55_60.jpg", Assert.Single(mod.Screenshots).ThumbnailUrl);
        Assert.Equal("https://discord.gg/x", mod.Links.Homepage);
        Assert.Null(mod.Links.Source);
        Assert.Null(mod.Links.Wiki);
        Assert.Equal(["Worldgen", "Ruins"], mod.Tags);
    }

    private static ModDbClient Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://mods.test/") }, new MemoryCache(new MemoryCacheOptions()));

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
