using KaiZent.Platform.SampleWeb.Components;
using KaiZent.Platform.Web;
using KaiZent.Platform.Web.Services;

namespace KaiZent.Platform.SampleWeb;

public sealed class WebProgram
{
    public static readonly TextCatalog Texts = new(new Dictionary<string, (string, string)>
    {
        ["home.hello"] = ("Hallo {0}", "Hello {0}"),
    });

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents();
        builder.AddKaiZentWeb(o =>
        {
            o.ProductName = "KaiZent Beispiel";
            o.ProductShort = "Beispiel";
            o.TaglineDe = "Beispiel-App der Plattform";
            o.TaglineEn = "Platform sample app";
            o.CookiePrefix = "kzs";
        }, Texts);

        var app = builder.Build();
        app.UseKaiZentWeb();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapKaiZentAccount();
        app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(KaiZentWebOptions).Assembly);
        app.Run();
    }
}
