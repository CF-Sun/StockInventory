using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;
using StockInventory.Data;

namespace StockInventory.Web.Pages.Account.Manage;

public class TwoFactorModel(UserManager<AppUser> users, SignInManager<AppUser> signIn) : PageModel
{
    [BindProperty] public string Code { get; set; } = "";
    public bool Enabled { get; private set; }
    public string SharedKey { get; private set; } = "";
    public string QrSvg { get; private set; } = "";
    public string[]? RecoveryCodes { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Account/Login");
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return RedirectToPage("/Account/Login");
        if (user.TwoFactorEnabled) { await LoadAsync(user); return Page(); }

        var code = (Code ?? "").Replace(" ", "").Replace("-", "");
        var ok = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!ok)
        {
            Error = "驗證碼錯誤,請重新輸入";
            Code = "";
            await LoadAsync(user);
            return Page();
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        // 還原碼只在此刻顯示一次,不存入 TempData 或日誌
        RecoveryCodes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        await signIn.RefreshSignInAsync(user);
        Enabled = true;
        return Page();
    }

    private async Task LoadAsync(AppUser user)
    {
        Enabled = user.TwoFactorEnabled;
        if (Enabled) return;
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        SharedKey = string.Join(' ', Enumerable.Range(0, (key!.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
        var label = Uri.EscapeDataString(user.UserName!);
        var uri = string.Format(CultureInfo.InvariantCulture,
            "otpauth://totp/StockInventory:{0}?secret={1}&issuer=StockInventory&digits=6", label, key);
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        QrSvg = new SvgQRCode(data).GetGraphic(4);
    }
}
