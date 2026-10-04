using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InjuryReporting.Web.Controllers;

public class HomeController : AppController
{
    public IActionResult Index() => View();

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? code)
    {
        ViewData["Code"] = code;
        ViewData["RequestId"] = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        if (code is { } c) Response.StatusCode = c;
        return View();
    }
}
