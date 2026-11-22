using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game.Accounts;

namespace KSLauncher.App.Services;

/// <summary>Per-process Microsoft to Xbox/XSTS to Minecraft Java login.</summary>
public sealed class MicrosoftAccountService
{
    // Keep OAuth and game tokens in memory instead of CmlLib's plain JSON account store.
    private readonly JELoginHandler _handler = new JELoginHandlerBuilder()
        .WithAccountManager(new InMemoryXboxGameAccountManager(JEGameAccount.FromSessionStorage))
        .Build();

    public Task<MSession> SignInAsync() => _handler.AuthenticateInteractively();
}
