using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;

namespace Coflnet.Sky.SkyAuctionTracker
{
    public class Program
    {
        public static void Main(string[] args)
        {
            double avg = 0;
            var penaltiy = Controllers.AnalyseController.GetPenalty(TimeSpan.FromMinutes(5),
                new List<(double TotalSeconds, TimeSpan age)>(){
                    (3,TimeSpan.FromMinutes(1)),
                    (4,TimeSpan.FromMinutes(1)),
                    (3,TimeSpan.FromMinutes(1))
                    }, ref avg);
            var penaltiyOld = Controllers.AnalyseController.GetPenalty(TimeSpan.FromMinutes(5),
                new List<(double TotalSeconds, TimeSpan age)>(){
                    (3,TimeSpan.FromMinutes(4)),
                    (4,TimeSpan.FromMinutes(4)),
                    (3,TimeSpan.FromMinutes(4))
                    }, ref avg);
            Console.WriteLine(penaltiyOld + " <- old penaltiy " + penaltiy);
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
