using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StockInventory.Data;

namespace StockInventory.Web.Pages.Account;

public class LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, ILogger<LoginModel> log,
    StockInventory.Quotes.LoginFailureAlerts alerts) : PageModel
{
    public const string GenericError = "帳號或密碼錯誤"; // SEC-07:不透露帳號是否存在、是否停用、是否鎖定

    [BindProperty] public string UserName { get; set; } = "";
    [BindProperty, DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty] public string? ReturnUrl { get; set; }
    public string? Error { get; set; }

    public void OnGet(string? returnUrl = null) => ReturnUrl = returnUrl;

    public async Task<IActionResult> OnPostAsync()
    {
        var user = string.IsNullOrWhiteSpace(UserName) ? null : await users.FindByNameAsync(UserName.Trim());
        if (user is null || !user.IsActive || string.IsNullOrEmpty(Password))
            return await FailAsync();

        var result = await signIn.PasswordSignInAsync(user, Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(Safe(ReturnUrl));
        if (result.RequiresTwoFactor) return RedirectToPage("LoginWith2fa", new { returnUrl = ReturnUrl });
        return await FailAsync();
    }

    private async Task<IActionResult> FailAsync()
    {
        await alerts.RecordFailureAsync(UserName, HttpContext.Connection.RemoteIpAddress?.ToString(), HttpContext.RequestAborted);
        log.LogInformation("登入失敗"); // 不記錄帳號與密碼
        Error = GenericError;
        Password = "";
        return Page();
    }

    internal string Safe(string? url) => !string.IsNullOrEmpty(url) && Url.IsLocalUrl(url) ? url : "/";
}
