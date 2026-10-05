using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using StockInventory.Data;
using StockInventory.Web.Options;

namespace StockInventory.Web.Pages.Account.Manage;

public class PasswordModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, IOptions<AuthOptions> auth) : PageModel
{
    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    public List<string> Errors { get; } = [];
    public bool MustChange { get; private set; }
    public int MinLength => auth.Value.MinPasswordLength;

    public async Task OnGetAsync() => MustChange = (await users.GetUserAsync(User))?.MustChangePassword ?? false;

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Account/Login");
        MustChange = user.MustChangePassword;

        if (NewPassword != ConfirmPassword) { Errors.Add("兩次輸入的新密碼不一致"); return Page(); }
        var res = await users.ChangePasswordAsync(user, CurrentPassword, NewPassword);
        if (!res.Succeeded)
        {
            Errors.AddRange(res.Errors.Select(e => e.Code switch
            {
                "PasswordMismatch" => "目前密碼錯誤",
                "PasswordTooShort" => $"新密碼至少需要 {MinLength} 個字元",
                _ => "新密碼不符合規則",
            }));
            return Page();
        }

        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await signIn.RefreshSignInAsync(user);
        return RedirectToPage(user.TwoFactorEnabled ? "/Index" : "/Account/Manage/2fa");
    }
}
