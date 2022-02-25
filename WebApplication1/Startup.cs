using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace WebApplication1
{
    public partial class Startup
    {
        public IConfiguration Configuration { get; }
        public IWebHostEnvironment Environment { get; }

        public Startup(IConfiguration configuration, IWebHostEnvironment environment)
        {
            Configuration = configuration;
            Environment = environment;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddControllersWithViews(options =>
                {
                    var policy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .Build();

                    options.Filters.Add(new AuthorizeFilter(policy));
                    options.Filters.Add<AutoValidateAntiforgeryTokenAttribute>();
                });


            // Add the reverse proxy to capability to the server
            var proxyBuilder = services.AddReverseProxy();
            // Initialize the reverse proxy from the "ReverseProxy" section of configuration
            proxyBuilder.LoadFromConfig(Configuration.GetSection("ReverseProxy"));



            services.Configure<ForwardedHeadersOptions>(opt =>
            {
                opt.ForwardedHeaders = ForwardedHeaders.XForwardedProto;

                // Alleen de proto header wordt overgenomen
                opt.KnownNetworks.Clear();
                opt.KnownProxies.Clear();
            });

            // Used for proxying calls to the API
            services.AddHttpContextAccessor();
            services.AddHttpClient();

            // Used by Kubernetes
            services.AddHealthChecks();

            services.AddHttpForwarder();

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IAntiforgery antiforgery, IHttpForwarder forwarder)
        {
            app.UseForwardedHeaders();

            if (Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStaticFiles();
            app.UseCookiePolicy();

            app.UseStatusCodePagesWithReExecute("/errorstatus/{0}");

            app.UseRouting();
            app.UseCors();

            app.UseAuthentication();
            app.UseAuthorization();

            var httpClient = new HttpMessageInvoker(new SocketsHttpHandler()
            {
                UseProxy = true,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            });
            var transformer = new CustomTransformer();
            var requestConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };

            app.UseEndpoints(endpoints =>
            {
                // my frontend will send the application insights data to /track
                endpoints.Map("/track", async httpContext =>
                {
                    // forwarding to application insights https://dc.services.visualstudio.com/v2
                    var error = await forwarder.SendAsync(httpContext, "https://dc.services.visualstudio.com/v2",
                        httpClient, requestConfig, transformer);

                    if (error != ForwarderError.None)
                    {
                        var errorFeature = httpContext.GetForwarderErrorFeature();
                        var exception = errorFeature?.Exception;
                        throw new Exception(exception?.Message);
                    }

                });
                endpoints.MapReverseProxy(options =>
                {
                    endpoints.MapReverseProxy();
                });

                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller}/{action=Index}/{id?}");

                endpoints.MapControllers();
                endpoints.MapHealthChecks("/health");
            });
        }
    }


    public class CustomTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext,
            HttpRequestMessage proxyRequest, string destinationPrefix)
        {
            var body = "";
            using (var streamReader = new StreamReader(httpContext.Request.Body))
            {
                body = await streamReader.ReadToEndAsync();
            }

            // replace fake Instrumentation Key ("%TEMP%") with the real Instrumentation Key
            body = body.Replace("%TEMP%", "application insights InstrumentationKey here");
            var content = new StringContent(body, Encoding.UTF8, "text/html");
            httpContext.Request.ContentLength = body.Length;
            httpContext.Request.Body?.Dispose();
            httpContext.Request.Body = await content.ReadAsStreamAsync();
            

            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix);
            var queryContext = new QueryTransformContext(httpContext.Request);
            proxyRequest.RequestUri = new Uri(destinationPrefix + httpContext.Request.Path + queryContext.QueryString);
            proxyRequest.Headers.Host = null;
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse)
        {
            if (proxyResponse == null)
            {
                return false;
            }
            return await base.TransformResponseAsync(httpContext, proxyResponse);
        }
    }
}

