﻿
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeatherHttp
{
    public class WebApplicationHostBuilder : IHostBuilder
    {
        private readonly IHostBuilder _hostBuilder;
        private string[] _urls;

        public WebApplicationHostBuilder() : this(new HostBuilder())
        {

        }

        public WebApplicationHostBuilder(IHostBuilder hostBuilder)
        {
            _hostBuilder = hostBuilder;

            Services = new ServiceCollection();

            // HACK: MVC and Identity do this horrible thing to get the hosting environment as an instance
            // from the service collection before it is built. That needs to be fixed...
            Environment = new WebHostEnvironment();
            Services.AddSingleton(Environment);

            // REVIEW: Since the configuration base is tied to the content root, it needs to be specified as part of 
            // builder creation. It's not changing in the current design.
            Configuration = new ConfigurationBuilder().SetBasePath(Environment.ContentRootPath);
            HostConfiguration = new ConfigurationBuilder().SetBasePath(Environment.ContentRootPath);
            Logging = new LoggingBuilder(Services);
        }

        public IWebHostEnvironment Environment { get; }

        public IServiceCollection Services { get; }

        public IConfigurationBuilder Configuration { get; }

        public IConfigurationBuilder HostConfiguration { get; }

        public ILoggingBuilder Logging { get; }

        public IDictionary<object, object> Properties => _hostBuilder.Properties;

        public WebApplicationHost Build()
        {
            WebApplicationHost webAppHost = null;

            _hostBuilder.ConfigureWebHostDefaults(web =>
            {
                if (_urls != null)
                {
                    web.UseUrls(_urls);
                }

                web.Configure(app =>
                {
                    var hasRoutesWithoutRouteMiddleware = false;

                    // The endpoints were already added on the outside
                    if (webAppHost.DataSources.Count > 0)
                    {
                        // The user did not register the routing middleware
                        if (webAppHost.RouteBuilder == null)
                        {
                            hasRoutesWithoutRouteMiddleware = true;

                            // Add the routing middleware first
                            app.UseRouting();

                            // Copy the routes over
                            var routes = (IEndpointRouteBuilder)app.Properties[WebApplicationHost.EndpointRouteBuilder];
                            foreach (var ds in webAppHost.DataSources)
                            {
                                routes.DataSources.Add(ds);
                            }
                        }
                        else
                        {
                            foreach (var ds in webAppHost.DataSources)
                            {
                                webAppHost.RouteBuilder.DataSources.Add(ds);
                            }

                            webAppHost.UseEndpoints(_ => { });
                        }
                    }

                    ApplyApplicationBuilder(webAppHost, (ApplicationBuilder)app, hasRoutesWithoutRouteMiddleware);
                });
            });

            _hostBuilder.ConfigureServices(services =>
            {
                foreach (var s in Services)
                {
                    services.Add(s);
                }
            });

            _hostBuilder.ConfigureAppConfiguration(builder =>
            {
                foreach (var s in Configuration.Sources)
                {
                    builder.Sources.Add(s);
                }
            });

            _hostBuilder.ConfigureHostConfiguration(builder =>
            {
                foreach (var s in HostConfiguration.Sources)
                {
                    builder.Sources.Add(s);
                }
            });

            var host = _hostBuilder.Build();

            return webAppHost = new WebApplicationHost(host);
        }

        public void UseUrls(params string[] urls)
        {
            _urls = urls;
        }

        private static void ApplyApplicationBuilder(WebApplicationHost source, ApplicationBuilder dest, bool hasRoutesWithoutRouteMiddleware)
        {
            var sourceComponents = GetComponentList(source.ApplicationBuilder);
            var destComponents = GetComponentList(dest);

            foreach (var item in source.Properties)
            {
                dest.Properties[item.Key] = item.Value;
            }

            // Add the middleware entries
            foreach (var ds in sourceComponents)
            {
                destComponents.Add(ds);
            }

            // Add the endpoint middleware
            if (hasRoutesWithoutRouteMiddleware)
            {
                dest.UseEndpoints(e => { });
            }
        }

        private static IList<Func<RequestDelegate, RequestDelegate>> GetComponentList(ApplicationBuilder application)
        {
            // HACK: We need to get the list of middleware from the application builder so it can be mutated
            return (IList<Func<RequestDelegate, RequestDelegate>>)typeof(ApplicationBuilder)
                    .GetField("_components", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(application);
        }

        IHost IHostBuilder.Build()
        {
            return _hostBuilder.Build();
        }

        public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureAppConfiguration(configureDelegate);
        }

        public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureContainer(configureDelegate);
        }

        public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureHostConfiguration(configureDelegate);
        }

        public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            return _hostBuilder.ConfigureServices(configureDelegate);
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        {
            return _hostBuilder.UseServiceProviderFactory(factory);
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
        {
            return _hostBuilder.UseServiceProviderFactory(factory);
        }

        private class LoggingBuilder : ILoggingBuilder
        {
            public LoggingBuilder(IServiceCollection services)
            {
                Services = services;
            }

            public IServiceCollection Services { get; }
        }

        private class WebHostEnvironment : IWebHostEnvironment
        {
            public WebHostEnvironment()
            {
                WebRootPath = "wwwroot";
                ContentRootPath = Directory.GetCurrentDirectory();
                ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
                var webRoot = Path.Combine(ContentRootPath, WebRootPath);
                WebRootFileProvider = Directory.Exists(webRoot) ? (IFileProvider)new PhysicalFileProvider(webRoot) : new NullFileProvider();
                ApplicationName = Assembly.GetEntryAssembly().GetName().Name;
            }

            public IFileProvider WebRootFileProvider { get; set; }
            public string WebRootPath { get; set; }
            public string ApplicationName { get; set; }
            public IFileProvider ContentRootFileProvider { get; set; }
            public string ContentRootPath { get; set; }
            public string EnvironmentName { get; set; }
        }
    }

    public class WebApplicationHost : IHost, IApplicationBuilder, IEndpointRouteBuilder
    {
        public const string EndpointRouteBuilder = "__EndpointRouteBuilder";

        private readonly IHost _host;
        private readonly List<EndpointDataSource> _dataSources = new List<EndpointDataSource>();

        public WebApplicationHost(IHost host)
        {
            _host = host;
            ApplicationBuilder = new ApplicationBuilder(host.Services);
            Logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(Environment.ApplicationName);
        }

        public IServiceProvider Services => _host.Services;

        public IConfiguration Configuration => _host.Services.GetRequiredService<IConfiguration>();

        public IWebHostEnvironment Environment => _host.Services.GetRequiredService<IWebHostEnvironment>();

        public ILogger Logger { get; }

        public IFeatureCollection ServerFeatures => _host.Services.GetRequiredService<IServer>().Features;

        public IServiceProvider ApplicationServices { get => ApplicationBuilder.ApplicationServices; set => ApplicationBuilder.ApplicationServices = value; }

        public IDictionary<string, object> Properties => ApplicationBuilder.Properties;

        public ICollection<EndpointDataSource> DataSources => _dataSources;

        internal IEndpointRouteBuilder RouteBuilder
        {
            get
            {
                Properties.TryGetValue(EndpointRouteBuilder, out var value);
                return (IEndpointRouteBuilder)value;
            }
        }

        internal ApplicationBuilder ApplicationBuilder { get; }

        public IServiceProvider ServiceProvider => Services;

        public static WebApplicationHostBuilder CreateDefaultBuilder(string[] args)
        {
            return new WebApplicationHostBuilder(Host.CreateDefaultBuilder(args));
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return _host.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return _host.StopAsync(cancellationToken);
        }

        public void Dispose()
        {
            _host.Dispose();
        }

        public RequestDelegate Build()
        {
            return ApplicationBuilder.Build();
        }

        public IApplicationBuilder New()
        {
            return ApplicationBuilder.New();
        }

        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
        {
            ApplicationBuilder.Use(middleware);
            return this;
        }

        public IApplicationBuilder CreateApplicationBuilder() => New();
    }
}
