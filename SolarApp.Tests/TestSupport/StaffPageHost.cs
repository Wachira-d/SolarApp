using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SolarApp.Data;
using SolarApp.Models;

namespace SolarApp.Tests.TestSupport;

/// <summary>
/// Hosts the real Razor pages behind a test server, signed in as an Admin
/// that exists in the Identity store, so a test can GET a page, fill its form
/// like a browser without JavaScript would, and POST it back with the
/// antiforgery token — exercising model binding exactly as production does.
/// </summary>
public sealed class StaffPageHost : IAsyncDisposable
{
    public const string AdminId = "test-admin";

    private readonly WebApplication _app;
    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;
    public string DbName { get; }

    private StaffPageHost(WebApplication app, string dbName)
    {
        _app = app;
        DbName = dbName;
        Client = app.GetTestClient();
    }

    public ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(DbName).Options);

    public static async Task<StaffPageHost> StartAsync(string dbName, Action<IServiceCollection>? services = null)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SolarApp"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(SolarApp.Pages.Installations.EditModel).Assembly.GetName().Name,
            ContentRootPath = contentRoot,
            EnvironmentName = "Development",
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddIdentity<ApplicationUser, IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = "Test";
            o.DefaultAuthenticateScheme = "Test";
            o.DefaultChallengeScheme = "Test";
        }).AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(SolarApp.Pages.Installations.EditModel).Assembly);
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<SolarApp.Services.Security.ICredentialProtector, SolarApp.Services.Security.CredentialProtector>();
        builder.Services.AddScoped<SolarApp.Services.Interfaces.IInstallationService, SolarApp.Services.Implementations.InstallationService>();
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        await app.StartAsync();

        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roles.RoleExistsAsync("Admin")) await roles.CreateAsync(new IdentityRole("Admin"));
            if (await users.FindByIdAsync(AdminId) == null)
            {
                var admin = new ApplicationUser { Id = AdminId, UserName = "admin@test", Email = "admin@test" };
                await users.CreateAsync(admin);
                await users.AddToRoleAsync(admin, "Admin");
            }
        }
        return new StaffPageHost(app, dbName);
    }

    /// <summary>GET a page and return its first POST form's fields, the antiforgery cookie and the HTML.</summary>
    public async Task<PageForm> GetFormAsync(string url)
    {
        var res = await Client.GetAsync(url);
        var html = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"GET {url} returned {(int)res.StatusCode}: {html[..Math.Min(1500, html.Length)]}");
        var cookie = res.Headers.TryGetValues("Set-Cookie", out var c)
            ? string.Join("; ", c.Select(v => v.Split(';')[0]))
            : "";
        return new PageForm(url, html, ParseFirstPostForm(html), cookie);
    }

    public async Task<HttpResponseMessage> PostAsync(PageForm form, string? handler = null)
    {
        var url = handler == null ? form.Url : $"{form.Url}{(form.Url.Contains('?') ? "&" : "?")}handler={handler}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form.Fields.SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v)))),
        };
        if (form.Cookie.Length > 0) req.Headers.Add("Cookie", form.Cookie);
        return await Client.SendAsync(req);
    }

    /// <summary>What a browser without JavaScript would submit from the page's first method="post" form.</summary>
    internal static Dictionary<string, List<string>> ParseFirstPostForm(string html)
    {
        var start = Regex.Match(html, "<form[^>]*method=\"post\"[^>]*>", RegexOptions.IgnoreCase);
        if (!start.Success) throw new InvalidOperationException("No POST form on the page");
        var end = html.IndexOf("</form>", start.Index, StringComparison.OrdinalIgnoreCase);
        var body = html.Substring(start.Index, end - start.Index);

        var fields = new Dictionary<string, List<string>>();
        void Add(string name, string value)
        {
            if (!fields.TryGetValue(name, out var list)) fields[name] = list = new List<string>();
            list.Add(value);
        }
        static Dictionary<string, string> Attrs(string tag)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(tag, "([\\w:-]+)(?:=\"([^\"]*)\")?"))
                d[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value);
            return d;
        }

        foreach (Match m in Regex.Matches(body, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var a = Attrs(m.Value);
            if (!a.TryGetValue("name", out var name) || a.ContainsKey("disabled")) continue;
            var type = a.TryGetValue("type", out var t) ? t.ToLowerInvariant() : "text";
            if (type is "submit" or "button" or "file" or "image" or "reset") continue;
            if (type is "checkbox" or "radio")
            {
                if (a.ContainsKey("checked")) Add(name, a.TryGetValue("value", out var cv) && cv.Length > 0 ? cv : "on");
                continue;
            }
            Add(name, a.TryGetValue("value", out var v) ? v : "");
        }
        foreach (Match m in Regex.Matches(body, "<select\\b([^>]*)>(.*?)</select>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var a = Attrs(m.Groups[1].Value);
            if (!a.TryGetValue("name", out var name) || a.ContainsKey("disabled")) continue;
            var options = Regex.Matches(m.Groups[2].Value, "<option\\b([^>]*)>(.*?)</option>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(o => (Attrs: Attrs(o.Groups[1].Value), Text: WebUtility.HtmlDecode(o.Groups[2].Value.Trim()))).ToList();
            var chosen = options.FirstOrDefault(o => o.Attrs.ContainsKey("selected"));
            if (chosen.Attrs == null && options.Count > 0) chosen = options[0];
            if (chosen.Attrs != null) Add(name, chosen.Attrs.TryGetValue("value", out var ov) ? ov : chosen.Text);
        }
        foreach (Match m in Regex.Matches(body, "<textarea\\b([^>]*)>(.*?)</textarea>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var a = Attrs(m.Groups[1].Value);
            if (a.TryGetValue("name", out var name) && !a.ContainsKey("disabled"))
                Add(name, WebUtility.HtmlDecode(m.Groups[2].Value).TrimStart('\r', '\n'));
        }
        return fields;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private sealed class TestAuth : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : base(o, l, e) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, AdminId), new Claim(ClaimTypes.Name, "admin@test"),
                new Claim(ClaimTypes.Role, "Admin"),
            }, "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
        }
    }
}

public sealed record PageForm(string Url, string Html, Dictionary<string, List<string>> Fields, string Cookie)
{
    public PageForm Set(string name, string value)
    {
        Fields[name] = new List<string> { value };
        return this;
    }

    public string? Get(string name) => Fields.TryGetValue(name, out var v) ? v.LastOrDefault() : null;
}
