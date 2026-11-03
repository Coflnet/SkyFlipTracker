using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using NUnit.Framework;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.Sky.SkyAuctionTracker.Services;

public class SwaggerTests
{
    [Test]
    public void InvestigationOperationsAreMarkedInternalForClientGeneration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebHostEnvironment>(new FakeEnv());
        services.AddLogging();
        services.AddControllers().AddApplicationPart(typeof(Startup).Assembly);
        services.AddSwaggerGen();
        services.ConfigureSwaggerGen(Startup.ConfigureSwagger);
        using var provider = services.BuildServiceProvider();

        var document = provider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        var investigation = document.Paths.Where(p => p.Key.StartsWith("/investigate/")).ToList();
        Assert.That(investigation, Has.Count.GreaterThanOrEqualTo(5), "investigation endpoints must stay reachable over HTTP and documented");
        foreach (var operation in investigation.SelectMany(p => p.Value.Operations.Values))
            Assert.That(operation.Extensions.ContainsKey("x-internal"), Is.True, $"{operation.OperationId} would be generated into the client");
        // everything else keeps being generated
        var others = document.Paths.Where(p => !p.Key.StartsWith("/investigate/")).SelectMany(p => p.Value.Operations.Values);
        Assert.That(others, Is.Not.Empty);
        Assert.That(others.Where(o => o.Extensions != null && o.Extensions.ContainsKey("x-internal")), Is.Empty);

        // the serialized document is what the client generator consumes
        using var writer = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(writer));
        Assert.That(writer.ToString(), Does.Contain("\"x-internal\": true"));
    }

    private class FakeEnv : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "SkyFlipTracker";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Production";
    }
}
