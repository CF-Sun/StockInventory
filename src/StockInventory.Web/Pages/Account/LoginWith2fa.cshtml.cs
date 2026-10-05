using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StockInventory.Data;

namespace StockInventory.Web.Pages.Account;

public class LoginWith2faModel(SignInManager<AppUser> signIn) : PageModel
{
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public bool RememberDevice { get; set; }
    [BindProperty] public string? ReturnUrl { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        if (await signIn.GetTwoFactorAuthenticationUserAsync() is null) return RedirectToPage("Login");
        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null || !user.IsActive) return RedirectToPage("Login");

        var code = (Code ?? "").Replace(" ", "").Replace("-", "");
        var res = await signIn.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: RememberDevice);
        if (res.Succeeded)
            return LocalRedirect(!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
        if (res.IsLockedOut) return RedirectToPage("Login");
        Error = "驗證碼錯誤";
        Code = "";
        return Page();
    }
}
