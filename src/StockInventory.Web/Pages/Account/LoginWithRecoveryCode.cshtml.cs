using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StockInventory.Data;

namespace StockInventory.Web.Pages.Account;

public class LoginWithRecoveryCodeModel(SignInManager<AppUser> signIn) : PageModel
{
    [BindProperty] public string RecoveryCode { get; set; } = "";
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

        var res = await signIn.TwoFactorRecoveryCodeSignInAsync((RecoveryCode ?? "").Replace(" ", ""));
        if (res.Succeeded)
            return LocalRedirect(!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
        if (res.IsLockedOut) return RedirectToPage("Login");
        Error = "還原碼錯誤";
        RecoveryCode = "";
        return Page();
    }
}
